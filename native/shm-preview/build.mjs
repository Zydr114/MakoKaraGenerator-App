// Cross-platform builder for native/shm-preview/build/shm_bridge.node.
// Windows: VS BuildTools cl.exe with zero node.lib / zero Electron header download (dynamic GetProcAddress N-API).
// Linux: delegates to native/shm-preview/build.sh.
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SHM_DIR = path.dirname(fileURLToPath(import.meta.url));

/**
 * Pure-JS PE32+ export & import table parser used to verify shm_bridge.node ABI
 * without relying on external dumpbin/nm binaries.
 */
export function readPeExportsAndImports(filePath) {
  const buf = fs.readFileSync(filePath);
  if (buf.length < 0x100 || buf.readUInt16LE(0) !== 0x5a4d) {
    throw new Error(`Not a valid PE binary (missing MZ header): ${filePath}`);
  }
  const peOffset = buf.readUInt32LE(0x3c);
  if (peOffset + 24 > buf.length || buf.readUInt32LE(peOffset) !== 0x00004550) {
    throw new Error(`Not a valid PE binary (missing PE signature): ${filePath}`);
  }
  const coffOffset = peOffset + 4;
  const machine = buf.readUInt16LE(coffOffset);
  if (machine !== 0x8664) {
    throw new Error(`Expected x64 PE32+ binary (0x8664), got 0x${machine.toString(16)}`);
  }
  const numberOfSections = buf.readUInt16LE(coffOffset + 2);
  const sizeOfOptionalHeader = buf.readUInt16LE(coffOffset + 16);
  const optOffset = coffOffset + 20;
  const magic = buf.readUInt16LE(optOffset);
  if (magic !== 0x020b) {
    throw new Error(`Expected PE32+ optional header (0x20b), got 0x${magic.toString(16)}`);
  }
  const numberOfRvaAndSizes = buf.readUInt32LE(optOffset + 108);
  const dataDirOffset = optOffset + 112;
  const sectionTableOffset = optOffset + sizeOfOptionalHeader;

  const sections = [];
  for (let i = 0; i < numberOfSections; i++) {
    const off = sectionTableOffset + i * 40;
    const virtualSize = buf.readUInt32LE(off + 8);
    const virtualAddress = buf.readUInt32LE(off + 12);
    const sizeOfRawData = buf.readUInt32LE(off + 16);
    const pointerToRawData = buf.readUInt32LE(off + 20);
    sections.push({ virtualSize, virtualAddress, sizeOfRawData, pointerToRawData });
  }

  const rvaToOffset = (rva) => {
    for (const sec of sections) {
      const span = Math.max(sec.virtualSize, sec.sizeOfRawData);
      if (rva >= sec.virtualAddress && rva < sec.virtualAddress + span) {
        const delta = rva - sec.virtualAddress;
        if (delta < sec.sizeOfRawData) return sec.pointerToRawData + delta;
      }
    }
    return -1;
  };

  const readCString = (offset) => {
    if (offset < 0 || offset >= buf.length) return '';
    let end = offset;
    while (end < buf.length && buf[end] !== 0) end++;
    return buf.subarray(offset, end).toString('ascii');
  };

  const exports = [];
  if (numberOfRvaAndSizes > 0) {
    const exportRva = buf.readUInt32LE(dataDirOffset);
    const exportSize = buf.readUInt32LE(dataDirOffset + 4);
    if (exportRva !== 0 && exportSize !== 0) {
      const dirOff = rvaToOffset(exportRva);
      if (dirOff >= 0) {
        const numberOfNames = buf.readUInt32LE(dirOff + 24);
        const namesRva = buf.readUInt32LE(dirOff + 32);
        const namesOff = rvaToOffset(namesRva);
        if (namesOff >= 0) {
          for (let i = 0; i < numberOfNames; i++) {
            const nameRva = buf.readUInt32LE(namesOff + i * 4);
            exports.push(readCString(rvaToOffset(nameRva)));
          }
        }
      }
    }
  }

  const imports = [];
  if (numberOfRvaAndSizes > 1) {
    const importRva = buf.readUInt32LE(dataDirOffset + 8);
    const importSize = buf.readUInt32LE(dataDirOffset + 12);
    if (importRva !== 0 && importSize !== 0) {
      let entryOff = rvaToOffset(importRva);
      while (entryOff >= 0 && entryOff + 20 <= buf.length) {
        const nameRva = buf.readUInt32LE(entryOff + 12);
        const firstThunk = buf.readUInt32LE(entryOff + 16);
        if (nameRva === 0 && firstThunk === 0) break;
        const dllName = readCString(rvaToOffset(nameRva));
        if (dllName) imports.push(dllName);
        entryOff += 20;
      }
    }
  }

  return { exports, imports };
}

