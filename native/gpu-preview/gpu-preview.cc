#include "gpu-preview.h"
#include "gpu-core.h"
#include "ipc-core.h"
#include <array>
#include <cerrno>
#include <cstring>
#include <fcntl.h>
#include <memory>
#include <new>
#include <string>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

namespace {
enum class SlotState { Free, Reserved, Ready, Exported };
struct Slot : mkg::Image {
  SlotState state = SlotState::Free;
  mkg_gpu_lease lease{};
  uint64_t epoch = 0;
};
template <class T> void Validate(const T *value) {
  if (!value || value->size != sizeof(T) ||
      value->version != MKG_GPU_ABI_VERSION)
    throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT,
                       "ABI struct size/version mismatch");
}
template <class T> void Reset(T &value) {
  value = {};
  value.size = sizeof(T);
  value.version = MKG_GPU_ABI_VERSION;
}
void SetError(mkg_gpu_error *error, mkg_gpu_status status, int32_t vk,
              const char *message) noexcept {
  if (!error || error->size != sizeof(*error) ||
      error->version != MKG_GPU_ABI_VERSION)
    return;
  error->status = status;
  error->vk_result = vk;
  std::strncpy(error->message, message, sizeof(error->message) - 1);
  error->message[sizeof(error->message) - 1] = 0;
}
template <class F>
mkg_gpu_status Guard(mkg_gpu_error *error, F operation) noexcept {
  SetError(error, MKG_GPU_OK, 0, "");
  try {
    return operation();
  } catch (const mkg::Failure &failure) {
    SetError(error, failure.status, failure.result, failure.what());
    return failure.status;
  } catch (const mkg::ipc::Failure &failure) {
    SetError(error, failure.status, 0, failure.what());
    return failure.status;
  } catch (const std::bad_alloc &) {
    SetError(error, MKG_GPU_OUT_OF_MEMORY, 0, "native host allocation failed");
    return MKG_GPU_OUT_OF_MEMORY;
  } catch (const std::exception &failure) {
    SetError(error, MKG_GPU_INTERNAL_ERROR, 0, failure.what());
    return MKG_GPU_INTERNAL_ERROR;
  } catch (...) {
    SetError(error, MKG_GPU_INTERNAL_ERROR, 0, "unknown native failure");
    return MKG_GPU_INTERNAL_ERROR;
  }
}
template <class T> T Handle(uint64_t bits) {
  static_assert(sizeof(T) == sizeof(bits));
  T handle;
  std::memcpy(&handle, &bits, sizeof(handle));
  return handle;
}
} // namespace

struct mkg_gpu_pool {
  mkg::Context context{};
  mkg_gpu_pool_config config{};
  mkg_gpu_pool_info info{};
  std::array<Slot, MKG_GPU_SLOT_COUNT> slots{};
  VkCommandPool commands{};
  bool quarantined = false;
  uint32_t cursor = 0;
  uint64_t lastFrame = 0, blits = 0, waits = 0;
  ~mkg_gpu_pool() {
    if (commands)
      vkDestroyCommandPool(context.device, commands, nullptr);
    for (auto &slot : slots)
      mkg::DestroyImage(context, slot);
  }
  void Usable() const {
    if (quarantined)
      throw mkg::Failure(MKG_GPU_QUARANTINED,
                         "pool quarantined; no new writes or exports");
  }
  Slot &Find(const mkg_gpu_lease *lease) {
    Validate(lease);
    if (lease->slot_id >= slots.size())
      throw mkg::Failure(MKG_GPU_STALE_LEASE, "lease slot out of range");
    Slot &slot = slots[lease->slot_id];
    const auto &current = slot.lease;
    if (lease->producer_epoch != current.producer_epoch ||
        lease->pool_generation != current.pool_generation ||
        lease->frame_seq != current.frame_seq ||
        lease->slot_epoch != current.slot_epoch ||
        slot.state == SlotState::Free)
      throw mkg::Failure(MKG_GPU_STALE_LEASE,
                         "stale or already released lease");
    return slot;
  }
  bool Drained() const {
    for (const auto &slot : slots)
      if (slot.state != SlotState::Free)
        return false;
    return true;
  }
};
struct mkg_gpu_channel {
  int fd = -1;
  bool listener = false;
  std::string path;
  dev_t pathDevice{};
  ino_t pathInode{};
  ~mkg_gpu_channel() {
    if (fd >= 0)
      close(fd);
    if (!path.empty()) {
      struct stat current{};
      if (lstat(path.c_str(), &current) == 0 && current.st_dev == pathDevice &&
          current.st_ino == pathInode && S_ISSOCK(current.st_mode))
        unlink(path.c_str());
    }
  }
};

