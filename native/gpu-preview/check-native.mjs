// Metadata/FD ownership checks only. No Electron import or presentation claim.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { readdirSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const directory = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const broker = require(path.join(directory, 'build/gpu_preview_broker.node'));
const temporary = await mkdtemp(path.join(tmpdir(), 'mako-native-'));
const children = new Set();

async function producer(name, options, use) {
  const socket = path.join(temporary, `${name}.sock`);
  const child = spawn(path.join(directory, 'build/gpu-preview-producer'),
    ['--socket', socket, '--width', '128', '--height', '64', '--release-timeout-ms', '300', ...options]);
  children.add(child);
  let stdout = '', stderr = '';
  child.stdout.on('data', b => { stdout += b; });
  child.stderr.on('data', b => { stderr += b; });
  const completion = new Promise(resolve => child.once('exit', code => { children.delete(child); resolve(code); }));
  const deadline = Date.now() + 5000;
  while (!stdout.includes('listening') && Date.now() < deadline && child.exitCode === null) await delay(5);
  assert.match(stdout, /listening/, stderr);
  const connection = broker.connect(socket);
  try {
    await use(connection);
    const code = await completion;
    return { code, stdout, stderr };
  } finally { broker.close(connection); }
}
async function receive(connection) {
  const deadline = Date.now() + 5000;
  for (;;) {
    const packet = broker.receive(connection);
    if (packet) return packet;
    assert.ok(Date.now() < deadline, 'bounded receive deadline');
    await delay(2);
  }
}
function release(connection, packet) {
  const { frameSeq, slotId, slotEpoch } = packet.metadata;
  // No importing consumer exists in this native check: closing all local FDs is sufficient.
  for (const fd of packet.fds) {
    broker.closeFd(fd);
    assert.throws(() => broker.closeFd(fd), /not owned/);
  }
  broker.send(connection, JSON.stringify({ type: 'release', frameSeq, slotId, slotEpoch }));
}
try {
  const single = await producer('single', ['--frames', '1'], async c => {
    const p = await receive(c);
    assert.equal(p.fds.length, 1);
    assert.equal(p.metadata.modifier, '0');
    assert.equal(p.metadata.counters.cpuPixelReadbacks, 0);
    const flags = readFileSync(`/proc/self/fdinfo/${p.fds[0]}`, 'utf8').match(/^flags:\s+(\d+)$/m)[1];
    assert.notEqual(parseInt(flags, 8) & 0o2000000, 0, 'received descriptor is CLOEXEC');
    release(c, p);
    assert.equal((await receive(c)).metadata.type, 'finished');
  });
  assert.equal(single.code, 0, single.stderr);

  const triple = await producer('triple', ['--slots', '3', '--frames', '6'], async c => {
    const first = await Promise.all([receive(c), receive(c), receive(c)]);
    assert.equal(new Set(first.map(p => p.metadata.slotId)).size, 3);
    // Reverse release order proves that a release is matched to slot epoch and frame identity.
    first.reverse().forEach(p => release(c, p));
    for (let i = 0; i < 3; i++) release(c, await receive(c));
    assert.equal((await receive(c)).metadata.type, 'finished');
  });
  assert.equal(triple.code, 0, triple.stderr);

  const stopped = await producer('stop', ['--slots', '3', '--frames', '60'], async c => {
    const p = await receive(c);
    broker.send(c, JSON.stringify({ type: 'stop' }));
    release(c, p);
    let count = 1;
    for (;;) {
      const pending = await receive(c);
      if (pending.metadata.type === 'stopped') {
        assert.equal(pending.metadata.frames, count);
        assert.equal(count, 3, 'stop prevented fourth publication');
        break;
      }
      release(c, pending);
      count++;
    }
  });
  assert.equal(stopped.code, 0, stopped.stderr);

  const stale = await producer('stale', ['--frames', '2'], async c => {
    const p = await receive(c);
    release(c, p);
    const next = await receive(c);
    for (const fd of next.fds) broker.closeFd(fd);
    const { frameSeq, slotId, slotEpoch } = p.metadata;
    broker.send(c, JSON.stringify({ type: 'release', frameSeq, slotId, slotEpoch }));
  });
  assert.equal(stale.code, 1);
  assert.match(stale.stderr, /stale\/duplicate\/unknown/);

  const timeout = await producer('timeout', ['--frames', '1'], async c => {
    const p = await receive(c);
    for (const fd of p.fds) broker.closeFd(fd);
  });
  assert.equal(timeout.code, 1);
  assert.match(timeout.stderr, /timeout\/disconnect/);

  const python = `
import array, os, socket, sys
path, case = sys.argv[1:]
s=socket.socket(socket.AF_UNIX,socket.SOCK_SEQPACKET);s.bind(path);s.listen(1)
print('ready',flush=True)
c,_=s.accept()
payload={'json':b'{broken','null':b'null','array':b'[]','bytes':b'x'*20000,'fds':b'{}'}[case]
fds=[os.open('/dev/null',os.O_RDONLY) for _ in range(20 if case=='fds' else 1)]
c.sendmsg([payload],[(socket.SOL_SOCKET,socket.SCM_RIGHTS,array.array('i',fds))])
for fd in fds:os.close(fd)
c.recv(16);c.close();s.close()
`;
  for (const kind of ['json', 'null', 'array', 'bytes', 'fds']) {
    const socket = path.join(temporary, `malformed-${kind}.sock`);
    const child = spawn('python3', ['-u', '-c', python, socket, kind]);
    children.add(child);
    const completion = new Promise(resolve => child.once('exit', code => { children.delete(child); resolve(code); }));
    let stdout = '';
    child.stdout.on('data', b => { stdout += b; });
    const deadline = Date.now() + 5000;
    while (!stdout.includes('ready') && Date.now() < deadline) await delay(2);
    assert.match(stdout, /ready/);
    const c = broker.connect(socket);
    const before = readdirSync('/proc/self/fd').length;
    let rejected = false;
    try {
      while (!rejected && Date.now() < deadline) {
        try { assert.equal(broker.receive(c), null); }
        catch { rejected = true; }
        if (!rejected) await delay(2);
      }
      assert.ok(rejected, `rejected malformed ${kind}`);
      assert.equal(readdirSync('/proc/self/fd').length, before, `no incoming FD leak for ${kind}`);
    } finally { broker.close(c); }
    assert.equal(await completion, 0);
  }
  console.log(JSON.stringify({ type: 'native-checks-passed', cases: ['single-fd-cloexec', 'three-inflight-reverse-release', 'stop-drain', 'double-close', 'stale-release', 'timeout-no-reuse', 'malformed-json-null-array', 'truncated-bytes-fds-no-leak'], presentationVerified: false }));
} finally {
  for (const child of children) child.kill('SIGTERM');
  await rm(temporary, { recursive: true, force: true });
}
