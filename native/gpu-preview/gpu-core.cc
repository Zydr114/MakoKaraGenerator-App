#include "gpu-core.h"
#include <algorithm>
#include <cstring>
#include <fcntl.h>
#include <unistd.h>
#include <vector>

namespace mkg {
void Check(VkResult result, const char *operation) {
  if (result == VK_SUCCESS)
    return;
  auto status = result == VK_ERROR_DEVICE_LOST ? MKG_GPU_DEVICE_LOST
                : result == VK_ERROR_OUT_OF_DEVICE_MEMORY ||
                        result == VK_ERROR_OUT_OF_HOST_MEMORY
                    ? MKG_GPU_OUT_OF_MEMORY
                    : MKG_GPU_UNSUPPORTED;
  throw Failure(status, operation, result);
}
void QueryCapabilities(const Context &c, uint32_t width, uint32_t height,
                       bool requireBlit, mkg_gpu_pool_info *info) {
  uint32_t count = 0;
  Check(vkEnumerateDeviceExtensionProperties(c.physical, nullptr, &count,
                                             nullptr),
        "enumerate device extension count");
  std::vector<VkExtensionProperties> extensions(count);
  Check(vkEnumerateDeviceExtensionProperties(c.physical, nullptr, &count,
                                             extensions.data()),
        "enumerate device extensions");
  const char *required[] = {VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME,
                            VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME,
                            VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME,
                            VK_EXT_QUEUE_FAMILY_FOREIGN_EXTENSION_NAME,
                            VK_EXT_PHYSICAL_DEVICE_DRM_EXTENSION_NAME};
  for (const auto *name : required)
    if (std::none_of(extensions.begin(), extensions.end(), [&](const auto &e) {
          return std::strcmp(e.extensionName, name) == 0;
        }))
      throw Failure(MKG_GPU_UNSUPPORTED,
                    std::string("missing extension: ") + name);
  uint32_t queueCount = 0;
  vkGetPhysicalDeviceQueueFamilyProperties(c.physical, &queueCount, nullptr);
  std::vector<VkQueueFamilyProperties> families(queueCount);
  vkGetPhysicalDeviceQueueFamilyProperties(c.physical, &queueCount,
                                           families.data());
  if (c.family >= queueCount ||
      !(families[c.family].queueFlags & VK_QUEUE_GRAPHICS_BIT))
    throw Failure(MKG_GPU_UNSUPPORTED, "borrowed queue family is not graphics");
  VkPhysicalDeviceDrmPropertiesEXT drm{
      VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRM_PROPERTIES_EXT};
  VkPhysicalDeviceIDProperties ids{
      VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
  drm.pNext = &ids;
  VkPhysicalDeviceProperties2 properties{
      VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
  properties.pNext = &drm;
  vkGetPhysicalDeviceProperties2(c.physical, &properties);
  if (!drm.hasRender ||
      properties.properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_CPU)
    throw Failure(MKG_GPU_UNSUPPORTED, "hardware DRM render device required");
  VkDrmFormatModifierPropertiesListEXT list{
      VK_STRUCTURE_TYPE_DRM_FORMAT_MODIFIER_PROPERTIES_LIST_EXT};
  VkFormatProperties2 format{VK_STRUCTURE_TYPE_FORMAT_PROPERTIES_2};
  format.pNext = &list;
  vkGetPhysicalDeviceFormatProperties2(c.physical, VK_FORMAT_B8G8R8A8_UNORM,
                                       &format);
  std::vector<VkDrmFormatModifierPropertiesEXT> modifiers(
      list.drmFormatModifierCount);
  list.pDrmFormatModifierProperties = modifiers.data();
  vkGetPhysicalDeviceFormatProperties2(c.physical, VK_FORMAT_B8G8R8A8_UNORM,
                                       &format);
  VkFormatFeatureFlags requiredFeatures =
      VK_FORMAT_FEATURE_COLOR_ATTACHMENT_BIT |
      VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT | VK_FORMAT_FEATURE_TRANSFER_SRC_BIT |
      VK_FORMAT_FEATURE_TRANSFER_DST_BIT;
  if (requireBlit)
    requiredFeatures |= VK_FORMAT_FEATURE_BLIT_DST_BIT;
  auto modifier =
      std::find_if(modifiers.begin(), modifiers.end(), [&](auto &m) {
        return m.drmFormatModifier == 0 && m.drmFormatModifierPlaneCount == 1 &&
               (m.drmFormatModifierTilingFeatures & requiredFeatures) ==
                   requiredFeatures;
      });
  if (modifier == modifiers.end() ||
      (requireBlit && !(format.formatProperties.optimalTilingFeatures &
                        VK_FORMAT_FEATURE_BLIT_SRC_BIT)))
    throw Failure(
        MKG_GPU_UNSUPPORTED,
        "BGRA optimal source / linear modifier target features unsupported");
  VkPhysicalDeviceImageDrmFormatModifierInfoEXT modifierInfo{
      VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_DRM_FORMAT_MODIFIER_INFO_EXT};
  modifierInfo.drmFormatModifier = 0;
  modifierInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
  VkPhysicalDeviceExternalImageFormatInfo external{
      VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO};
  external.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;
  external.pNext = &modifierInfo;
  VkPhysicalDeviceImageFormatInfo2 query{
      VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2};
  query.pNext = &external;
  query.format = VK_FORMAT_B8G8R8A8_UNORM;
  query.type = VK_IMAGE_TYPE_2D;
  query.tiling = VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT;
  query.usage = kTargetUsage;
  VkExternalImageFormatProperties exported{
      VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES};
  VkImageFormatProperties2 result{VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2};
  result.pNext = &exported;
  Check(vkGetPhysicalDeviceImageFormatProperties2(c.physical, &query, &result),
        "query BGRA modifier external image format");
  if (!(exported.externalMemoryProperties.externalMemoryFeatures &
        VK_EXTERNAL_MEMORY_FEATURE_EXPORTABLE_BIT) ||
      width > result.imageFormatProperties.maxExtent.width ||
      height > result.imageFormatProperties.maxExtent.height)
    throw Failure(MKG_GPU_UNSUPPORTED,
                  "external target dimensions/export unsupported");
  if (requireBlit) {
    VkPhysicalDeviceImageFormatInfo2 source{
        VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2};
    source.format = VK_FORMAT_B8G8R8A8_UNORM;
    source.type = VK_IMAGE_TYPE_2D;
    source.tiling = VK_IMAGE_TILING_OPTIMAL;
    source.usage =
        VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    VkImageFormatProperties2 supported{
        VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2};
    Check(vkGetPhysicalDeviceImageFormatProperties2(c.physical, &source,
                                                    &supported),
          "query BGRA optimal source format");
    if (width > supported.imageFormatProperties.maxExtent.width ||
        height > supported.imageFormatProperties.maxExtent.height ||
        !(supported.imageFormatProperties.sampleCounts & VK_SAMPLE_COUNT_1_BIT))
      throw Failure(MKG_GPU_UNSUPPORTED,
                    "source dimensions/sample count unsupported");
  }
  if (info) {
    info->width = width;
    info->height = height;
    info->format = VK_FORMAT_B8G8R8A8_UNORM;
    info->modifier = 0;
    info->vendor_id = properties.properties.vendorID;
    info->device_id = properties.properties.deviceID;
    info->drm_render_major = drm.renderMajor;
    info->drm_render_minor = drm.renderMinor;
    std::memcpy(info->device_uuid, ids.deviceUUID, sizeof(info->device_uuid));
    std::memcpy(info->device_name, properties.properties.deviceName,
                sizeof(info->device_name));
    info->device_name[sizeof(info->device_name) - 1] = 0;
  }
}
void AllocateImage(const Context &c, uint32_t width, uint32_t height,
                   uint64_t remainingBudget, Image &s) {
  uint64_t modifier = 0;
  VkImageDrmFormatModifierListCreateInfoEXT list{
      VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_LIST_CREATE_INFO_EXT};
  list.drmFormatModifierCount = 1;
  list.pDrmFormatModifiers = &modifier;
  VkExternalMemoryImageCreateInfo external{
      VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO};
  external.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;
  external.pNext = &list;
  VkImageCreateInfo image{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
  image.pNext = &external;
  image.imageType = VK_IMAGE_TYPE_2D;
  image.format = VK_FORMAT_B8G8R8A8_UNORM;
  image.extent = {width, height, 1};
  image.mipLevels = 1;
  image.arrayLayers = 1;
  image.samples = VK_SAMPLE_COUNT_1_BIT;
  image.tiling = VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT;
  image.usage = kTargetUsage;
  image.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
  image.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
  Check(vkCreateImage(c.device, &image, nullptr, &s.image),
        "create export image");
  VkMemoryRequirements requirement{};
  vkGetImageMemoryRequirements(c.device, s.image, &requirement);
  if (requirement.size > remainingBudget)
    throw Failure(MKG_GPU_OUT_OF_MEMORY,
                  "actual allocation exceeds pool budget");
  VkPhysicalDeviceMemoryProperties memory{};
  vkGetPhysicalDeviceMemoryProperties(c.physical, &memory);
  uint32_t type = UINT32_MAX;
  for (uint32_t i = 0; i < memory.memoryTypeCount; i++)
    if ((requirement.memoryTypeBits & (1u << i)) &&
        (memory.memoryTypes[i].propertyFlags &
         VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)) {
      type = i;
      break;
    }
  if (type == UINT32_MAX)
    throw Failure(MKG_GPU_UNSUPPORTED, "no device-local export memory type");
  VkMemoryDedicatedAllocateInfo dedicated{
      VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO};
  dedicated.image = s.image;
  VkExportMemoryAllocateInfo exported{
      VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO};
  exported.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;
  exported.pNext = &dedicated;
  VkMemoryAllocateInfo allocation{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
  allocation.pNext = &exported;
  allocation.allocationSize = requirement.size;
  allocation.memoryTypeIndex = type;
  Check(vkAllocateMemory(c.device, &allocation, nullptr, &s.memory),
        "allocate export memory");
  s.allocation = requirement.size;
  Check(vkBindImageMemory(c.device, s.image, s.memory, 0), "bind export image");
  auto getModifier =
      reinterpret_cast<PFN_vkGetImageDrmFormatModifierPropertiesEXT>(
          vkGetDeviceProcAddr(c.device,
                              "vkGetImageDrmFormatModifierPropertiesEXT"));
  if (!getModifier)
    throw Failure(MKG_GPU_UNSUPPORTED, "modifier procedure missing on device");
  VkImageDrmFormatModifierPropertiesEXT actual{
      VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_PROPERTIES_EXT};
  Check(getModifier(c.device, s.image, &actual), "query image modifier");
  if (actual.drmFormatModifier != 0)
    throw Failure(MKG_GPU_UNSUPPORTED, "actual image modifier is not linear");
  VkImageSubresource plane{VK_IMAGE_ASPECT_MEMORY_PLANE_0_BIT_EXT, 0, 0};
  VkSubresourceLayout layout{};
  vkGetImageSubresourceLayout(c.device, s.image, &plane, &layout);
  s.stride = layout.rowPitch;
  s.offset = layout.offset;
  s.size = layout.size;
  if (s.stride < width * 4ull || s.stride > UINT32_MAX ||
      s.offset >= s.allocation ||
      s.stride * (height - 1ull) + width * 4ull > s.allocation - s.offset)
    throw Failure(MKG_GPU_UNSUPPORTED, "invalid exported plane bounds");
}
void DestroyImage(const Context &c, Image &image) noexcept {
  if (image.image)
    vkDestroyImage(c.device, image.image, nullptr);
  if (image.memory)
    vkFreeMemory(c.device, image.memory, nullptr);
  image = {};
}
int ExportFd(const Context &c, const Image &image) {
  auto getFd = reinterpret_cast<PFN_vkGetMemoryFdKHR>(
      vkGetDeviceProcAddr(c.device, "vkGetMemoryFdKHR"));
  if (!getFd)
    throw Failure(MKG_GPU_UNSUPPORTED, "memory fd procedure missing on device");
  VkMemoryGetFdInfoKHR info{VK_STRUCTURE_TYPE_MEMORY_GET_FD_INFO_KHR};
  info.memory = image.memory;
  info.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;
  int fd = -1;
  Check(getFd(c.device, &info, &fd), "export DMA_BUF fd");
  if (fcntl(fd, F_SETFD, FD_CLOEXEC) < 0) {
    close(fd);
    throw Failure(MKG_GPU_IO_ERROR, "export fd CLOEXEC failed");
  }
  return fd;
}
void Acquire(VkCommandBuffer cmd, const Context &c, const Image &image,
             VkImageLayout layout, VkAccessFlags access,
             VkPipelineStageFlags stage) {
  VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
  barrier.oldLayout =
      image.external ? VK_IMAGE_LAYOUT_GENERAL : VK_IMAGE_LAYOUT_UNDEFINED;
  barrier.newLayout = layout;
  barrier.srcQueueFamilyIndex =
      image.external ? VK_QUEUE_FAMILY_FOREIGN_EXT : VK_QUEUE_FAMILY_IGNORED;
  barrier.dstQueueFamilyIndex =
      image.external ? c.family : VK_QUEUE_FAMILY_IGNORED;
  barrier.dstAccessMask = access;
  barrier.image = image.image;
  barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
  vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, stage, 0, 0,
                       nullptr, 0, nullptr, 1, &barrier);
}
void Release(VkCommandBuffer cmd, const Context &c, const Image &image,
             VkImageLayout layout, VkAccessFlags access,
             VkPipelineStageFlags stage) {
  VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
  barrier.oldLayout = layout;
  barrier.newLayout = VK_IMAGE_LAYOUT_GENERAL;
  barrier.srcQueueFamilyIndex = c.family;
  barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_FOREIGN_EXT;
  barrier.srcAccessMask = access;
  barrier.image = image.image;
  barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
  vkCmdPipelineBarrier(cmd, stage, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, 0, 0,
                       nullptr, 0, nullptr, 1, &barrier);
}
} // namespace mkg
