import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const desktop = fileURLToPath(new URL('..', import.meta.url));
const root = path.dirname(desktop);
const env = { ...process.env, ELECTRON_CACHE: path.join(root, '.local/electron-cache') };
for (const [command, args] of [
  ['pnpm', ['install', '--frozen-lockfile']],
  [process.execPath, ['node_modules/electron/install.js']],
]) {
  const result = spawnSync(command, args, { cwd: desktop, env, stdio: 'inherit' });
  if (result.status !== 0) process.exit(result.status ?? 1);
}
