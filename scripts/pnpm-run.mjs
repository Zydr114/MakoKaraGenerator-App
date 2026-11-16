// Cross-platform pnpm forwarder supporting both direct pnpm and corepack pnpm without admin shims.
import { spawn } from 'node:child_process';
import { resolvePnpmCommand } from './pnpm-command.mjs';

const resolved = resolvePnpmCommand(process.argv.slice(2));
const child = spawn(resolved.executable, resolved.args, { ...resolved.spawnOptions, stdio: 'inherit', windowsHide: true });
child.on('error', (error) => {
  console.error(error.message);
  process.exit(1);
});
child.on('exit', (code, signal) => {
  process.exit(code ?? (signal ? 1 : 0));
});
