'use strict';

const test = require('node:test');
const assert = require('node:assert');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const fs = require('node:fs');

const {
  MAX_PACKET_BYTES,
  encodeFrame,
  FrameDecoder,
  connectLocalStream
} = require('./local-stream-client.cjs');

test('encodeFrame & FrameDecoder split / coalesced chunks', () => {
  const f1 = { type: 'ping', id: 1 };
  const f2 = { type: 'pong', id: 2 };

  const buf1 = encodeFrame(f1);
  const buf2 = encodeFrame(f2);
  const combined = Buffer.concat([buf1, buf2]);

  const decoder = new FrameDecoder();

  const part1 = combined.subarray(0, 5);
  const part2 = combined.subarray(5);

  const res1 = decoder.push(part1);
  assert.deepStrictEqual(res1, []);

  const res2 = decoder.push(part2);
  assert.deepStrictEqual(res2, [f1, f2]);
});

test('FrameDecoder rejects invalid lengths and malformed JSON', () => {
  const decoder1 = new FrameDecoder();
  const badLen = Buffer.alloc(4);
  badLen.writeUInt32LE(0, 0);
  assert.throws(() => decoder1.push(badLen), /Invalid frame length header/);

  const decoder2 = new FrameDecoder();
  const tooLarge = Buffer.alloc(4);
  tooLarge.writeUInt32LE(MAX_PACKET_BYTES + 1, 0);
  assert.throws(() => decoder2.push(tooLarge), /Invalid frame length header/);

  const decoder3 = new FrameDecoder();
  const rawBadJson = Buffer.concat([
    Buffer.from([4, 0, 0, 0]),
    Buffer.from('{bad', 'utf8')
  ]);
  assert.throws(() => decoder3.push(rawBadJson), /Failed to parse frame JSON/);
});

test('connectLocalStream real server roundtrip & retry', async () => {
  const tmpDir = os.tmpdir();
  const socketPath = process.platform === 'win32'
    ? `\\\\.\\pipe\\shm-test-${crypto.randomBytes(6).toString('hex')}`
    : path.join(tmpDir, `shm-test-${crypto.randomBytes(6).toString('hex')}.sock`);
  if (process.platform !== 'win32' && fs.existsSync(socketPath)) {
    try { fs.unlinkSync(socketPath); } catch (_) {}
  }

  const server = net.createServer((socket) => {
    const serverDecoder = new FrameDecoder();
    socket.on('data', (chunk) => {
      const frames = serverDecoder.push(chunk);
      for (const f of frames) {
        if (f.type === 'echo') {
          socket.write(encodeFrame({ type: 'ack', data: f.data }));
        }
      }
    });
  });

  await new Promise(resolve => server.listen(socketPath, resolve));

  const client = await connectLocalStream(socketPath, { timeoutMs: 5000, retryDelayMs: 20 });
  assert.strictEqual(client.isClosed(), false);

  client.send({ type: 'echo', data: 'hello gpu' });
  const resp = await client.receive();
  assert.deepStrictEqual(resp, { metadata: { type: 'ack', data: 'hello gpu' }, fds: [] });

  client.close();
  assert.strictEqual(client.isClosed(), true);

  await new Promise(resolve => server.close(resolve));
  if (process.platform !== 'win32' && fs.existsSync(socketPath)) {
    try { fs.unlinkSync(socketPath); } catch (_) {}
  }
});
