'use strict';

const net = require('node:net');

const MAX_PACKET_BYTES = 16384;

function encodeFrame(payload) {
  let body;
  if (Buffer.isBuffer(payload)) {
    body = payload;
  } else if (typeof payload === 'string') {
    body = Buffer.from(payload, 'utf8');
  } else if (payload && typeof payload === 'object') {
    body = Buffer.from(JSON.stringify(payload), 'utf8');
  } else {
    throw new TypeError('Payload must be a string, Buffer, or JSON-serializable object');
  }

  if (body.length < 1 || body.length > MAX_PACKET_BYTES) {
    throw new Error(`Frame body length ${body.length} out of bounds (1..${MAX_PACKET_BYTES})`);
  }

  const header = Buffer.allocUnsafe(4);
  header.writeUInt32LE(body.length, 0);
  return Buffer.concat([header, body]);
}

class FrameDecoder {
  constructor() {
    this._buffer = Buffer.alloc(0);
  }

  push(chunk) {
    if (!chunk || chunk.length === 0) return [];
    this._buffer = this._buffer.length === 0 ? Buffer.from(chunk) : Buffer.concat([this._buffer, chunk]);
    const frames = [];

    while (this._buffer.length >= 4) {
      const len = this._buffer.readUInt32LE(0);
      if (len === 0 || len > MAX_PACKET_BYTES) {
        throw new Error(`Invalid frame length header: ${len}`);
      }

      if (this._buffer.length < 4 + len) {
        break;
      }

      const bodyBuf = this._buffer.subarray(4, 4 + len);
      this._buffer = this._buffer.subarray(4 + len);

      let parsed;
      try {
        parsed = JSON.parse(bodyBuf.toString('utf8'));
      } catch (err) {
        throw new Error(`Failed to parse frame JSON: ${err.message}`);
      }

      frames.push(parsed);
    }

    return frames;
  }
}

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function connectLocalStream(endpoint, options = {}) {
  if (typeof endpoint !== 'string' || endpoint.length === 0 || endpoint.length > 512 || endpoint.includes('\0')) {
    throw new Error('Invalid endpoint string');
  }

  const timeoutMs = options.timeoutMs ?? 5000;
  const retryDelayMs = options.retryDelayMs ?? 20;
  const deadline = Date.now() + timeoutMs;

  let socket = null;
  let lastError = null;
  while (!socket) {
    try {
      socket = await new Promise((resolve, reject) => {
        const candidate = net.createConnection(endpoint);
        const onConnect = () => {
          candidate.removeListener('error', onError);
          resolve(candidate);
        };
        const onError = (err) => {
          candidate.removeListener('connect', onConnect);
          candidate.destroy();
          reject(err);
        };
        candidate.once('connect', onConnect);
        candidate.once('error', onError);
      });
    } catch (err) {
      lastError = err;
      const retryable = err?.code === 'ENOENT' || err?.code === 'ECONNREFUSED' || err?.code === 'EAGAIN';
      if (!retryable || Date.now() >= deadline) {
        throw err;
      }
      await delay(retryDelayMs);
    }
  }

  if (!socket) {
    throw lastError ?? new Error('Failed to connect to local preview stream');
  }

  try {
    socket.setNoDelay?.(true);
  } catch (_) {}

  let closed = false;
  let peerDisconnected = false;
  let fatalError = null;
  const decoder = new FrameDecoder();
  const queue = [];
  let waiting = null;
  const { onPacket, onClose } = options;

  function settleWaiter(item, err) {
    if (!waiting) return;
    const current = waiting;
    waiting = null;
    if (err) current.reject(err);
    else current.resolve(item);
  }

  function terminate(err, intentional = false) {
    if (closed) return;
    closed = true;
    if (!intentional) peerDisconnected = true;
    if (err && !fatalError) fatalError = err;
    try {
      socket.destroy();
    } catch (_) {}
    settleWaiter(null, fatalError);
    if (typeof onClose === 'function') {
      try {
        onClose(fatalError);
      } catch (_) {}
    }
  }

  socket.on('data', (chunk) => {
    if (closed) return;
    try {
      const frames = decoder.push(chunk);
      for (const metadata of frames) {
        const packet = { metadata, fds: [] };
        if (typeof onPacket === 'function') {
          try {
            onPacket(packet);
          } catch (_) {}
        }
        if (waiting) {
          settleWaiter(packet, null);
        } else {
          queue.push(packet);
        }
      }
    } catch (err) {
      terminate(err, false);
    }
  });

  socket.on('error', (err) => {
    if (!closed) terminate(err, false);
  });

  socket.on('close', () => {
    if (!closed) terminate(fatalError, false);
  });

  return {
    send(message) {
      if (closed || peerDisconnected) {
        throw fatalError ?? new Error('closed connection');
      }
      const encoded = encodeFrame(message);
      socket.write(encoded);
    },
    tryReceive() {
      if (queue.length > 0) {
        return queue.shift();
      }
      if (fatalError) {
        throw fatalError;
      }
      if (peerDisconnected) {
        throw new Error('peer disconnected; outstanding leases unknown');
      }
      return null;
    },
    receive() {
      if (queue.length > 0) {
        return Promise.resolve(queue.shift());
      }
      if (fatalError) {
        return Promise.reject(fatalError);
      }
      if (closed) {
        return Promise.resolve(null);
      }
      return new Promise((resolve, reject) => {
        waiting = { resolve, reject };
      });
    },
    close() {
      terminate(null, true);
    },
    isClosed() {
      return closed;
    },
  };
}

module.exports = {
  MAX_PACKET_BYTES,
  encodeFrame,
  FrameDecoder,
  connectLocalStream,
};
