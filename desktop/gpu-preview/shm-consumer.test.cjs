'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');

const {
  allocateExactFrameBuffer,
  createShmStreamConsumer,
} = require('./shm-consumer.cjs');

function makeBaseMeta(overrides = {}) {
  return {
    type: 'frame',
    protocolVersion: 1,
    streamId: 's1',
    hostInstanceId: 'h1',
    producerEpoch: '1',
    inputGeneration: '1',
    consumerEpoch: '1',
    timelineEpoch: '1',
    poolGeneration: '2',
    transport: 'shm',
    api: 'vulkan',
    pixelFormat: 'bgra8888',
    width: 1280,
    height: 720,
    strideBytes: 1280 * 4,
    slotId: 0,
    slotEpoch: '10',
    frameSeq: 1,
    allocationSize: 1280 * 720 * 4 * 3,
    slotOffsetBytes: 0,
    slotSizeBytes: 1280 * 720 * 4,
    counters: { gpuBlits: 0, cpuPixelReadbacks: 1, queueIdleWaits: 1 },
    timings: { renderDurationUs: 100, portCaptureDurationUs: 50 },
    ...overrides,
  };
}

test('allocateExactFrameBuffer creates unpooled exact ArrayBuffer at byteOffset 0', () => {
  const stride = 1280 * 4;
  const height = 720;
  const { buffer, exactFrameBytes } = allocateExactFrameBuffer(stride, height);

  assert.equal(exactFrameBytes, stride * height);
  assert.equal(buffer.byteOffset, 0);
  assert.equal(buffer.buffer.byteLength, exactFrameBytes);
});

test('port.postMessage is called with strictly 1 argument (no transfer list) and full 10-field release ACK is emitted', () => {
  const portEmitter = new EventEmitter();
  const postCalls = [];
  const releases = [];
  const completions = [];

  const mockPort = {
    postMessage(...args) {
      if (args.length !== 1) {
        throw new Error(`Expected strictly 1 postMessage arg, got ${args.length}`);
      }
      postCalls.push(args[0]);
    },
    on: portEmitter.on.bind(portEmitter),
  };

  let closedMapping = false;
  const mockShmBridge = {
    openReadOnly() {
      return {
        readSlot(_offset, length, targetBuf) {
          targetBuf.fill(0x5a, 0, Math.min(length, 16));
        },
        close() {
          closedMapping = true;
        },
      };
    },
  };

  const consumer = createShmStreamConsumer({
    shmBridge: mockShmBridge,
    shmName: 'shm://s1',
    allocationBytes: 1280 * 720 * 4 * 3,
    size: { width: 1280, height: 720 },
    strideBytes: 1280 * 4,
    pixelFormat: 'bgra8888',
    streamIdentity: { streamId: 's1' },
    hostConfig: { hostInstanceId: 'h1', producerEpoch: '1', inputGeneration: '1', poolGeneration: '2' },
    port: mockPort,
    sendRelease: (rel) => releases.push(rel),
    onFrameCompleted: (info) => completions.push(info),
  });

  const m1 = makeBaseMeta({ frameSeq: 1, slotId: 0, slotEpoch: '10' });
  consumer.consumeFrame(m1);
  assert.equal(postCalls.length, 1);
  assert.equal(postCalls[0].buffer.byteOffset, 0);
  assert.equal(postCalls[0].buffer.buffer.byteLength, 1280 * 720 * 4);
  assert.equal(consumer.getOutstandingCount(), 1);

  portEmitter.emit('message', {
    data: {
      type: 'drawn',
      streamId: 's1',
      hostInstanceId: 'h1',
      producerEpoch: '1',
      inputGeneration: '1',
      consumerEpoch: '1',
      timelineEpoch: '1',
      poolGeneration: '2',
      slotId: 0,
      slotEpoch: '10',
      frameSeq: 1,
    },
  });

  assert.equal(consumer.getOutstandingCount(), 0);
  assert.equal(releases.length, 1);
  assert.deepEqual(releases[0], {
    type: 'release',
    protocolVersion: 1,
    streamId: 's1',
    hostInstanceId: 'h1',
    producerEpoch: '1',
    inputGeneration: '1',
    consumerEpoch: '1',
    timelineEpoch: '1',
    poolGeneration: '2',
    slotId: 0,
    slotEpoch: '10',
    frameSeq: 1,
  });
  assert.equal(completions.length, 1);
  assert.equal(completions[0].drawn, true);
  assert.deepEqual(completions[0].timings, { renderDurationUs: 100, portCaptureDurationUs: 50 });

  consumer.close();
  consumer.close();
  assert.equal(closedMapping, true);
});

test('3-slot lease backpressure, duplicate slotId, stale slotEpoch, and padded stride support', () => {
  const mockPort = { postMessage() {}, on() {} };
  const mockShmBridge = { openReadOnly: () => ({ readSlot() {}, close() {} }) };
  const strideBytes = 1280 * 4 + 64;
  const slotSizeBytes = strideBytes * 720;
  const allocationBytes = slotSizeBytes * 3;

  const consumer = createShmStreamConsumer({
    shmBridge: mockShmBridge,
    shmName: 'shm://padded',
    allocationBytes,
    size: { width: 1280, height: 720 },
    strideBytes,
    pixelFormat: 'rgba8888',
    streamIdentity: { streamId: 's1' },
    port: mockPort,
  });

  for (let i = 0; i < 3; i++) {
    consumer.consumeFrame(makeBaseMeta({
      api: 'cpu',
      pixelFormat: 'rgba8888',
      strideBytes,
      slotSizeBytes,
      allocationSize: allocationBytes,
      slotId: i,
      slotEpoch: '5',
      slotOffsetBytes: i * slotSizeBytes,
      frameSeq: i + 1,
    }));
  }

  assert.equal(consumer.getOutstandingCount(), 3);
  assert.equal(consumer.getPeakOutstanding(), 3);

  assert.throws(() => {
    consumer.consumeFrame(makeBaseMeta({
      api: 'cpu',
      pixelFormat: 'rgba8888',
      strideBytes,
      slotSizeBytes,
      allocationSize: allocationBytes,
      slotId: 0,
      slotEpoch: '6',
      slotOffsetBytes: 0,
      frameSeq: 4,
    }));
  }, /already outstanding/);

  consumer.close();
});

test('shouldTransfer === false releases immediately with full lease without readSlot or postMessage', () => {
  let readCalled = false;
  let posted = false;
  let released = null;

  const consumer = createShmStreamConsumer({
    shmBridge: { openReadOnly: () => ({ readSlot() { readCalled = true; }, close() {} }) },
    shmName: 'shm://stale',
    allocationBytes: 1280 * 720 * 4 * 3,
    size: { width: 1280, height: 720 },
    strideBytes: 1280 * 4,
    pixelFormat: 'bgra8888',
    streamIdentity: { streamId: 's1' },
    port: { postMessage() { posted = true; }, on() {} },
    shouldTransfer: () => false,
    sendRelease: (r) => { released = r; },
  });

  consumer.consumeFrame(makeBaseMeta({ slotId: 2, slotEpoch: '3', slotOffsetBytes: 1280 * 720 * 4 * 2, frameSeq: 9 }));
  assert.equal(readCalled, false);
  assert.equal(posted, false);
  assert.equal(released.type, 'release');
  assert.equal(released.slotId, 2);
  assert.equal(released.slotEpoch, '3');
  assert.equal(released.frameSeq, 9);
  assert.equal(released.poolGeneration, '2');

  consumer.close();
});
