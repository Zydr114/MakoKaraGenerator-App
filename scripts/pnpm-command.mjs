import { existsSync } from 'node:fs';
import { delimiter, dirname, isAbsolute, join } from 'node:path';

// Prefer JS entry points so paths and arguments never pass through a shell.
export function resolvePnpmCommand(args, { env = process.env, platform = process.platform, execPath = process.execPath } = {}) {
  const nodeCommand = (entry, prefix = []) => ({ executable: execPath, args: [entry, ...prefix, ...args] });
  const findInPath = (names) => {
    for (const dir of String(env.PATH ?? '').split(delimiter)) {
      if (!dir || !isAbsolute(dir)) continue;
      for (const name of names) {
        const candidate = join(dir, name);
        if (existsSync(candidate)) return candidate;
      }
    }
    return null;
  };
  if (env.npm_execpath && /pnpm/i.test(env.npm_execpath) && /\.[cm]?js$/i.test(env.npm_execpath) && existsSync(env.npm_execpath)) {
    return nodeCommand(env.npm_execpath);
  }
  if (platform !== 'win32') {
    const pnpm = findInPath(['pnpm']);
    return pnpm ? { executable: pnpm, args } : { executable: 'corepack', args: ['pnpm', ...args] };
  }
  const pnpmExe = findInPath(['pnpm.exe']);
  if (pnpmExe) return { executable: pnpmExe, args };
  const pnpmCmd = findInPath(['pnpm.cmd']);
  if (pnpmCmd) {
    const base = dirname(pnpmCmd);
    for (const entry of [
      join(base, 'node_modules', 'pnpm', 'bin', 'pnpm.cjs'),
      join(base, 'node_modules', 'pnpm', 'bin', 'pnpm.js'),
      join(base, 'node_modules', 'corepack', 'dist', 'pnpm.js'),
    ]) {
      if (existsSync(entry)) return nodeCommand(entry);
    }
    return batchCommand(pnpmCmd, args, env);
  }
  const nodeCorepackJs = join(dirname(execPath), 'node_modules', 'corepack', 'dist', 'corepack.js');
  if (existsSync(nodeCorepackJs)) return nodeCommand(nodeCorepackJs, ['pnpm']);
  const corepackCmd = findInPath(['corepack.cmd', 'corepack']);
  if (corepackCmd) {
    const entry = join(dirname(corepackCmd), 'node_modules', 'corepack', 'dist', 'corepack.js');
    if (existsSync(entry)) return nodeCommand(entry, ['pnpm']);
    return batchCommand(corepackCmd, ['pnpm', ...args], env);
  }
  throw new Error('Cannot locate pnpm or corepack in PATH.');
}

function batchCommand(executable, args, env) {
  // cmd /s strips the outer pair; keep a second pair around every token.
  // Disable delayed expansion and reject quote/expansion/control characters:
  // unlike native argv, arbitrary strings cannot safely round-trip a .cmd shim.
  const quote = (value) => {
    if (/["%!\x00-\x1f]/.test(value)) {
      throw new Error('Cannot safely pass this path or argument through a Windows batch shim; install a pnpm/corepack JS entry point.');
    }
    // npm-style shims forward %* to Node's CRT argv parser. Escape trailing
    // backslashes so its closing quote cannot become part of the argument.
    return `"${value.replace(/\\+$/, '$&$&')}"`;
  };
  const command = `"${[executable, ...args].map(quote).join(' ')}"`;
  return {
    executable: env.ComSpec || 'cmd.exe',
    args: ['/d', '/s', '/v:off', '/c', command],
    spawnOptions: { windowsVerbatimArguments: true },
  };
}