extern "C" uint32_t mkg_gpu_abi_version(void) { return MKG_GPU_ABI_VERSION; }

extern "C" mkg_gpu_status mkg_gpu_pool_create_borrowed(
    const mkg_gpu_borrowed_context *context, const mkg_gpu_pool_config *config,
    mkg_gpu_pool **outPool, mkg_gpu_pool_info *outInfo, mkg_gpu_error *error) {
  if (outPool)
    *outPool = nullptr;
  return Guard(error, [&]() {
    Validate(context);
    Validate(config);
    Validate(outInfo);
    if (!outPool || !context->instance || !context->physical_device ||
        !context->device || !context->queue ||
        (context->enabled_extensions & MKG_GPU_REQUIRED_EXTENSIONS) !=
            MKG_GPU_REQUIRED_EXTENSIONS ||
        config->width < 64 || config->height < 64 || config->width > 2560 ||
        config->height > 1440 || config->slot_count != MKG_GPU_SLOT_COUNT ||
        !config->producer_epoch || !config->pool_generation ||
        !config->max_allocation_bytes ||
        config->max_allocation_bytes > mkg::kAbsolutePoolBudget ||
        config->reserved)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT,
                         "invalid borrowed context/pool configuration");
    auto pool = std::make_unique<mkg_gpu_pool>();
    pool->context = {Handle<VkInstance>(context->instance),
                     Handle<VkPhysicalDevice>(context->physical_device),
                     Handle<VkDevice>(context->device),
                     Handle<VkQueue>(context->queue), context->queue_family};
    pool->config = *config;
    Reset(pool->info);
    mkg::QueryCapabilities(pool->context, config->width, config->height, true,
                           &pool->info);
    for (auto &slot : pool->slots) {
      mkg::AllocateImage(
          pool->context, config->width, config->height,
          config->max_allocation_bytes - pool->info.allocation_bytes, slot);
      pool->info.allocation_bytes += slot.allocation;
    }
    pool->info.slot_count = MKG_GPU_SLOT_COUNT;
    VkCommandPoolCreateInfo create{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    create.flags = VK_COMMAND_POOL_CREATE_TRANSIENT_BIT;
    create.queueFamilyIndex = context->queue_family;
    mkg::Check(vkCreateCommandPool(pool->context.device, &create, nullptr,
                                   &pool->commands),
               "create preview command pool");
    *outInfo = pool->info;
    *outPool = pool.release();
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status mkg_gpu_pool_try_acquire(mkg_gpu_pool *pool,
                                                   uint64_t frameSeq,
                                                   mkg_gpu_lease *outLease,
                                                   mkg_gpu_error *error) {
  return Guard(error, [&]() {
    Validate(outLease);
    if (!pool || !frameSeq || frameSeq <= pool->lastFrame)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT,
                         "frame sequence must increase");
    pool->Usable();
    for (uint32_t offset = 0; offset < pool->slots.size(); offset++) {
      uint32_t id = (pool->cursor + offset) % pool->slots.size();
      auto &slot = pool->slots[id];
      if (slot.state != SlotState::Free)
        continue;
      if (slot.epoch == UINT64_MAX) {
        pool->quarantined = true;
        throw mkg::Failure(MKG_GPU_QUARANTINED, "slot epoch exhausted");
      }
      Reset(slot.lease);
      slot.lease.producer_epoch = pool->config.producer_epoch;
      slot.lease.pool_generation = pool->config.pool_generation;
      slot.lease.frame_seq = frameSeq;
      slot.lease.slot_epoch = ++slot.epoch;
      slot.lease.slot_id = id;
      slot.state = SlotState::Reserved;
      pool->lastFrame = frameSeq;
      pool->cursor = (id + 1) % pool->slots.size();
      *outLease = slot.lease;
      return MKG_GPU_OK;
    }
    return MKG_GPU_NO_FREE_SLOT;
  });
}
extern "C" mkg_gpu_status mkg_gpu_pool_blit(mkg_gpu_pool *pool,
                                            const mkg_gpu_lease *lease,
                                            const mkg_gpu_source *source,
                                            mkg_gpu_error *error) {
  return Guard(error, [&]() {
    Validate(source);
    if (!pool)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null pool");
    pool->Usable();
    Slot &slot = pool->Find(lease);
    if (slot.state != SlotState::Reserved || !source->image ||
        source->width != pool->config.width ||
        source->height != pool->config.height ||
        source->format != VK_FORMAT_B8G8R8A8_UNORM ||
        source->tiling != VK_IMAGE_TILING_OPTIMAL ||
        !(source->usage & VK_IMAGE_USAGE_TRANSFER_SRC_BIT) ||
        source->sample_count != VK_SAMPLE_COUNT_1_BIT ||
        source->layout != VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL ||
        source->queue_family != pool->context.family ||
        source->origin > MKG_GPU_BOTTOM_LEFT || source->reserved)
      throw mkg::Failure(
          MKG_GPU_INVALID_ARGUMENT,
          "source state/format or lease not suitable for one blit");
    try {
      VkCommandBufferAllocateInfo allocate{
          VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
      allocate.commandPool = pool->commands;
      allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
      allocate.commandBufferCount = 1;
      VkCommandBuffer command{};
      mkg::Check(
          vkAllocateCommandBuffers(pool->context.device, &allocate, &command),
          "allocate blit command");
      VkCommandBufferBeginInfo begin{
          VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
      begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
      mkg::Check(vkBeginCommandBuffer(command, &begin), "begin blit command");
      mkg::Acquire(
          command, pool->context, slot, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
          VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
      VkImageBlit region{};
      region.srcSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
      region.dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
      bool flip = source->origin == MKG_GPU_BOTTOM_LEFT;
      region.srcOffsets[0] = {0, flip ? int32_t(source->height) : 0, 0};
      region.srcOffsets[1] = {int32_t(source->width),
                              flip ? 0 : int32_t(source->height), 1};
      region.dstOffsets[1] = {int32_t(source->width), int32_t(source->height),
                              1};
      vkCmdBlitImage(command, Handle<VkImage>(source->image),
                     VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, slot.image,
                     VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region,
                     VK_FILTER_NEAREST);
      mkg::Release(
          command, pool->context, slot, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
          VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
      mkg::Check(vkEndCommandBuffer(command), "end blit command");
      VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
      submit.commandBufferCount = 1;
      submit.pCommandBuffers = &command;
      mkg::Check(vkQueueSubmit(pool->context.queue, 1, &submit, VK_NULL_HANDLE),
                 "submit preview blit");
      mkg::Check(vkQueueWaitIdle(pool->context.queue),
                 "wait preview blit completion");
      pool->waits++;
      pool->blits++;
      vkFreeCommandBuffers(pool->context.device, pool->commands, 1, &command);
      slot.external = true;
      slot.state = SlotState::Ready;
      return MKG_GPU_OK;
    } catch (...) {
      pool->quarantined = true;
      throw;
    }
  });
}
extern "C" mkg_gpu_status
mkg_gpu_pool_export(mkg_gpu_pool *pool, const mkg_gpu_lease *lease,
                    mkg_gpu_frame_descriptor *descriptor, int32_t *outFd,
                    mkg_gpu_error *error) {
  if (outFd)
    *outFd = -1;
  return Guard(error, [&]() {
    Validate(descriptor);
    if (!pool || !outFd)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null pool/export output");
    pool->Usable();
    Slot &slot = pool->Find(lease);
    if (slot.state != SlotState::Ready)
      throw mkg::Failure(MKG_GPU_STALE_LEASE,
                         "lease is not ready or already exported");
    int fd = mkg::ExportFd(pool->context, slot);
    Reset(*descriptor);
    descriptor->lease = slot.lease;
    descriptor->width = pool->config.width;
    descriptor->height = pool->config.height;
    descriptor->format = VK_FORMAT_B8G8R8A8_UNORM;
    descriptor->fourcc =
        0x34325241u; // DRM_FORMAT_ARGB8888: little-endian BGRA.
    descriptor->plane_count = 1;
    descriptor->origin = MKG_GPU_TOP_LEFT;
    descriptor->layout = VK_IMAGE_LAYOUT_GENERAL;
    descriptor->queue_family = VK_QUEUE_FAMILY_FOREIGN_EXT;
    descriptor->modifier = 0;
    descriptor->allocation_bytes = slot.allocation;
    descriptor->queried_subresource_bytes = slot.size;
    descriptor->gpu_blits = pool->blits;
    descriptor->queue_idle_waits = pool->waits;
    descriptor->planes[0] = {0, static_cast<uint32_t>(slot.stride), slot.offset,
                             slot.allocation};
    slot.state = SlotState::Exported;
    *outFd = fd;
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status
mkg_gpu_pool_cancel_unexported(mkg_gpu_pool *pool, const mkg_gpu_lease *lease,
                               mkg_gpu_error *error) {
  return Guard(error, [&]() {
    if (!pool)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null pool");
    pool->Usable();
    Slot &slot = pool->Find(lease);
    if (slot.state != SlotState::Reserved && slot.state != SlotState::Ready)
      throw mkg::Failure(MKG_GPU_STALE_LEASE,
                         "exported lease cannot be cancelled");
    slot.state = SlotState::Free;
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status
mkg_gpu_pool_consumer_released(mkg_gpu_pool *pool, const mkg_gpu_lease *lease,
                               mkg_gpu_error *error) {
  return Guard(error, [&]() {
    if (!pool)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null pool");
    Slot &slot = pool->Find(lease);
    if (slot.state != SlotState::Exported)
      throw mkg::Failure(MKG_GPU_STALE_LEASE,
                         "only an exported lease may be released");
    slot.state = SlotState::Free;
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status mkg_gpu_pool_read_state(mkg_gpu_pool *pool,
                                                  mkg_gpu_pool_state *state,
                                                  mkg_gpu_error *error) {
  return Guard(error, [&]() {
    Validate(state);
    if (!pool)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null pool");
    Reset(*state);
    for (const auto &slot : pool->slots) {
      if (slot.state == SlotState::Free)
        state->free_slots++;
      if (slot.state == SlotState::Reserved)
        state->reserved_slots++;
      if (slot.state == SlotState::Ready)
        state->ready_slots++;
      if (slot.state == SlotState::Exported)
        state->exported_slots++;
    }
    state->quarantined = pool->quarantined;
    state->allocation_bytes = pool->info.allocation_bytes;
    state->gpu_blits = pool->blits;
    state->queue_idle_waits = pool->waits;
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status mkg_gpu_pool_quarantine(mkg_gpu_pool *pool,
                                                  mkg_gpu_error *error) {
  return Guard(error, [&]() {
    if (!pool)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null pool");
    pool->quarantined = true;
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status mkg_gpu_pool_destroy_drained(mkg_gpu_pool *pool,
                                                       mkg_gpu_error *error) {
  return Guard(error, [&]() {
    if (!pool)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null pool");
    if (!pool->Drained())
      return MKG_GPU_NOT_DRAINED;
    mkg::Check(vkQueueWaitIdle(pool->context.queue),
               "checked drained pool shutdown");
    delete pool;
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status mkg_gpu_pool_abandon_local(mkg_gpu_pool *pool,
                                                     uint32_t *wasDrained,
                                                     mkg_gpu_error *error) {
  if (wasDrained)
    *wasDrained = 0;
  return Guard(error, [&]() {
    if (!pool || !wasDrained)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null pool/abandon output");
    pool->quarantined = true;
    VkResult waited = vkQueueWaitIdle(pool->context.queue);
    if (waited != VK_SUCCESS && waited != VK_ERROR_DEVICE_LOST)
      mkg::Check(waited, "checked local abandon wait");
    *wasDrained = waited == VK_SUCCESS && pool->Drained();
    if (waited == VK_ERROR_DEVICE_LOST)
      SetError(
          error, MKG_GPU_OK, waited,
          "device lost: local objects abandoned; consumer drain unverified");
    delete pool;
    return MKG_GPU_OK;
  });
}

extern "C" mkg_gpu_status mkg_gpu_channel_listen(const char *path,
                                                 mkg_gpu_channel **outListener,
                                                 mkg_gpu_error *error) {
  if (outListener)
    *outListener = nullptr;
  return Guard(error, [&]() {
    if (!path || !outListener)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null listener arguments");
    size_t length = strnlen(path, sizeof(sockaddr_un::sun_path));
    std::string name(path, length);
    size_t slash = name.rfind('/');
    if (length == 0 || length >= sizeof(sockaddr_un::sun_path) ||
        name.front() != '/' || slash == std::string::npos ||
        slash == length - 1)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT,
                         "invalid absolute private socket path");
    std::string directory = name.substr(0, slash);
    struct stat parent{};
    if (stat(directory.c_str(), &parent) < 0 || !S_ISDIR(parent.st_mode) ||
        parent.st_uid != getuid() || (parent.st_mode & 0077) != 0)
      throw mkg::Failure(
          MKG_GPU_INVALID_ARGUMENT,
          "socket parent must be caller-owned private directory");
    auto listener = std::make_unique<mkg_gpu_channel>();
    listener->fd =
        socket(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
    if (listener->fd < 0)
      throw mkg::Failure(MKG_GPU_IO_ERROR, "create listener socket failed");
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    std::memcpy(address.sun_path, name.c_str(), length + 1);
    if (bind(listener->fd, reinterpret_cast<sockaddr *>(&address),
             sizeof(address)) < 0)
      throw mkg::Failure(MKG_GPU_IO_ERROR,
                         "bind listener failed; existing path retained");
    listener->path = name;
    struct stat bound{};
    if (lstat(name.c_str(), &bound) < 0)
      throw mkg::Failure(MKG_GPU_IO_ERROR, "stat bound socket failed");
    listener->pathDevice = bound.st_dev;
    listener->pathInode = bound.st_ino;
    if (chmod(name.c_str(), 0600) < 0 || listen(listener->fd, 1) < 0)
      throw mkg::Failure(MKG_GPU_IO_ERROR, "prepare listener failed");
    listener->listener = true;
    *outListener = listener.release();
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status mkg_gpu_channel_accept(mkg_gpu_channel *listener,
                                                 mkg_gpu_channel **outPeer,
                                                 uint32_t *peerPid,
                                                 mkg_gpu_error *error) {
  if (outPeer)
    *outPeer = nullptr;
  if (peerPid)
    *peerPid = 0;
  return Guard(error, [&]() {
    if (!listener || !listener->listener || !outPeer || !peerPid)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "invalid accept arguments");
    auto peer = std::make_unique<mkg_gpu_channel>();
    peer->fd =
        accept4(listener->fd, nullptr, nullptr, SOCK_CLOEXEC | SOCK_NONBLOCK);
    if (peer->fd < 0 && (errno == EAGAIN || errno == EWOULDBLOCK))
      return MKG_GPU_WOULD_BLOCK;
    if (peer->fd < 0)
      throw mkg::Failure(MKG_GPU_IO_ERROR, "accept failed");
    *peerPid = mkg::ipc::PeerPidSameUid(peer->fd);
    *outPeer = peer.release();
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status
mkg_gpu_channel_send(mkg_gpu_channel *peer, const void *bytes,
                     uint32_t byteCount, const int32_t *fds, uint32_t fdCount,
                     mkg_gpu_error *error) {
  return Guard(error, [&]() {
    if (!peer || peer->listener)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "invalid connected channel");
    return mkg::ipc::Send(peer->fd, bytes, byteCount, fds, fdCount);
  });
}
extern "C" mkg_gpu_status
mkg_gpu_channel_receive_control(mkg_gpu_channel *peer, void *bytes,
                                uint32_t capacity, uint32_t *byteCount,
                                mkg_gpu_error *error) {
  if (byteCount)
    *byteCount = 0;
  return Guard(error, [&]() {
    if (!peer || peer->listener || !bytes || !byteCount ||
        capacity != MKG_GPU_MAX_PACKET_BYTES)
      throw mkg::Failure(
          MKG_GPU_INVALID_ARGUMENT,
          "receive requires connected channel and 16 KiB buffer");
    auto packet = mkg::ipc::Receive(peer->fd);
    if (!packet.available)
      return MKG_GPU_WOULD_BLOCK;
    if (!packet.fds.empty())
      throw mkg::Failure(MKG_GPU_IO_ERROR,
                         "control packets must not contain FDs");
    std::memcpy(bytes, packet.bytes.data(), packet.bytes.size());
    *byteCount = packet.bytes.size();
    return MKG_GPU_OK;
  });
}
extern "C" mkg_gpu_status mkg_gpu_channel_close(mkg_gpu_channel *channel,
                                                mkg_gpu_error *error) {
  return Guard(error, [&]() {
    if (!channel)
      throw mkg::Failure(MKG_GPU_INVALID_ARGUMENT, "null channel");
    delete channel;
    return MKG_GPU_OK;
  });
}
