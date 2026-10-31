// Real GPU lifecycle check. No importing GPU consumer, pixel readback or fps
// claim. Reuse the standalone P0's owned context only as the borrowed-device
// fixture.
#define main MakoStandaloneProducerFixtureEntry
#include "producer.cc"
#undef main
#include <filesystem>

namespace {
template <class T> T Abi() {
  T value{};
  value.size = sizeof(T);
  value.version = MKG_GPU_ABI_VERSION;
  return value;
}
template <class T> uint64_t Bits(T value) {
  uint64_t bits{};
  static_assert(sizeof(value) == sizeof(bits));
  std::memcpy(&bits, &value, sizeof(bits));
  return bits;
}
void Require(bool value, const char *what) {
  if (!value)
    throw std::runtime_error(what);
}
void Expect(mkg_gpu_status got, mkg_gpu_status wanted,
            const mkg_gpu_error &error) {
  if (got != wanted)
    throw std::runtime_error("C ABI status=" + std::to_string(got) +
                             " wanted=" + std::to_string(wanted) + " " +
                             error.message);
}
struct NativePool {
  mkg_gpu_pool *value = nullptr;
  ~NativePool() {
    if (value) {
      uint32_t ignored;
      mkg_gpu_pool_abandon_local(value, &ignored, nullptr);
    }
  }
};
struct Source {
  mkg::Context context;
  VkImage image{};
  VkDeviceMemory memory{};
  VkCommandPool commands{};
  explicit Source(mkg::Context c) : context(c) {
    VkImageCreateInfo create{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    create.imageType = VK_IMAGE_TYPE_2D;
    create.format = VK_FORMAT_B8G8R8A8_UNORM;
    create.extent = {128, 64, 1};
    create.mipLevels = 1;
    create.arrayLayers = 1;
    create.samples = VK_SAMPLE_COUNT_1_BIT;
    create.tiling = VK_IMAGE_TILING_OPTIMAL;
    create.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
                   VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
                   VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    create.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    Check(vkCreateImage(c.device, &create, nullptr, &image),
          "create optimal source");
    VkMemoryRequirements requirement{};
    vkGetImageMemoryRequirements(c.device, image, &requirement);
    VkPhysicalDeviceMemoryProperties properties{};
    vkGetPhysicalDeviceMemoryProperties(c.physical, &properties);
    uint32_t type = UINT32_MAX;
    for (uint32_t i = 0; i < properties.memoryTypeCount; i++)
      if ((requirement.memoryTypeBits & (1u << i)) &&
          (properties.memoryTypes[i].propertyFlags &
           VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)) {
        type = i;
        break;
      }
    Require(type != UINT32_MAX, "source device-local memory unavailable");
    VkMemoryAllocateInfo allocate{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    allocate.allocationSize = requirement.size;
    allocate.memoryTypeIndex = type;
    Check(vkAllocateMemory(c.device, &allocate, nullptr, &memory),
          "allocate source");
    Check(vkBindImageMemory(c.device, image, memory, 0), "bind source");
    VkCommandPoolCreateInfo pool{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    pool.queueFamilyIndex = c.family;
    Check(vkCreateCommandPool(c.device, &pool, nullptr, &commands),
          "source commands");
    Clear(false);
  }
  void Clear(bool reuse) {
    VkCommandBufferAllocateInfo allocate{
        VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    allocate.commandPool = commands;
    allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    allocate.commandBufferCount = 1;
    VkCommandBuffer command{};
    Check(vkAllocateCommandBuffers(context.device, &allocate, &command),
          "source allocate");
    VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    Check(vkBeginCommandBuffer(command, &begin), "source begin");
    VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
    barrier.oldLayout = reuse ? VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL
                              : VK_IMAGE_LAYOUT_UNDEFINED;
    barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier.srcAccessMask = reuse ? VK_ACCESS_TRANSFER_READ_BIT : 0;
    barrier.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    barrier.image = image;
    barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    vkCmdPipelineBarrier(command,
                         reuse ? VK_PIPELINE_STAGE_TRANSFER_BIT
                               : VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0,
                         nullptr, 1, &barrier);
    VkClearColorValue color{{1, 0.25f, 0.5f, 1}};
    vkCmdClearColorImage(command, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                         &color, 1, &barrier.subresourceRange);
    barrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    barrier.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TRANSFER_BIT,
                         VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0,
                         nullptr, 1, &barrier);
    Check(vkEndCommandBuffer(command), "source end");
    VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    submit.commandBufferCount = 1;
    submit.pCommandBuffers = &command;
    Check(vkQueueSubmit(context.queue, 1, &submit, VK_NULL_HANDLE),
          "source submit");
    Check(vkQueueWaitIdle(context.queue), "source completion");
    vkFreeCommandBuffers(context.device, commands, 1, &command);
  }
  ~Source() {
    vkQueueWaitIdle(context.queue);
    vkDestroyCommandPool(context.device, commands, nullptr);
    vkDestroyImage(context.device, image, nullptr);
    vkFreeMemory(context.device, memory, nullptr);
  }
};
struct Channel {
  mkg_gpu_channel *value{};
  ~Channel() {
    if (value)
      mkg_gpu_channel_close(value, nullptr);
  }
};
size_t FdCount() {
  size_t count = 0;
  for ([[maybe_unused]] const auto &entry :
       std::filesystem::directory_iterator("/proc/self/fd"))
    count++;
  return count;
}
} // namespace

int main() {
  try {
    Require(mkg_gpu_abi_version() == MKG_GPU_ABI_VERSION, "ABI version");
    Gpu fixture;
    fixture.Init(128, 64, 1);
    Source sourceImage(fixture.Core());
    auto context = Abi<mkg_gpu_borrowed_context>();
    context.instance = Bits(fixture.instance);
    context.physical_device = Bits(fixture.physical);
    context.device = Bits(fixture.device);
    context.queue = Bits(fixture.queue);
    context.queue_family = fixture.family;
    context.enabled_extensions = MKG_GPU_REQUIRED_EXTENSIONS;
    auto config = Abi<mkg_gpu_pool_config>();
    config.width = 128;
    config.height = 64;
    config.slot_count = 3;
    config.producer_epoch = 101;
    config.pool_generation = 1;
    config.max_allocation_bytes = 32 * 1024 * 1024;
    auto info = Abi<mkg_gpu_pool_info>();
    auto error = Abi<mkg_gpu_error>();
    NativePool pool;
    auto bad = config;
    bad.max_allocation_bytes = 1;
    Expect(mkg_gpu_pool_create_borrowed(&context, &bad, &pool.value, &info,
                                        &error),
           MKG_GPU_OUT_OF_MEMORY, error);
    Require(!pool.value, "failed create retained no pool");
    Expect(mkg_gpu_pool_create_borrowed(&context, &config, &pool.value, &info,
                                        &error),
           MKG_GPU_OK, error);
    Require(info.slot_count == 3 &&
                info.allocation_bytes <= config.max_allocation_bytes &&
                info.modifier == 0 && info.drm_render_major == 226,
            "actual pool capabilities");
    auto source = Abi<mkg_gpu_source>();
    source.image = Bits(sourceImage.image);
    source.width = 128;
    source.height = 64;
    source.format = VK_FORMAT_B8G8R8A8_UNORM;
    source.tiling = VK_IMAGE_TILING_OPTIMAL;
    source.usage = VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    source.sample_count = 1;
    source.layout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    source.queue_family = fixture.family;
    source.origin = MKG_GPU_BOTTOM_LEFT;
    std::array<mkg_gpu_lease, 3> leases;
    for (uint32_t i = 0; i < 3; i++) {
      leases[i] = Abi<mkg_gpu_lease>();
      Expect(mkg_gpu_pool_try_acquire(pool.value, i + 1, &leases[i], &error),
             MKG_GPU_OK, error);
    }
    auto extra = Abi<mkg_gpu_lease>();
    Expect(mkg_gpu_pool_try_acquire(pool.value, 4, &extra, &error),
           MKG_GPU_NO_FREE_SLOT, error);
    auto wrong = source;
    wrong.layout = VK_IMAGE_LAYOUT_GENERAL;
    Expect(mkg_gpu_pool_blit(pool.value, &leases[0], &wrong, &error),
           MKG_GPU_INVALID_ARGUMENT, error);
    std::array<int32_t, 3> fds{-1, -1, -1};
    for (uint32_t i = 0; i < 3; i++) {
      Expect(mkg_gpu_pool_blit(pool.value, &leases[i], &source, &error),
             MKG_GPU_OK, error);
      auto descriptor = Abi<mkg_gpu_frame_descriptor>();
      Expect(mkg_gpu_pool_export(pool.value, &leases[i], &descriptor, &fds[i],
                                 &error),
             MKG_GPU_OK, error);
      Require(fds[i] >= 0 && (fcntl(fds[i], F_GETFD) & FD_CLOEXEC) &&
                  descriptor.gpu_blits == i + 1 &&
                  descriptor.cpu_pixel_readbacks == 0 &&
                  descriptor.planes[0].stride >= 128 * 4 &&
                  descriptor.lease.slot_epoch == leases[i].slot_epoch,
              "real exported descriptor");
      int32_t duplicate = 99;
      Expect(mkg_gpu_pool_export(pool.value, &leases[i], &descriptor,
                                 &duplicate, &error),
             MKG_GPU_STALE_LEASE, error);
      Require(duplicate == -1, "failed export exposes no fd");
      Expect(mkg_gpu_pool_cancel_unexported(pool.value, &leases[i], &error),
             MKG_GPU_STALE_LEASE, error);
    }
    Expect(mkg_gpu_pool_destroy_drained(pool.value, &error),
           MKG_GPU_NOT_DRAINED, error);

    char temporary[] = "/tmp/mako-gpu-api-XXXXXX";
    Require(mkdtemp(temporary) != nullptr, "private channel directory");
    struct Directory {
      std::string path;
      ~Directory() { rmdir(path.c_str()); }
    } directory{temporary};
    Channel listener, peer;
    std::string socketPath = std::string(temporary) + "/control.sock";
    Expect(mkg_gpu_channel_listen(socketPath.c_str(), &listener.value, &error),
           MKG_GPU_OK, error);
    uint32_t peerPid = 0;
    Expect(
        mkg_gpu_channel_accept(listener.value, &peer.value, &peerPid, &error),
        MKG_GPU_WOULD_BLOCK, error);
    UniqueFd client(socket(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC, 0));
    sockaddr_un address{};
    address.sun_family = AF_UNIX;
    std::memcpy(address.sun_path, socketPath.c_str(), socketPath.size() + 1);
    Require(connect(client.fd, reinterpret_cast<sockaddr *>(&address),
                    sizeof(address)) == 0,
            "connect API channel");
    Expect(
        mkg_gpu_channel_accept(listener.value, &peer.value, &peerPid, &error),
        MKG_GPU_OK, error);
    Require(peerPid == static_cast<uint32_t>(getpid()), "verified peer pid");
    std::string metadata = "{\"type\":\"native-check\"}";
    Expect(mkg_gpu_channel_send(peer.value, metadata.data(), metadata.size(),
                                &fds[0], 1, &error),
           MKG_GPU_OK, error);
    {
      auto received = mkg::ipc::Receive(client.fd);
      Require(received.available && received.bytes == metadata &&
                  received.fds.size() == 1 &&
                  (fcntl(received.fds[0], F_GETFD) & FD_CLOEXEC),
              "production SCM_RIGHTS payload");
    }
    std::array<char, MKG_GPU_MAX_PACKET_BYTES> bytes{};
    uint32_t length = 17;
    Expect(mkg_gpu_channel_receive_control(peer.value, bytes.data(),
                                           bytes.size(), &length, &error),
           MKG_GPU_WOULD_BLOCK, error);
    Require(length == 0, "would block exposes no packet");
    mkg::ipc::Send(client.fd, metadata.data(), metadata.size(), nullptr, 0);
    Expect(mkg_gpu_channel_receive_control(peer.value, bytes.data(),
                                           bytes.size(), &length, &error),
           MKG_GPU_OK, error);
    Require(std::string(bytes.data(), length) == metadata,
            "bounded control payload");
    size_t baselineFds = FdCount();
    mkg::ipc::Send(client.fd, metadata.data(), metadata.size(), &fds[0], 1);
    Expect(mkg_gpu_channel_receive_control(peer.value, bytes.data(),
                                           bytes.size(), &length, &error),
           MKG_GPU_IO_ERROR, error);
    Require(length == 0 && FdCount() == baselineFds,
            "unexpected control FD closed");
    Expect(mkg_gpu_channel_send(peer.value, bytes.data(), bytes.size() + 1,
                                nullptr, 0, &error),
           MKG_GPU_INVALID_ARGUMENT, error);
    for (int fd : fds)
      close(fd); // No actual importing consumer exists in this check.
    auto stale = leases[0];
    stale.slot_epoch--;
    Expect(mkg_gpu_pool_consumer_released(pool.value, &stale, &error),
           MKG_GPU_STALE_LEASE, error);
    for (uint32_t id : {2u, 0u, 1u})
      Expect(mkg_gpu_pool_consumer_released(pool.value, &leases[id], &error),
             MKG_GPU_OK, error);
    Expect(mkg_gpu_pool_consumer_released(pool.value, &leases[0], &error),
           MKG_GPU_STALE_LEASE, error);
    Expect(mkg_gpu_pool_try_acquire(pool.value, 4, &extra, &error), MKG_GPU_OK,
           error);
    Expect(mkg_gpu_pool_cancel_unexported(pool.value, &extra, &error),
           MKG_GPU_OK, error);
    Expect(mkg_gpu_pool_try_acquire(pool.value, 5, &extra, &error), MKG_GPU_OK,
           error);
    source.origin = MKG_GPU_TOP_LEFT;
    Expect(mkg_gpu_pool_blit(pool.value, &extra, &source, &error), MKG_GPU_OK,
           error);
    Expect(mkg_gpu_pool_cancel_unexported(pool.value, &extra, &error),
           MKG_GPU_OK, error);
    auto state = Abi<mkg_gpu_pool_state>();
    Expect(mkg_gpu_pool_read_state(pool.value, &state, &error), MKG_GPU_OK,
           error);
    Require(state.free_slots == 3 && state.gpu_blits == 4 &&
                state.allocation_bytes == info.allocation_bytes,
            "fixed pool returns to baseline");
    Expect(mkg_gpu_pool_quarantine(pool.value, &error), MKG_GPU_OK, error);
    Expect(mkg_gpu_pool_try_acquire(pool.value, 6, &extra, &error),
           MKG_GPU_QUARANTINED, error);
    Expect(mkg_gpu_pool_destroy_drained(pool.value, &error), MKG_GPU_OK, error);
    pool.value = nullptr;

    config.pool_generation++;
    Expect(mkg_gpu_pool_create_borrowed(&context, &config, &pool.value, &info,
                                        &error),
           MKG_GPU_OK, error);
    Expect(mkg_gpu_pool_try_acquire(pool.value, 1, &extra, &error), MKG_GPU_OK,
           error);
    Expect(mkg_gpu_pool_blit(pool.value, &extra, &source, &error), MKG_GPU_OK,
           error);
    auto descriptor = Abi<mkg_gpu_frame_descriptor>();
    int32_t fd = -1;
    Expect(mkg_gpu_pool_export(pool.value, &extra, &descriptor, &fd, &error),
           MKG_GPU_OK, error);
    close(fd);
    uint32_t wasDrained = 1;
    Expect(mkg_gpu_pool_abandon_local(pool.value, &wasDrained, &error),
           MKG_GPU_OK, error);
    pool.value = nullptr;
    Require(wasDrained == 0, "abandon never claims outstanding consumer drain");
    sourceImage.Clear(
        true); // Library left borrowed context/queue and source state usable.
    std::cout
        << "{\"type\":\"production-native-checks-passed\",\"slots\":3,"
           "\"gpuBlits\":5,\"cpuPixelReadbacks\":0,\"fdOwnership\":true,"
           "\"borrowedDevicePreserved\":true,\"presentationVerified\":false}"
        << std::endl;
    return 0;
  } catch (const std::exception &failure) {
    std::cerr << failure.what() << std::endl;
    return 1;
  }
}