function findVcvars64() {
  const candidates = [];
  if (process.env.VSINSTALLDIR) {
    candidates.push(path.join(process.env.VSINSTALLDIR, 'VC', 'Auxiliary', 'Build', 'vcvars64.bat'));
  }
  const programFilesX86 = process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)';
  const vswhere = path.join(programFilesX86, 'Microsoft Visual Studio', 'Installer', 'vswhere.exe');
  if (fs.existsSync(vswhere)) {
    try {
      const output = execFileSync(
        vswhere,
        ['-latest', '-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath'],
        { encoding: 'utf8', windowsHide: true },
      ).trim();
      if (output) {
        candidates.push(path.join(output, 'VC', 'Auxiliary', 'Build', 'vcvars64.bat'));
      }
    } catch { /* fall through */ }
  }
  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return candidate;
  }
  throw new Error('Unable to locate Visual Studio BuildTools vcvars64.bat (requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64).');
}

export function buildShmBridge(shmDir = SHM_DIR) {
  const buildDir = path.join(shmDir, 'build');
  const outputPath = path.join(buildDir, 'shm_bridge.node');
  fs.mkdirSync(buildDir, { recursive: true });

  if (process.platform === 'linux') {
    const res = spawnSync('bash', [path.join(shmDir, 'build.sh')], { cwd: shmDir, stdio: 'inherit' });
    if (res.status !== 0) {
      throw new Error(`shm_bridge Linux build failed (${res.status ?? res.signal})`);
    }
    return outputPath;
  }

  if (process.platform !== 'win32' || process.arch !== 'x64') {
    throw new Error(`Unsupported platform for shm_bridge build: ${process.platform}/${process.arch}`);
  }

  const vcvars64 = findVcvars64();
  const clCmd = [
    `"${vcvars64}" >nul`,
    'cl.exe /nologo /std:c++20 /O2 /W4 /WX /EHsc /LD /Fo:build\\ shm_bridge.cc /link /OUT:build\\shm_bridge.node /IMPLIB:build\\shm_bridge.lib kernel32.lib advapi32.lib',
  ].join(' && ');

  const res = spawnSync(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', `"${clCmd}"`], {
    cwd: shmDir,
    stdio: 'inherit',
    windowsHide: true,
    windowsVerbatimArguments: true,
  });
  if (res.error) throw res.error;
  if (res.status !== 0) {
    throw new Error(`shm_bridge Windows MSVC build failed (exit=${res.status ?? res.signal})`);
  }

  const { exports, imports } = readPeExportsAndImports(outputPath);
  if (!exports.includes('napi_register_module_v1')) {
    throw new Error(`Required PE export 'napi_register_module_v1' missing from ${outputPath}; exports=${JSON.stringify(exports)}`);
  }
  const forbidden = imports.filter((dll) => /^(node|electron)\.(exe|dll)$/i.test(dll));
  if (forbidden.length > 0) {
    throw new Error(`shm_bridge.node must not statically link host runtime (${forbidden.join(', ')})`);
  }

  return outputPath;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const built = buildShmBridge();
  console.log(`[shm-preview] built ${built}`);
}
