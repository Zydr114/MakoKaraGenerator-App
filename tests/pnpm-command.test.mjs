import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { test } from 'node:test';
import { resolvePnpmCommand } from '../scripts/pnpm-command.mjs';

const windows = process.platform === 'win32';
const forwarder = resolve(import.meta.dirname, '../scripts/pnpm-run.mjs');

async function fixture(t, name = 'pnpm.cmd') {
  const dir = await mkdtemp(join(tmpdir(), 'mako pnpm (space) & fixture '));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const entry = join(dir, 'echo args.cjs');
  await writeFile(entry, 'process.stdout.write(JSON.stringify(process.argv.slice(2)));');
  const shim = join(dir, name);
  await writeFile(shim, `@echo off\r\n"${process.execPath}" "${entry}" %*\r\n`);
  const env = { ...process.env, PATH: dir, npm_execpath: '' };
  return { dir, shim, env, entry };
}

function execute(command, env) {
  const result = spawnSync(command.executable, command.args, {
    ...command.spawnOptions, env, encoding: 'utf8', windowsHide: true, timeout: 10000,
  });
  assert.ifError(result.error);
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}

test('Windows pnpm.cmd in a real path with spaces: old launch fails, quoted fallback preserves argv', { skip: !windows }, async (t) => {
  const { shim, env } = await fixture(t);
  const args = ['build', '--dir', 'project with spaces', 'literal & echo injected', '(parentheses)', 'trailing\\'];
  const old = spawnSync(env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', shim, 'build'], {
    env, encoding: 'utf8', windowsHide: true, timeout: 10000,
  });
  assert.notEqual(old.status, 0, 'fixture must reproduce the original unquoted cmd command failure');
  const command = resolvePnpmCommand(args, { env });
  assert.equal(command.spawnOptions.windowsVerbatimArguments, true);
  assert.deepEqual(execute(command, env), args);

  const result = spawnSync(process.execPath, [forwarder, ...args], { env, encoding: 'utf8', windowsHide: true, timeout: 10000 });
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), args);
});

test('Windows corepack.cmd fallback preserves pnpm prefix and spaced arguments', { skip: !windows }, async (t) => {
  const { dir, env } = await fixture(t, 'corepack.cmd');
  const args = ['--dir', 'frontend project', 'test:unit'];
  const command = resolvePnpmCommand(args, { env, execPath: join(dir, 'missing-node.exe') });
  assert.deepEqual(execute(command, env), ['pnpm', ...args]);
});

test('pnpm npm_execpath uses Node directly and supports shell characters as literal arguments', async (t) => {
  const { env } = await fixture(t);
  const pnpmEntry = join(env.PATH, 'pnpm.cjs');
  await writeFile(pnpmEntry, 'process.stdout.write(JSON.stringify(process.argv.slice(2)));');
  env.npm_execpath = pnpmEntry;
  const args = ['with spaces', '" & echo injected', '%PATH%', '!NAME!', 'newline\nhere'];
  const command = resolvePnpmCommand(args, { env });
  assert.equal(command.executable, process.execPath);
  assert.equal(command.spawnOptions, undefined);
  assert.deepEqual(execute(command, env), args);
});

test('Windows npm pnpm/corepack shims prefer their sibling JS entries', { skip: !windows }, async (t) => {
  const { dir, env } = await fixture(t);
  for (const relative of ['node_modules/pnpm/bin/pnpm.cjs', 'node_modules/corepack/dist/pnpm.js']) {
    const entry = join(dir, relative);
    await mkdir(resolve(entry, '..'), { recursive: true });
    await writeFile(entry, 'process.stdout.write(JSON.stringify(process.argv.slice(2)));');
    const command = resolvePnpmCommand(['build'], { env });
    assert.equal(command.executable, process.execPath);
    assert.equal(command.args[0], entry);
    assert.deepEqual(execute(command, env), ['build']);
    await rm(entry);
  }
});

test('Windows corepack.cmd prefers sibling corepack.js and keeps the pnpm prefix', { skip: !windows }, async (t) => {
  const { dir, env } = await fixture(t, 'corepack.cmd');
  const entry = join(dir, 'node_modules/corepack/dist/corepack.js');
  await mkdir(resolve(entry, '..'), { recursive: true });
  await writeFile(entry, 'process.stdout.write(JSON.stringify(process.argv.slice(2)));');
  const command = resolvePnpmCommand(['build'], { env, execPath: join(dir, 'missing-node.exe') });
  assert.equal(command.args[0], entry);
  // Resolution uses the supplied Node location; execute with the real Node fixture runtime.
  assert.deepEqual(execute({ ...command, executable: process.execPath }, env), ['pnpm', 'build']);
});

test('Windows batch fallback rejects expansion, quotes and control characters before spawning', { skip: !windows }, async (t) => {
  const { env } = await fixture(t);
  for (const value of ['%PATH%', '!NAME!', 'quote" & echo injected', 'line\nbreak', 'nul\0byte']) {
    assert.throws(() => resolvePnpmCommand([value], { env }), /Cannot safely pass/);
  }
});
