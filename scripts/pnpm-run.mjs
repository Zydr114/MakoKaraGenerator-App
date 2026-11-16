// Cross-platform pnpm forwarder supporting both direct pnpm and corepack pnpm without admin shims.
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { delimiter, dirname, isAbsolute, join } from 'node:path';

function findInPath(names) {
  for (const dir of String(process.env.PATH ?? '').split(delimiter)) {
    if (!dir || !isAbsolute(dir)) continue;
    for (const name of names) {
      const candidate = join(dir, name);
      if (existsSync(candidate)) return candidate;
    }
  }
  return null;
}

function resolveCommand(args) {
  const execPath = process.env.npm_execpath;
  if (execPath && existsSync(execPath) && /pnpm/i.test(execPath) && /\.[cm]?js$/i.test(execPath)) {
    return { executable: process.execPath, args: [execPath, ...args] };
  }
  if (process.platform !== 'win32') {
    const pnpmPath = findInPath(['pnpm']);
    return pnpmPath
      ? { executable: pnpmPath, args }
      : { executable: 'corepack', args: ['pnpm', ...args] };
  }
  const pnpmExe = findInPath(['pnpm.exe']);
  if (pnpmExe) return { executable: pnpmExe, args };
  const pnpmCmd = findInPath(['pnpm.cmd']);
  if (pnpmCmd) return { executable: process.env.ComSpec || 'cmd.exe', args: ['/d', '/s', '/c', pnpmCmd, ...args] };
  const nodeCorepackJs = join(dirname(process.execPath), 'node_modules', 'corepack', 'dist', 'corepack.js');
  if (existsSync(nodeCorepackJs)) {
    return { executable: process.execPath, args: [nodeCorepackJs, 'pnpm', ...args] };
  }
  const corepackCmd = findInPath(['corepack.cmd', 'corepack']);
  if (corepackCmd) {
    const siblingCorepackJs = join(dirname(corepackCmd), 'node_modules', 'corepack', 'dist', 'corepack.js');
    if (existsSync(siblingCorepackJs)) {
      return { executable: process.execPath, args: [siblingCorepackJs, 'pnpm', ...args] };
    }
    return { executable: process.env.ComSpec || 'cmd.exe', args: ['/d', '/s', '/c', corepackCmd, 'pnpm', ...args] };
  }
  throw new Error('Cannot locate pnpm or corepack in PATH.');
}

const resolved = resolveCommand(process.argv.slice(2));
const child = spawn(resolved.executable, resolved.args, { stdio: 'inherit', windowsHide: true });
child.on('error', (error) => {
  console.error(error.message);
  process.exit(1);
});
child.on('exit', (code, signal) => {
  process.exit(code ?? (signal ? 1 : 0));
});
