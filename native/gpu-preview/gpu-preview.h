#ifndef MKG_GPU_PREVIEW_H
#define MKG_GPU_PREVIEW_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MKG_GPU_ABI_VERSION 1u
#define MKG_GPU_SLOT_COUNT 3u
#define MKG_GPU_MAX_PACKET_BYTES 16384u
#define MKG_GPU_MAX_PACKET_FDS 4u

typedef struct mkg_gpu_pool mkg_gpu_pool;
typedef struct mkg_gpu_channel mkg_gpu_channel;

typedef enum mkg_gpu_status {
  MKG_GPU_OK = 0,
  MKG_GPU_WOULD_BLOCK = 1,
  MKG_GPU_NO_FREE_SLOT = 2,
  MKG_GPU_UNSUPPORTED = 3,
  MKG_GPU_INVALID_ARGUMENT = 4,
  MKG_GPU_STALE_LEASE = 5,
  MKG_GPU_QUARANTINED = 6,
  MKG_GPU_NOT_DRAINED = 7,
  MKG_GPU_DEVICE_LOST = 8,
  MKG_GPU_IO_ERROR = 9,
  MKG_GPU_OUT_OF_MEMORY = 10,
  MKG_GPU_INTERNAL_ERROR = 11
} mkg_gpu_status;

enum mkg_gpu_extension_bits {
  MKG_GPU_EXTERNAL_MEMORY_FD = 1u << 0,
  MKG_GPU_EXTERNAL_MEMORY_DMA_BUF = 1u << 1,
  MKG_GPU_DRM_FORMAT_MODIFIER = 1u << 2,
  MKG_GPU_QUEUE_FAMILY_FOREIGN = 1u << 3,
  MKG_GPU_PHYSICAL_DEVICE_DRM = 1u << 4,
  MKG_GPU_REQUIRED_EXTENSIONS = 31u
};

enum mkg_gpu_origin { MKG_GPU_TOP_LEFT = 0, MKG_GPU_BOTTOM_LEFT = 1 };

// All input/output structs start with sizeof(struct), MKG_GPU_ABI_VERSION.
// Vulkan handles are their unmodified 64-bit handle bits on Linux x64.
typedef struct mkg_gpu_error {
  uint32_t size, version;
  int32_t status, vk_result;
  char message[256];
} mkg_gpu_error;

typedef struct mkg_gpu_borrowed_context {
  uint32_t size, version;
  uint64_t instance, physical_device, device, queue;
  uint32_t queue_family, enabled_extensions;
} mkg_gpu_borrowed_context;

typedef struct mkg_gpu_pool_config {
  uint32_t size, version;
  uint32_t width, height, slot_count, reserved;
  uint64_t producer_epoch, pool_generation, max_allocation_bytes;
} mkg_gpu_pool_config;

typedef struct mkg_gpu_pool_info {
  uint32_t size, version;
  uint32_t width, height, slot_count, format;
  uint64_t allocation_bytes, modifier;
  uint32_t vendor_id, device_id, drm_render_major, drm_render_minor;
  uint8_t device_uuid[16];
  char device_name[256];
} mkg_gpu_pool_info;

typedef struct mkg_gpu_lease {
  uint32_t size, version;
  uint64_t producer_epoch, pool_generation, frame_seq, slot_epoch;
  uint32_t slot_id, reserved;
} mkg_gpu_lease;

typedef struct mkg_gpu_source {
  uint32_t size, version;
  uint64_t image;
  uint32_t width, height, format, tiling;
  uint32_t usage, sample_count, layout, queue_family, origin, reserved;
} mkg_gpu_source;

typedef struct mkg_gpu_plane {
  uint32_t fd_index, stride;
  uint64_t offset, size;
} mkg_gpu_plane;

typedef struct mkg_gpu_frame_descriptor {
  uint32_t size, version;
  mkg_gpu_lease lease;
  uint32_t width, height, format, fourcc;
  uint32_t plane_count, origin, layout, queue_family;
  uint64_t modifier, allocation_bytes, queried_subresource_bytes;
  uint64_t gpu_blits, cpu_pixel_readbacks, queue_idle_waits;
  mkg_gpu_plane planes[MKG_GPU_MAX_PACKET_FDS];
} mkg_gpu_frame_descriptor;

typedef struct mkg_gpu_pool_state {
  uint32_t size, version;
  uint32_t free_slots, reserved_slots, ready_slots, exported_slots;
  uint32_t quarantined, reserved;
  uint64_t allocation_bytes, gpu_blits, cpu_pixel_readbacks, queue_idle_waits;
} mkg_gpu_pool_state;

