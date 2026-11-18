'use strict';

const IDENTITY_KEYS = [
  'streamId',
  'hostInstanceId',
  'producerEpoch',
  'inputGeneration',
  'consumerEpoch',
  'timelineEpoch',
  'poolGeneration',
  'slotId',
  'slotEpoch',
  'frameSeq',
];

const extractLease = (m) => Object.fromEntries(IDENTITY_KEYS.map((key) => [key, m[key]]));
const makeLeaseKey = (m) => IDENTITY_KEYS.map((key) => String(m[key])).join('/');

function allocateExactFrameBuffer(strideBytes, height) {
  if (!Number.isSafeInteger(strideBytes) || strideBytes <= 0 || strideBytes % 4 !== 0) {
    throw new Error(`Invalid strideBytes: ${strideBytes}`);
  }
  if (!Number.isSafeInteger(height) || height <= 0) {
    throw new Error(`Invalid height: ${height}`);
  }

  const exactFrameBytes = strideBytes * height;
  if (exactFrameBytes > 64 * 1024 * 1024) {
    throw new Error(`Frame buffer size ${exactFrameBytes} exceeds 64MB limit`);
  }

  const buf = Buffer.allocUnsafeSlow(exactFrameBytes);
  if (buf.byteOffset !== 0 || buf.buffer.byteLength !== exactFrameBytes) {
    throw new Error('Buffer allocation did not yield exact unpooled ArrayBuffer starting at byteOffset 0');
  }

  return { buffer: buf, exactFrameBytes };
}

function isCanonicalPositiveDecimal(value) {
  return typeof value === 'string' && /^[1-9]\d*$/.test(value) && BigInt(value) <= 0xffffffffffffffffn;
}

function validateShmFrameMetadata(metadata, state, hostConfig) {
  if (!metadata || typeof metadata !== 'object') {
    throw new Error('Metadata must be an object');
  }
  if (metadata.type !== 'frame' || metadata.protocolVersion !== 1) {
    throw new Error('Invalid SHM frame packet header');
  }
  if (metadata.streamId !== state.streamId || metadata.consumerEpoch !== '1') {
    throw new Error('SHM frame stream identity mismatch');
  }
  if (hostConfig) {
    if (
      metadata.hostInstanceId !== hostConfig.hostInstanceId ||
      metadata.producerEpoch !== hostConfig.producerEpoch ||
      metadata.inputGeneration !== hostConfig.inputGeneration ||
      metadata.poolGeneration !== hostConfig.poolGeneration
    ) {
      throw new Error('SHM frame generation or host identity mismatch');
    }
  }
  for (const epochKey of ['producerEpoch', 'inputGeneration', 'timelineEpoch', 'poolGeneration', 'slotEpoch']) {
    if (!isCanonicalPositiveDecimal(metadata[epochKey])) {
      throw new Error(`Invalid canonical epoch field: ${epochKey}`);
    }
  }

  if (metadata.transport !== 'shm') {
    throw new Error(`Invalid transport: ${metadata.transport}`);
  }
  if (!['vulkan', 'cpu'].includes(metadata.api)) {
    throw new Error(`Invalid API: ${metadata.api}`);
  }
  if (state.pixelFormat && metadata.pixelFormat !== state.pixelFormat) {
    throw new Error(`Pixel format changed unexpectedly: ${metadata.pixelFormat} !== ${state.pixelFormat}`);
  }
  if (!['bgra8888', 'rgba8888'].includes(metadata.pixelFormat)) {
    throw new Error(`Invalid pixel format: ${metadata.pixelFormat}`);
  }
  if (metadata.width !== state.size.width || metadata.height !== state.size.height) {
    throw new Error('Dimension mismatch with consumer state');
  }
  if (metadata.strideBytes !== state.strideBytes) {
    throw new Error('Stride bytes mismatch with consumer state');
  }
  if (metadata.strideBytes < metadata.width * 4 || metadata.strideBytes % 4 !== 0) {
    throw new Error('Invalid stride bytes alignment or width relation');
  }

  const slotId = metadata.slotId;
  if (!Number.isSafeInteger(slotId) || slotId < 0 || slotId >= 3) {
    throw new Error(`Invalid slotId: ${slotId}`);
  }

  const slotEpochBig = BigInt(metadata.slotEpoch);
  const lastSlotEpoch = state.slotEpochs.get(slotId) ?? 0n;
  if (slotEpochBig <= lastSlotEpoch) {
    throw new Error(`Stale or duplicate slot epoch for slot ${slotId}: ${slotEpochBig} <= ${lastSlotEpoch}`);
  }

  for (const tracked of state.outstanding.values()) {
    if (tracked.metadata.slotId === slotId) {
      throw new Error(`Slot ${slotId} is already outstanding in flight`);
    }
  }
  if (state.outstanding.size >= 3) {
    throw new Error('Too many outstanding frames (max 3)');
  }

  if (!Number.isSafeInteger(metadata.frameSeq) || metadata.frameSeq <= state.lastFrameSeq) {
    throw new Error(`Invalid or non-monotonic frameSeq: ${metadata.frameSeq} <= ${state.lastFrameSeq}`);
  }

  const exactFrameBytes = metadata.strideBytes * metadata.height;
  if (metadata.allocationSize !== state.allocationBytes) {
    throw new Error('Allocation size mismatch');
  }
  if (
    !Number.isSafeInteger(metadata.slotOffsetBytes) ||
    !Number.isSafeInteger(metadata.slotSizeBytes) ||
    metadata.slotOffsetBytes < 0 ||
    metadata.slotSizeBytes < exactFrameBytes ||
    metadata.slotOffsetBytes + exactFrameBytes > state.allocationBytes
  ) {
    throw new Error('Slot bounds out of allocation range');
  }

  if (metadata.counters?.cpuPixelReadbacks !== 1) {
    throw new Error('Invalid cpuPixelReadbacks counter');
  }

  const timings = metadata.timings;
  if (
    !timings ||
    !Number.isSafeInteger(timings.renderDurationUs) ||
    timings.renderDurationUs < 0 ||
    !Number.isSafeInteger(timings.portCaptureDurationUs) ||
    timings.portCaptureDurationUs < 0
  ) {
    throw new Error('Invalid or unsafe timings data');
  }

  if (metadata.diagnostics?.errorCount) {
    throw new Error('Frame diagnostics reported errorCount > 0');
  }

  return exactFrameBytes;
}

