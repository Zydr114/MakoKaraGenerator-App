import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { resolvePnpmCommand } from '../scripts/pnpm-command.mjs';

const desktop = fileURLToPath(new URL('.', import.meta.url));
const root = path.dirname(desktop);
const env = { ...process.env, ELECTRON_CACHE: path.join(root, '.local/electron-cache') };
const pnpm = resolvePnpmCommand(['install', '--frozen-lockfile']);

for (const [command, args, extraOptions] of [
  [pnpm.executable, pnpm.args, pnpm.spawnOptions ?? {}],
  [process.execPath, ['node_modules/electron/install.js'], {}],
]) {
  const result = spawnSync(command, args, { ...extraOptions, cwd: desktop, env, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}
