#include "draw-plugin.h"
#include "gpu-core.h"
#include "ipc-core.h"
#include <algorithm>
#include <array>
#include <chrono>
#include <cstring>
#include <dlfcn.h>
#include <fcntl.h>
#include <iostream>
#include <map>
#include <poll.h>
#include <signal.h>
#include <sstream>
#include <stdexcept>
#include <string>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>
#include <vector>
#include <vulkan/vulkan.h>

namespace {
void Check(VkResult r, const char *what) {
  if (r != VK_SUCCESS)
    throw std::runtime_error(std::string(what) +
                             ": VkResult=" + std::to_string(r));
}
std::string Quote(const std::string &s) {
  std::ostringstream o;
  o << '"';
  for (unsigned char c : s) {
    if (c == '"' || c == '\\')
      o << '\\' << c;
    else if (c >= 32)
      o << c;
  }
  o << '"';
  return o.str();
}
struct UniqueFd {
  int fd = -1;
  UniqueFd() = default;
  explicit UniqueFd(int f) : fd(f) {}
  ~UniqueFd() {
    if (fd >= 0)
      close(fd);
  }
  UniqueFd(const UniqueFd &) = delete;
  UniqueFd &operator=(const UniqueFd &) = delete;
};
struct Slot : mkg::Image {
  VkImageView view{};
  VkFramebuffer framebuffer{};
  uint64_t epoch = 0;
};
struct Gpu {
  VkInstance instance{};
  VkPhysicalDevice physical{};
  VkDevice device{};
  VkQueue queue{};
  uint32_t family = 0;
  VkCommandPool pool{};
  VkRenderPass pass{};
  std::vector<Slot> slots;
  VkPhysicalDeviceProperties properties{};
  VkPhysicalDeviceDrmPropertiesEXT drm{
      VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRM_PROPERTIES_EXT};
  VkPhysicalDeviceIDProperties ids{
      VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
  PFN_vkGetMemoryFdKHR getFd{};
  MakoGpuPreviewDraw pluginDraw = nullptr;
  MakoGpuPreviewShutdown pluginShutdown = nullptr;
  uint64_t gpuCopies = 0;
  uint32_t width = 0, height = 0;
  VkImageUsageFlags usage =
      VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
      VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
  ~Gpu() {
    if (device) {
      auto bestEffortIdle = [&]() {
        VkResult result = vkDeviceWaitIdle(device);
        if (result != VK_SUCCESS)
          std::cerr << "{\"type\":\"cleanup-failed\",\"operation\":"
                       "\"vkDeviceWaitIdle\",\"vkResult\":"
                    << result << ",\"drainVerified\":false}" << std::endl;
      };
      bestEffortIdle();
      if (pluginShutdown) {
        pluginShutdown();
        pluginShutdown = nullptr;
        bestEffortIdle();
      }
    }
    DestroyObjects();
  }
  void Shutdown() {
    Check(vkDeviceWaitIdle(device), "checked producer shutdown");
    if (pluginShutdown) {
      pluginShutdown();
      pluginShutdown = nullptr;
      Check(vkDeviceWaitIdle(device), "checked plugin shutdown");
    }
    DestroyObjects();
  }
  void DestroyObjects() {
    if (device) {
      for (auto &s : slots) {
        if (s.framebuffer)
          vkDestroyFramebuffer(device, s.framebuffer, nullptr);
        if (s.view)
          vkDestroyImageView(device, s.view, nullptr);
        mkg::DestroyImage(Core(), s);
      }
      if (pass)
        vkDestroyRenderPass(device, pass, nullptr);
      if (pool)
        vkDestroyCommandPool(device, pool, nullptr);
      vkDestroyDevice(device, nullptr);
      device = VK_NULL_HANDLE;
    }
    if (instance) {
      vkDestroyInstance(instance, nullptr);
      instance = VK_NULL_HANDLE;
    }
  }
  mkg::Context Core() const {
    return {instance, physical, device, queue, family};
  }
  void Init(uint32_t w, uint32_t h, uint32_t count) {
    width = w;
    height = h;
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.pApplicationName = "Mako P0 GPU producer";
    app.apiVersion = VK_API_VERSION_1_2;
    VkInstanceCreateInfo ci{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ci.pApplicationInfo = &app;
    Check(vkCreateInstance(&ci, nullptr, &instance), "vkCreateInstance");
    uint32_t n = 0;
    Check(vkEnumeratePhysicalDevices(instance, &n, nullptr), "enumerate count");
    std::vector<VkPhysicalDevice> devices(n);
    Check(vkEnumeratePhysicalDevices(instance, &n, devices.data()),
          "enumerate devices");
    for (auto p : devices) {
      VkPhysicalDeviceProperties prop;
      vkGetPhysicalDeviceProperties(p, &prop);
      if (prop.vendorID == 0x1002 &&
          prop.deviceType != VK_PHYSICAL_DEVICE_TYPE_CPU) {
        physical = p;
        break;
      }
    }
    if (!physical)
      throw std::runtime_error("No AMD hardware physical device; refusing "
                               "software or cross-device fallback");
    uint32_t ec = 0;
    Check(vkEnumerateDeviceExtensionProperties(physical, nullptr, &ec, nullptr),
          "extensions count");
    std::vector<VkExtensionProperties> eps(ec);
    Check(vkEnumerateDeviceExtensionProperties(physical, nullptr, &ec,
                                               eps.data()),
          "extensions");
    const char *required[] = {VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME,
                              VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME,
                              VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME,
                              VK_EXT_QUEUE_FAMILY_FOREIGN_EXTENSION_NAME,
                              VK_EXT_PHYSICAL_DEVICE_DRM_EXTENSION_NAME};
    for (auto name : required)
      if (std::none_of(eps.begin(), eps.end(),
                       [&](auto &e) { return !strcmp(e.extensionName, name); }))
        throw std::runtime_error(std::string("Missing extension ") + name);
    VkPhysicalDeviceProperties2 props{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
    props.pNext = &drm;
    drm.pNext = &ids;
    vkGetPhysicalDeviceProperties2(physical, &props);
    properties = props.properties;
    if (!drm.hasRender)
      throw std::runtime_error("No DRM render-node identity");
    uint32_t qc = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(physical, &qc, nullptr);
    std::vector<VkQueueFamilyProperties> qs(qc);
    vkGetPhysicalDeviceQueueFamilyProperties(physical, &qc, qs.data());
    bool found = false;
    for (uint32_t i = 0; i < qc; i++)
      if (qs[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) {
        family = i;
        found = true;
        break;
      }
    if (!found)
      throw std::runtime_error("No graphics queue");
    float priority = 1;
    VkDeviceQueueCreateInfo qi{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
    qi.queueFamilyIndex = family;
    qi.queueCount = 1;
    qi.pQueuePriorities = &priority;
    VkDeviceCreateInfo dc{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
    dc.queueCreateInfoCount = 1;
    dc.pQueueCreateInfos = &qi;
    dc.enabledExtensionCount = std::size(required);
    dc.ppEnabledExtensionNames = required;
    Check(vkCreateDevice(physical, &dc, nullptr, &device), "vkCreateDevice");
    vkGetDeviceQueue(device, family, 0, &queue);
    getFd = reinterpret_cast<PFN_vkGetMemoryFdKHR>(
        vkGetDeviceProcAddr(device, "vkGetMemoryFdKHR"));
    if (!getFd)
      throw std::runtime_error("vkGetMemoryFdKHR absent");
    mkg::QueryCapabilities(Core(), width, height, false, nullptr);
    VkCommandPoolCreateInfo pc{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    pc.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pc.queueFamilyIndex = family;
    Check(vkCreateCommandPool(device, &pc, nullptr, &pool), "command pool");
    VkAttachmentDescription ad{};
    ad.format = VK_FORMAT_B8G8R8A8_UNORM;
    ad.samples = VK_SAMPLE_COUNT_1_BIT;
    ad.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    ad.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    ad.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    ad.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    ad.initialLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
    ad.finalLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
    VkAttachmentReference ar{0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL};
    VkSubpassDescription sub{};
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1;
    sub.pColorAttachments = &ar;
    VkRenderPassCreateInfo rp{VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO};
    rp.attachmentCount = 1;
    rp.pAttachments = &ad;
    rp.subpassCount = 1;
    rp.pSubpasses = &sub;
    Check(vkCreateRenderPass(device, &rp, nullptr, &pass), "render pass");
    slots.resize(count);
    for (auto &s : slots)
      Allocate(s);
  }
  void Allocate(Slot &s) {
    mkg::AllocateImage(Core(), width, height, mkg::kAbsolutePoolBudget / 3, s);
    VkImageViewCreateInfo iv{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
    iv.image = s.image;
    iv.viewType = VK_IMAGE_VIEW_TYPE_2D;
    iv.format = VK_FORMAT_B8G8R8A8_UNORM;
    iv.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    Check(vkCreateImageView(device, &iv, nullptr, &s.view), "image view");
    VkFramebufferCreateInfo fb{VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO};
    fb.renderPass = pass;
    fb.attachmentCount = 1;
    fb.pAttachments = &s.view;
    fb.width = width;
    fb.height = height;
    fb.layers = 1;
    Check(vkCreateFramebuffer(device, &fb, nullptr, &s.framebuffer),
          "framebuffer");
  }
  void Draw(Slot &s, uint64_t seq) {
    if (pluginDraw) {
      uint32_t copies = 0;
      if (!pluginDraw(instance, physical, device, queue, family, s.image, width,
                      height, seq, s.external, &copies) ||
          copies > 1)
        throw std::runtime_error("GPU draw plugin failed; image state unknown; "
                                 "no publication or reuse");
      Check(vkQueueWaitIdle(queue), "plugin producer completion");
      gpuCopies += copies;
      s.external = true;
      s.epoch++;
      return;
    }
    VkCommandBufferAllocateInfo ca{
        VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    ca.commandPool = pool;
    ca.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ca.commandBufferCount = 1;
    VkCommandBuffer cmd;
    Check(vkAllocateCommandBuffers(device, &ca, &cmd), "allocate command");
    VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    Check(vkBeginCommandBuffer(cmd, &bi), "begin command");
    mkg::Acquire(cmd, Core(), s, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                 VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                 VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
    VkClearValue base{};
    base.color = {{0.12f, 0.12f, 0.12f, 1}};
    VkRenderPassBeginInfo begin{VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO};
    begin.renderPass = pass;
    begin.framebuffer = s.framebuffer;
    begin.renderArea = {{0, 0}, {width, height}};
    begin.clearValueCount = 1;
    begin.pClearValues = &base;
    vkCmdBeginRenderPass(cmd, &begin, VK_SUBPASS_CONTENTS_INLINE);
    auto rect = [&](uint32_t x, uint32_t y, uint32_t w, uint32_t h,
                    std::array<float, 4> color) {
      VkClearAttachment a{};
      a.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
      a.colorAttachment = 0;
      std::copy(color.begin(), color.end(), a.clearValue.color.float32);
      VkClearRect r{
          {{static_cast<int32_t>(x), static_cast<int32_t>(y)}, {w, h}}, 0, 1};
      vkCmdClearAttachments(cmd, 1, &a, 1, &r);
    };
    // RGBA values are GPU clear commands, never host pixel buffers. Distinct
    // corners encode origin.
    rect(0, 0, width / 2, height / 2, {1, 0, 0, 1});
    rect(width / 2, 0, width - width / 2, height / 2, {0, 1, 0, 1});
    rect(0, height / 2, width / 2, height - height / 2, {0, 0, 1, 1});
    rect(width / 2, height / 2, width - width / 2, height - height / 2,
         {1, 1, 1, 1});
    uint32_t band = std::max(1u, height / 8);
    for (uint32_t i = 0; i < 8; i++) {
      float v = i / 7.f;
      rect(i * width / 8, height / 2 - band / 2,
           (i + 1) * width / 8 - i * width / 8, band, {v, v, v, 1});
    }
    rect(width / 4, height * 3 / 4, width / 4, band, {0.5f, 0, 0.5f, 0.5f});
    rect(width / 2, height * 3 / 4, width / 4, band, {0, 0, 0, 0});
    for (uint32_t bit = 0; bit < 16; bit++) {
      bool set = (seq >> bit) & 1;
      rect(bit * width / 16, 0, (bit + 1) * width / 16 - bit * width / 16,
           std::max(1u, height / 16),
           set ? std::array<float, 4>{1, 1, 1, 1}
               : std::array<float, 4>{0, 0, 0, 1});
    }
    vkCmdEndRenderPass(cmd);
    mkg::Release(cmd, Core(), s, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                 VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                 VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
    Check(vkEndCommandBuffer(cmd), "end command");
    VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    submit.commandBufferCount = 1;
    submit.pCommandBuffers = &cmd;
    Check(vkQueueSubmit(queue, 1, &submit, VK_NULL_HANDLE), "queue submit");
    Check(vkQueueWaitIdle(queue), "producer completion");
    vkFreeCommandBuffers(device, pool, 1, &cmd);
    s.external = true;
    s.epoch++;
  }
  int Export(Slot &s) { return mkg::ExportFd(Core(), s); }
  std::string Descriptor(Slot &s, uint32_t id, uint64_t seq, uint64_t epoch) {
    std::ostringstream o;
    o << "{\"type\":\"frame\",\"protocolVersion\":1,\"producerPid\":"
      << getpid() << ",\"producerEpoch\":\"" << epoch
      << "\",\"poolGeneration\":1,\"slotId\":" << id
      << ",\"slotEpoch\":" << s.epoch << ",\"frameSeq\":" << seq
      << ",\"width\":" << width << ",\"height\":" << height
      << ",\"format\":\"BGRA_8888\",\"fourcc\":\"AR24\",\"modifier\":\"0\","
         "\"planes\":[{\"fdIndex\":0,\"stride\":"
      << s.stride << ",\"offset\":" << s.offset << ",\"size\":" << s.allocation
      << "}],\"allocationSize\":" << s.allocation
      << ",\"queriedSubresourceSize\":" << s.size
      << ",\"origin\":\"top-left\",\"alphaMode\":\"premultiplied\","
         "\"colorSpace\":\"srgb\",\"sync\":\"queue-idle-foreign-release\","
         "\"gpu\":{\"name\":"
      << Quote(properties.deviceName) << ",\"vendorId\":" << properties.vendorID
      << ",\"deviceId\":" << properties.deviceID
      << ",\"drmRenderMajor\":" << drm.renderMajor
      << ",\"drmRenderMinor\":" << drm.renderMinor << ",\"deviceUUID\":\"";
    const char *hex = "0123456789abcdef";
    for (auto b : ids.deviceUUID)
      o << hex[b >> 4] << hex[b & 15];
    o << "\"},\"provider\":"
      << (pluginDraw ? "\"skia-plugin\"" : "\"vulkan-clear\"")
      << ",\"counters\":{\"gpuBlits\":" << gpuCopies
      << ",\"cpuPixelReadbacks\":0,"
         "\"queueIdleWaits\":"
      << seq << "}}";
    return o.str();
  }
};
void Send(int socket, const std::string &text, int fd = -1) {
  int32_t attached = fd;
  if (mkg::ipc::Send(socket, text.data(), text.size(),
                     fd < 0 ? nullptr : &attached,
                     fd < 0 ? 0 : 1) != MKG_GPU_OK)
    throw std::runtime_error("bounded send would block; pool quarantined");
}
// Deliberately narrow flat release grammar: unique keys, integers, no arrays or
// nested input.
std::map<std::string, std::string> ParseRelease(const std::string &text) {
  size_t i = 0;
  auto ws = [&]() {
    while (i < text.size() && std::isspace(static_cast<unsigned char>(text[i])))
      i++;
  };
  auto token = [&]() {
    ws();
    std::string value;
    if (i < text.size() && text[i] == '"') {
      i++;
      while (i < text.size() && text[i] != '"') {
        if (text[i] == '\\' || static_cast<unsigned char>(text[i]) < 32)
          throw std::runtime_error("release escapes forbidden");
        value += text[i++];
      }
      if (i >= text.size())
        throw std::runtime_error("release string unterminated");
      i++;
    } else {
      while (i < text.size() && text[i] >= '0' && text[i] <= '9')
        value += text[i++];
      if (value.empty())
        throw std::runtime_error("release integer expected");
    }
    return value;
  };
  ws();
  if (i >= text.size() || text[i++] != '{')
    throw std::runtime_error("release object expected");
  std::map<std::string, std::string> result;
  for (;;) {
    ws();
    if (i < text.size() && text[i] == '}') {
      i++;
      break;
    }
    if (i >= text.size() || text[i] != '"')
      throw std::runtime_error("release key expected");
    auto key = token();
    ws();
    if (i >= text.size() || text[i++] != ':')
      throw std::runtime_error("release colon expected");
    auto value = token();
    if (!result.emplace(key, value).second)
      throw std::runtime_error("duplicate release key");
    ws();
    if (i < text.size() && text[i] == ',') {
      i++;
      continue;
    }
    if (i < text.size() && text[i] == '}') {
      i++;
      break;
    }
    throw std::runtime_error("release delimiter expected");
  }
  ws();
  if (i != text.size())
    throw std::runtime_error("release trailing data");
  return result;
}
std::map<std::string, std::string> Ack(int fd, uint32_t timeout) {
  pollfd p{fd, POLLIN, 0};
  int ready = poll(&p, 1, static_cast<int>(timeout));
  if (ready <= 0 || !(p.revents & POLLIN))
    throw std::runtime_error("consumer timeout/disconnect; no slot reuse");
  auto packet = mkg::ipc::Receive(fd);
  if (!packet.available || !packet.fds.empty() || packet.bytes.size() > 2048)
    throw std::runtime_error("invalid release transport; no slot reuse");
  auto a = ParseRelease(packet.bytes);
  if (a.size() == 1 && a["type"] == "stop")
    return a;
  if (a.size() != 4 || a["type"] != "release" || !a.contains("frameSeq") ||
      !a.contains("slotId") || !a.contains("slotEpoch"))
    throw std::runtime_error("malformed release; pool quarantined");
  return a;
}
} // namespace
int main(int argc, char **argv) {
  std::string path, pluginPath;
  uint32_t width = 1280, height = 720, count = 1, frames = 1, timeout = 30000;
  try {
    for (int i = 1; i < argc; i++) {
      std::string arg = argv[i];
      if (i + 1 >= argc)
        throw std::runtime_error("missing option value");
      std::string v = argv[++i];
      if (arg == "--socket")
        path = v;
      else if (arg == "--draw-plugin")
        pluginPath = v;
      else {
        auto number = std::stoul(v);
        if (number > UINT32_MAX)
          throw std::runtime_error("option too large");
        if (arg == "--width")
          width = number;
        else if (arg == "--height")
          height = number;
        else if (arg == "--slots")
          count = number;
        else if (arg == "--frames")
          frames = number;
        else if (arg == "--release-timeout-ms")
          timeout = number;
        else
          throw std::runtime_error("unknown option");
      }
    }
    if (path.empty() || path.size() >= sizeof(sockaddr_un::sun_path) ||
        width < 64 || height < 64 || width > 2560 || height > 1440 ||
        (count != 1 && count != 3) || frames < 1 || frames > 600 ||
        timeout < 100 || timeout > 120000)
      throw std::runtime_error("invalid bounded producer options");
    struct Plugin {
      void *handle = nullptr;
      ~Plugin() {
        if (handle)
          dlclose(handle);
      }
    } plugin;
    Gpu gpu;
    gpu.Init(width, height, count);
    if (!pluginPath.empty()) {
      if (pluginPath.front() != '/')
        throw std::runtime_error("draw plugin path must be absolute");
      plugin.handle = dlopen(pluginPath.c_str(), RTLD_NOW | RTLD_LOCAL);
      if (!plugin.handle)
        throw std::runtime_error(std::string("draw plugin load failed: ") +
                                 dlerror());
      gpu.pluginDraw = reinterpret_cast<MakoGpuPreviewDraw>(
          dlsym(plugin.handle, "mako_gpu_preview_draw_v1"));
      if (!gpu.pluginDraw)
        throw std::runtime_error("draw plugin ABI symbol missing");
      gpu.pluginShutdown = reinterpret_cast<MakoGpuPreviewShutdown>(
          dlsym(plugin.handle, "mako_gpu_preview_shutdown_v1"));
      if (!gpu.pluginShutdown)
        throw std::runtime_error("draw plugin shutdown ABI symbol missing");
    }
    UniqueFd server(socket(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC, 0));
    if (server.fd < 0)
      throw std::runtime_error("socket failed");
    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    std::memcpy(addr.sun_path, path.c_str(), path.size() + 1);
    mode_t previousUmask = umask(0077);
    int bound =
        bind(server.fd, reinterpret_cast<sockaddr *>(&addr), sizeof(addr));
    umask(previousUmask);
    if (bound < 0)
      throw std::runtime_error(
          "bind failed (existing paths are never unlinked)");
    struct Unlink {
      std::string path;
      ~Unlink() { unlink(path.c_str()); }
    } unlinker{path};
    if (chmod(path.c_str(), 0600) < 0 || listen(server.fd, 1) < 0)
      throw std::runtime_error("listen failed");
    std::cout << "{\"type\":\"listening\",\"pid\":" << getpid()
              << ",\"slots\":" << count
              << ",\"gpu\":" << Quote(gpu.properties.deviceName)
              << ",\"drmRenderMajor\":" << gpu.drm.renderMajor
              << ",\"drmRenderMinor\":" << gpu.drm.renderMinor << "}"
              << std::endl;
    pollfd p{server.fd, POLLIN, 0};
    if (poll(&p, 1, 60000) <= 0)
      throw std::runtime_error("consumer connect timeout");
    UniqueFd peer(accept4(server.fd, nullptr, nullptr, SOCK_CLOEXEC));
    if (peer.fd < 0)
      throw std::runtime_error("accept failed");
    ucred cred{};
    socklen_t len = sizeof(cred);
    if (getsockopt(peer.fd, SOL_SOCKET, SO_PEERCRED, &cred, &len) < 0 ||
        cred.uid != getuid())
      throw std::runtime_error("consumer UID mismatch");
    uint64_t producerEpoch =
        std::chrono::steady_clock::now().time_since_epoch().count();
    std::vector<uint64_t> inFlight(count, 0);
    uint64_t next = 1, released = 0;
    bool stopping = false;
    while (released < frames) {
      for (uint32_t id = 0; !stopping && id < count && next <= frames; id++)
        if (inFlight[id] == 0) {
          auto &s = gpu.slots[id];
          gpu.Draw(s, next);
          UniqueFd exported(gpu.Export(s));
          Send(peer.fd, gpu.Descriptor(s, id, next, producerEpoch),
               exported.fd);
          inFlight[id] = next++;
        }
      auto a = Ack(peer.fd, timeout);
      if (a["type"] == "stop") {
        stopping = true;
        frames = next - 1;
        continue;
      }
      bool matched = false;
      for (uint32_t id = 0; id < count; id++)
        if (inFlight[id] != 0 && a["slotId"] == std::to_string(id) &&
            a["slotEpoch"] == std::to_string(gpu.slots[id].epoch) &&
            a["frameSeq"] == std::to_string(inFlight[id])) {
          inFlight[id] = 0;
          released++;
          matched = true;
          break;
        }
      if (!matched)
        throw std::runtime_error(
            "stale/duplicate/unknown release; pool quarantined");
    }
    // Every external lease is acknowledged before producer cleanup. A failed
    // own-device wait is an error, never a successful drain/finished signal.
    gpu.Shutdown();
    std::string completion = stopping ? "stopped" : "finished";
    Send(peer.fd, "{\"type\":\"" + completion +
                      "\",\"frames\":" + std::to_string(frames) +
                      ",\"slots\":" + std::to_string(count) +
                      ",\"cpuPixelReadbacks\":0,\"gpuBlits\":" +
                      std::to_string(gpu.gpuCopies) + "}");
    std::cout << "{\"type\":\"" << completion << "\",\"frames\":" << frames
              << ",\"slots\":" << count
              << ",\"cpuPixelReadbacks\":0,\"gpuBlits\":" << gpu.gpuCopies
              << "}" << std::endl;
    return 0;
  } catch (const std::exception &ex) {
    std::cerr << "{\"type\":\"failed\",\"reason\":" << Quote(ex.what())
              << ",\"outstandingPolicy\":\"stop-no-reuse\"}" << std::endl;
    return 1;
  }
}
