import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash } from 'node:crypto';
import { readFile, writeFile, copyFile, readdir, realpath, mkdir } from 'node:fs/promises';
import { join, dirname } from 'node:path';
import backendHost from '../desktop/runtime/backend-host.cjs';
import { resolvePnpmCommand } from './pnpm-command.mjs';
import { buildShmBridge, readPeExportsAndImports } from '../native/shm-preview/build.mjs';

const execute = promisify(execFile);
const hash = async file => createHash('sha256').update(await readFile(file)).digest('hex');
const inside = (root, file) => backendHost.isInsideRoot(root, file);

async function step(command, args, cwd, env = process.env, spawnOptions = {}) {
  await new Promise((done, reject) => {
    const child = spawn(command, args, { ...spawnOptions, cwd, env, stdio: 'inherit', windowsHide: true });
    const forward = signal => {
      try { child.kill(signal); } catch { /* ignore already-exited child */ }
    };
    process.on('SIGINT', forward);
    process.on('SIGTERM', forward);
    const cleanup = () => {
      process.off('SIGINT', forward);
      process.off('SIGTERM', forward);
    };
    child.once('error', error => { cleanup(); reject(error); });
    child.once('exit', (code, signal) => {
      cleanup();
      code === 0 ? done() : reject(new Error(`${command} failed (${code ?? signal})`));
    });
  });
}

export async function nativeSources(root) {
  const { stdout } = await execute(
    'git',
    ['ls-files', '-co', '--exclude-standard', '--', 'native/gpu-preview', 'native/shm-preview', 'native/skia'],
    { cwd: root },
  );
  const files = [...new Set(stdout.trim().split(/\r?\n/).filter(Boolean))].sort();
  const result = {};
  for (const file of files) result[file] = await hash(join(root, file));
  return result;
}

async function linuxSymbols(file, names) {
  const { stdout } = await execute('nm', ['-D', '--defined-only', file], { maxBuffer: 8 * 1024 * 1024 });
  const exported = new Set(stdout.split('\n').map(line => line.trim().split(/\s+/).at(-1)?.split('@')[0]));
  for (const name of names) {
    if (!exported.has(name)) throw new Error(`Required native ABI symbol missing: ${name}`);
  }
}

export async function buildDesktopNative(root) {
  const rid = backendHost.platformRid();
  root = await realpath(root);
  const sourceInputs = await nativeSources(root);

  const pnpmSetup = resolvePnpmCommand(['--dir', 'desktop', 'run', 'setup']);
  await step(pnpmSetup.executable, pnpmSetup.args, root, process.env, pnpmSetup.spawnOptions);

  let artifacts;
  if (process.platform === 'linux') {
    await step('python3', ['native/skia/bootstrap.py'], root);
    await step('bash', ['native/skia/build-native.sh'], root);
    const { stdout } = await execute('bash', ['native/gpu-preview/bootstrap-headers.sh'], { cwd: root, maxBuffer: 1024 * 1024 });
    const headers = await realpath(stdout.trim());
    if (!inside(root, headers)) throw new Error('Native headers escaped App');
    await step('bash', ['native/gpu-preview/build.sh'], root, { ...process.env, VULKAN_INCLUDE_DIR: headers });
    await step('bash', ['native/shm-preview/build.sh'], root);
    if (JSON.stringify(await nativeSources(root)) !== JSON.stringify(sourceInputs)) {
      throw new Error('Native source changed during build; preserve output and rebuild from a stable slice');
    }
    artifacts = {
      'libSkiaSharp.so': join(root, '.local/skia-p0/libSkiaSharp.so'),
      'libmako_gpu_preview.so': join(root, 'native/gpu-preview/build/libmako_gpu_preview.so'),
      'gpu_preview_broker.node': join(root, 'native/gpu-preview/build/gpu_preview_broker.node'),
      'shm_bridge.node': join(root, 'native/shm-preview/build/shm_bridge.node'),
    };
    for (const file of Object.values(artifacts)) {
      if (!inside(root, await realpath(file))) throw new Error('Native artifact escaped App');
    }
    await linuxSymbols(artifacts['libSkiaSharp.so'], ['gr_mako_direct_context_flush_surface_vk_state', 'sk_mako_surface_get_vk_image_info']);
    await linuxSymbols(artifacts['libmako_gpu_preview.so'], ['mkg_gpu_pool_create_borrowed', 'mkg_gpu_pool_consumer_released']);
    await linuxSymbols(artifacts['shm_bridge.node'], ['napi_register_module_v1']);
  } else if (process.platform === 'win32') {
    // Windows uses official SkiaSharp.NativeAssets.Win32 + MSVC zero-import-library shm_bridge.node.
    const builtShmAddon = buildShmBridge(join(root, 'native/shm-preview'));
    if (JSON.stringify(await nativeSources(root)) !== JSON.stringify(sourceInputs)) {
      throw new Error('Native source changed during build; preserve output and rebuild from a stable slice');
    }
    artifacts = {
      'shm_bridge.node': builtShmAddon,
    };
    for (const file of Object.values(artifacts)) {
      if (!inside(root, await realpath(file))) throw new Error('Native artifact escaped App');
    }
    const { exports, imports } = readPeExportsAndImports(artifacts['shm_bridge.node']);
    if (!exports.includes('napi_register_module_v1')) {
      throw new Error('Required Windows PE ABI symbol missing: napi_register_module_v1');
    }
    if (imports.some(dll => /^(node|electron)\.(exe|dll)$/i.test(dll))) {
      throw new Error(`Windows shm_bridge.node must not statically link host runtime: ${imports.join(', ')}`);
    }
  } else {
    throw new Error(`Unsupported desktop native build platform: ${rid}`);
  }

  const { stdout: sourceCommit } = await execute('git', ['rev-parse', 'HEAD'], { cwd: root });
  const gitStatusArgs = process.platform === 'win32'
    ? ['-c', 'core.filemode=false', 'status', '--porcelain', '--', 'native/gpu-preview', 'native/shm-preview', 'native/skia']
    : ['status', '--porcelain', '--', 'native/gpu-preview', 'native/shm-preview', 'native/skia'];
  const { stdout: dirty } = await execute('git', gitStatusArgs, { cwd: root });
  return { platform: rid, sourceInputs, sourceCommit: sourceCommit.trim(), sourceDirty: !!dirty.trim(), artifacts };
}