uint32_t mkg_gpu_abi_version(void);

// Caller owns every Vulkan context handle and serializes ALL queue/Skia work,
// including calls on each pool. Destroy/close cannot race any other call.
// Context and source surface must remain alive through synchronous pool calls.
mkg_gpu_status mkg_gpu_pool_create_borrowed(
    const mkg_gpu_borrowed_context *context, const mkg_gpu_pool_config *config,
    mkg_gpu_pool **out_pool, mkg_gpu_pool_info *out_info, mkg_gpu_error *error);
mkg_gpu_status mkg_gpu_pool_try_acquire(mkg_gpu_pool *pool, uint64_t frame_seq,
                                        mkg_gpu_lease *out_lease,
                                        mkg_gpu_error *error);
// Source must already be flushed/submitted in TRANSFER_SRC_OPTIMAL on the same
// queue family. One nearest blit may flip Y. Source layout/family is unchanged.
mkg_gpu_status mkg_gpu_pool_blit(mkg_gpu_pool *pool, const mkg_gpu_lease *lease,
                                 const mkg_gpu_source *source,
                                 mkg_gpu_error *error);
// Success atomically enters ExportedAwaitRelease BEFORE exposing the new FD.
// Caller owns that FD and closes it after sendmsg. Never export a lease twice.
mkg_gpu_status mkg_gpu_pool_export(mkg_gpu_pool *pool,
                                   const mkg_gpu_lease *lease,
                                   mkg_gpu_frame_descriptor *out_descriptor,
                                   int32_t *out_fd, mkg_gpu_error *error);
// Only Reserved/Ready leases with no externally exported FD can be cancelled.
mkg_gpu_status mkg_gpu_pool_cancel_unexported(mkg_gpu_pool *pool,
                                              const mkg_gpu_lease *lease,
                                              mkg_gpu_error *error);
// Caller must have validated the true consumer allReferencesReleased message.
// A socket write, timeout or JS callback alone never authorizes this call.
mkg_gpu_status mkg_gpu_pool_consumer_released(mkg_gpu_pool *pool,
                                              const mkg_gpu_lease *lease,
                                              mkg_gpu_error *error);
mkg_gpu_status mkg_gpu_pool_read_state(mkg_gpu_pool *pool,
                                       mkg_gpu_pool_state *out_state,
                                       mkg_gpu_error *error);
mkg_gpu_status mkg_gpu_pool_quarantine(mkg_gpu_pool *pool,
                                       mkg_gpu_error *error);
// Success destroys only pool objects, never borrowed device/context handles.
// Failure retains the pool; caller may retry after valid releases or abandon.
mkg_gpu_status mkg_gpu_pool_destroy_drained(mkg_gpu_pool *pool,
                                            mkg_gpu_error *error);
// Drops producer-local objects after a checked own-queue wait. It does NOT
// prove consumer drain. out_was_drained reports whether every lease was Free.
// DEVICE_LOST permits only local abandonment: returns OK, error.vk_result is
// DEVICE_LOST, out_was_drained is 0. Other wait errors retain the pool.
mkg_gpu_status mkg_gpu_pool_abandon_local(mkg_gpu_pool *pool,
                                          uint32_t *out_was_drained,
                                          mkg_gpu_error *error);

// Independent bounded UNIX SOCK_SEQPACKET helpers. No pool or release
// semantics. Path parent must be a caller-owned private directory (mode 0700).
mkg_gpu_status mkg_gpu_channel_listen(const char *path,
                                      mkg_gpu_channel **out_listener,
                                      mkg_gpu_error *error);
mkg_gpu_status mkg_gpu_channel_accept(mkg_gpu_channel *listener,
                                      mkg_gpu_channel **out_peer,
                                      uint32_t *out_peer_pid,
                                      mkg_gpu_error *error);
mkg_gpu_status mkg_gpu_channel_send(mkg_gpu_channel *peer, const void *bytes,
                                    uint32_t byte_count, const int32_t *fds,
                                    uint32_t fd_count, mkg_gpu_error *error);
// Consumer->producer control has NO FD attachments. Invalid/truncated packets
// close all attached FDs and fail. out_byte_count is 0 on WouldBlock/error.
mkg_gpu_status mkg_gpu_channel_receive_control(mkg_gpu_channel *peer,
                                               void *bytes, uint32_t capacity,
                                               uint32_t *out_byte_count,
                                               mkg_gpu_error *error);
mkg_gpu_status mkg_gpu_channel_close(mkg_gpu_channel *channel,
                                     mkg_gpu_error *error);

#ifdef __cplusplus
}
#endif
#endif
