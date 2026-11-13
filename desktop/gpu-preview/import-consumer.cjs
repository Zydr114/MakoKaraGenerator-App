'use strict';

/** Shared by finite P0 and production Worker streams. Protocol ACKs remain with
 * the controller, which must stop and isolate on consumer/GPU loss: Electron's fallback
 * callback alone cannot prove the dead consumer's final GPU use completed. */
async function importAndTransferFrame(options) {
  const { packet, broker, sharedTexture } = options;
  let item;
  let primaryError;
  try {
    try {
      const metadata = options.validate(packet);
      const timestamp = options.timestamp(metadata);
      if (!Number.isSafeInteger(timestamp) || timestamp < 0) throw new Error('VideoFrame timestamp must be a safe nonnegative microsecond value');
      item = { metadata, mainReleased:false, failed:false, imported:null };
      item.imported = sharedTexture.importSharedTexture({
        textureInfo: {
          pixelFormat:'bgra',
          colorSpace:{ primaries:'bt709', transfer:'srgb', matrix:'rgb', range:'full' },
          codedSize:{ width:metadata.width, height:metadata.height }, timestamp,
          handle:{ nativePixmap:{
            planes:metadata.planes.map(p => ({ fd:packet.fds[p.fdIndex], stride:p.stride, offset:p.offset, size:p.size })),
            modifier:metadata.modifier, supportsZeroCopyWebGpuImport:false,
          } },
        },
        // A native callback must never unwind into Electron. Defer until release()
        // itself returned, so a throwing release cannot accidentally ACK a slot.
        allReferencesReleased: () => queueMicrotask(() => {
          try {
            if (item.failed) options.onAbandonedRelease?.(metadata);
            else options.onAllReferencesReleased(metadata);
          } catch (error) { try { options.onFailure?.(error,metadata); } catch {} }
        }),
      });
      options.onImported(item);
    } catch (error) { primaryError = error; }
    // v45.2.1 synchronously dup()s each nativePixmap FD. Close received SCM_RIGHTS
    // FDs on every path, even if another close fails, preserving the first error.
    for (const fd of packet.fds) {
      try { broker.closeFd(fd); }
      catch (error) { primaryError ??= error; }
    }
    if (primaryError) throw primaryError;
    if (options.shouldTransfer(item.metadata)) {
      await sharedTexture.sendSharedTexture({ frame:options.targetFrame(), importedSharedTexture:item.imported }, item.metadata);
      options.onTransferred?.(item.metadata);
    } else options.onDrained?.(item.metadata);
  } catch (error) {
    primaryError = error;
    if (item) item.failed = true;
    try { options.onFailure?.(error,item?.metadata); } catch { /* Preserve the original failure. */ }
    try { if (options.onTransferError?.(error,item?.metadata)) primaryError = undefined; }
    catch { /* Reporting must not replace the original failure. */ }
  } finally {
    // This outer finally also covers onImported() failures after import.
    if (item?.imported && !item.mainReleased) {
      item.mainReleased = true;
      try { item.imported.release(); }
      catch (error) { item.failed = true; primaryError ??= error; }
      try { options.onMainReleased?.(item.metadata); }
      catch (error) { item.failed = true; primaryError ??= error; }
    }
  }
  if (primaryError) throw primaryError;
}
module.exports = { importAndTransferFrame };