async function inventory(directory, prefix = '') {
  const result = {};
  for (const entry of (await readdir(directory, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
    const file = join(directory, entry.name);
    const name = prefix + entry.name;
    if (entry.isSymbolicLink()) throw new Error('Desktop deployment cannot contain symlinks');
    if (entry.isDirectory()) Object.assign(result, await inventory(file, name + '/'));
    else if (entry.isFile() && name !== 'deployment.json') result[name] = await hash(file);
  }
  return result;
}

export async function augmentDesktopDeployment(root, dist, native) {
  root = await realpath(root);
  dist = await realpath(dist);
  if (!inside(join(root, '.local/backend-dist'), dist)) throw new Error('Desktop deployment escaped App');
  if (JSON.stringify(await nativeSources(root)) !== JSON.stringify(native.sourceInputs)) {
    throw new Error('Native source changed before matched publication');
  }
  const files = {};
  for (const [name, source] of Object.entries(native.artifacts)) {
    const relative = (name.endsWith('.node') ? 'desktop/' : 'worker/') + name;
    const target = join(dist, relative);
    await mkdir(dirname(target), { recursive: true });
    await copyFile(source, target);
    files[relative] = await hash(target);
    if (files[relative] !== await hash(source)) throw new Error('Native payload copy hash mismatch');
  }
  const platform = native.platform ?? backendHost.platformRid();
  const manifest = platform === 'linux-x64'
    ? {
        schemaVersion: 1,
        abiVersion: 1,
        platform,
        electron: '45.2.1',
        sourceCommit: native.sourceCommit,
        sourceDirty: native.sourceDirty,
        sourceInputs: native.sourceInputs,
        sourcePins: {
          skiaSharp: 'f568ac94dd768ef9a2f593537cfde2dd0d348ef5',
          skia: '7dbfc07dd33181f84e0958afb7ee805c6c769f0b',
          vulkanHeaders: 'b5c8f996196ba4aa6d8f97e52b5d3b6e70f7e4e2',
        },
        gpuCopiesPerFrame: 1,
        files,
      }
    : {
        schemaVersion: 1,
        abiVersion: 1,
        platform,
        electron: '45.2.1',
        defaultTransport: 'shm',
        sourceCommit: native.sourceCommit,
        sourceDirty: native.sourceDirty,
        sourceInputs: native.sourceInputs,
        gpuCopiesPerFrame: 0,
        files,
      };
  await writeFile(join(dist, 'gpu-preview-manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
  const deployment = JSON.parse(await readFile(join(dist, 'deployment.json'), 'utf8'));
  deployment.gpuPreview = {
    manifest: 'gpu-preview-manifest.json',
    sha256: await hash(join(dist, 'gpu-preview-manifest.json')),
    abiVersion: 1,
  };
  deployment.files = await inventory(dist);
  await writeFile(join(dist, 'deployment.json'), JSON.stringify(deployment, null, 2) + '\n');
  await verifyDesktopDeployment(root, dist);
  return { manifest: join(dist, 'gpu-preview-manifest.json'), sha256: deployment.gpuPreview.sha256 };
}

export async function verifyDesktopDeployment(root, dist) {
  root = await realpath(root);
  dist = await realpath(dist);
  if (!inside(join(root, '.local/backend-dist'), dist)) throw new Error('Desktop deployment escaped App');
  const { gpuManifest } = backendHost.validateDesktopDeployment(dist, '45.2.1');
  return gpuManifest;
}