function createShmStreamConsumer(options) {
  const {
    shmBridge,
    shmName,
    allocationBytes,
    size,
    strideBytes,
    pixelFormat,
    streamIdentity,
    hostConfig,
    port,
    shouldTransfer = () => true,
    sendRelease,
    onFrameCompleted,
    onError,
  } = options;

  if (!shmBridge || typeof shmBridge.openReadOnly !== 'function') {
    throw new Error('Invalid shmBridge');
  }
  if (!shmName || typeof shmName !== 'string') {
    throw new Error('Invalid shmName');
  }
  if (!port) {
    throw new Error('Missing port');
  }

  const mapping = shmBridge.openReadOnly(shmName, allocationBytes);
  let frameBuffer;
  let exactFrameBytes;
  try {
    const allocated = allocateExactFrameBuffer(strideBytes, size.height);
    frameBuffer = allocated.buffer;
    exactFrameBytes = allocated.exactFrameBytes;
  } catch (err) {
    try { mapping.close(); } catch (_) {}
    throw err;
  }

  const state = {
    streamId: streamIdentity?.streamId ?? 'default-stream',
    size,
    strideBytes,
    pixelFormat,
    allocationBytes,
    slotEpochs: new Map(),
    outstanding: new Map(),
    peakOutstanding: 0,
    lastFrameSeq: 0,
  };

  let closed = false;

  function handlePortMessage(event) {
    if (closed) return;
    try {
      const msg = event?.data ?? event;
      if (!msg || typeof msg !== 'object') return;
      if (msg.type !== 'drawn' && msg.type !== 'released') return;

      const key = makeLeaseKey(msg);
      const tracked = state.outstanding.get(key);
      if (!tracked) {
        throw new Error('Unknown or duplicate SHM renderer completion');
      }

      state.outstanding.delete(key);

      if (typeof sendRelease === 'function') {
        sendRelease({
          type: 'release',
          protocolVersion: 1,
          ...extractLease(tracked.metadata),
        });
      }

      if (typeof onFrameCompleted === 'function') {
        onFrameCompleted({
          metadata: tracked.metadata,
          drawn: msg.type === 'drawn',
          observation: msg.observation,
          timings: tracked.metadata.timings,
          counters: tracked.metadata.counters,
        });
      }
    } catch (err) {
      if (typeof onError === 'function') onError(err);
    }
  }

  if (typeof port.on === 'function') {
    port.on('message', handlePortMessage);
  } else if (typeof port.addEventListener === 'function') {
    port.addEventListener('message', handlePortMessage);
  } else {
    port.onmessage = handlePortMessage;
  }
  port.start?.();

  function consumeFrame(metadata) {
    if (closed) {
      throw new Error('Consumer is closed');
    }

    validateShmFrameMetadata(metadata, state, hostConfig);
    state.slotEpochs.set(metadata.slotId, BigInt(metadata.slotEpoch));
    state.lastFrameSeq = metadata.frameSeq;

    if (!shouldTransfer(metadata)) {
      if (typeof sendRelease === 'function') {
        sendRelease({
          type: 'release',
          protocolVersion: 1,
          ...extractLease(metadata),
        });
      }
      return;
    }

    const key = makeLeaseKey(metadata);
    state.outstanding.set(key, { metadata });
    if (state.outstanding.size > state.peakOutstanding) {
      state.peakOutstanding = state.outstanding.size;
    }

    mapping.readSlot(metadata.slotOffsetBytes, exactFrameBytes, frameBuffer);

    // Electron MessagePortMain.postMessage does NOT support ArrayBuffer transfer lists:
    // pass strictly 1 argument so CloneableMessage / Mojo BigBuffer copies synchronously.
    port.postMessage({
      type: 'frame',
      metadata,
      buffer: frameBuffer,
    });
  }

  function close() {
    if (closed) return;
    closed = true;
    frameBuffer = null;
    try {
      mapping.close();
    } catch (_) {}
    if (typeof port.close === 'function') {
      try {
        port.close();
      } catch (_) {}
    }
  }

  return {
    consumeFrame,
    close,
    getOutstandingCount: () => state.outstanding.size,
    getPeakOutstanding: () => state.peakOutstanding,
    getFrameBufferByteLength: () => exactFrameBytes,
  };
}

module.exports = {
  IDENTITY_KEYS,
  extractLease,
  makeLeaseKey,
  allocateExactFrameBuffer,
  validateShmFrameMetadata,
  createShmStreamConsumer,
};
