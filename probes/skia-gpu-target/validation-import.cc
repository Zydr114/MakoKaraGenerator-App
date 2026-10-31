// Test-only external-memory reader. Never linked into production GPU libraries.
#include <vulkan/vulkan.h>
#include <unistd.h>
#include <cstdio>
#include <cstring>
#include <stdexcept>

namespace {
void Check(VkResult result, const char *operation) {
  if (result != VK_SUCCESS)
    throw std::runtime_error(operation);
}
uint32_t MemoryType(VkPhysicalDevice physical, uint32_t bits, VkMemoryPropertyFlags flags) {
  VkPhysicalDeviceMemoryProperties properties{};
  vkGetPhysicalDeviceMemoryProperties(physical, &properties);
  for (uint32_t i = 0; i < properties.memoryTypeCount; ++i)
    if ((bits & (1u << i)) && (properties.memoryTypes[i].propertyFlags & flags) == flags)
      return i;
  throw std::runtime_error("validation memory type unavailable");
}
}

extern "C" int mako_validation_import_read(
    VkPhysicalDevice physical, VkDevice device, VkQueue queue, uint32_t family,
    int sourceFd, uint32_t width, uint32_t height, uint32_t stride, uint64_t offset,
    uint64_t allocationBytes, uint64_t subresourceBytes, uint64_t modifier,
    unsigned char *destination, char *error, size_t errorSize) {
  VkImage image{};
  VkDeviceMemory memory{}, stagingMemory{};
  VkBuffer staging{};
  VkCommandPool commands{};
  void *mapping = nullptr;
  int fd = -1, result = 0;
  bool submitted = false;
  try {
    VkSubresourceLayout plane{offset, subresourceBytes, stride, 0, 0};
    VkExternalMemoryImageCreateInfo external{VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO};
    external.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;
    VkImageDrmFormatModifierExplicitCreateInfoEXT drm{VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_EXPLICIT_CREATE_INFO_EXT};
    drm.pNext = &external;
    drm.drmFormatModifier = modifier;
    drm.drmFormatModifierPlaneCount = 1;
    drm.pPlaneLayouts = &plane;
    VkImageCreateInfo create{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    create.pNext = &drm;
    create.imageType = VK_IMAGE_TYPE_2D;
    create.format = VK_FORMAT_B8G8R8A8_UNORM;
    create.extent = {width, height, 1};
    create.mipLevels = create.arrayLayers = 1;
    create.samples = VK_SAMPLE_COUNT_1_BIT;
    create.tiling = VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT;
    create.usage = VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    create.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    Check(vkCreateImage(device, &create, nullptr, &image), "validation create imported image");
    VkMemoryRequirements requirements{};
    vkGetImageMemoryRequirements(device, image, &requirements);
    auto getFdProperties = reinterpret_cast<PFN_vkGetMemoryFdPropertiesKHR>(vkGetDeviceProcAddr(device, "vkGetMemoryFdPropertiesKHR"));
    if (!getFdProperties) throw std::runtime_error("validation getFdProperties unavailable");
    VkMemoryFdPropertiesKHR properties{VK_STRUCTURE_TYPE_MEMORY_FD_PROPERTIES_KHR};
    Check(getFdProperties(device, VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT, sourceFd, &properties), "validation DMA-BUF properties");
    fd = dup(sourceFd);
    if (fd < 0) throw std::runtime_error("validation dup FD");
    VkMemoryDedicatedAllocateInfo dedicated{VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO};
    dedicated.image = image;
    VkImportMemoryFdInfoKHR import{VK_STRUCTURE_TYPE_IMPORT_MEMORY_FD_INFO_KHR};
    import.pNext = &dedicated;
    import.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT;
    import.fd = fd;
    VkMemoryAllocateInfo allocate{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    allocate.pNext = &import;
    allocate.allocationSize = allocationBytes;
    allocate.memoryTypeIndex = MemoryType(physical, properties.memoryTypeBits & requirements.memoryTypeBits, 0);
    Check(vkAllocateMemory(device, &allocate, nullptr, &memory), "validation import memory");
    fd = -1; // Vulkan owns only the duplicate after successful allocation.
    Check(vkBindImageMemory(device, image, memory, 0), "validation bind imported memory");
    VkBufferCreateInfo buffer{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    buffer.size = static_cast<uint64_t>(width) * height * 4;
    buffer.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    buffer.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    Check(vkCreateBuffer(device, &buffer, nullptr, &staging), "validation staging buffer");
    vkGetBufferMemoryRequirements(device, staging, &requirements);
    allocate.pNext = nullptr;
    allocate.allocationSize = requirements.size;
    allocate.memoryTypeIndex = MemoryType(physical, requirements.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    Check(vkAllocateMemory(device, &allocate, nullptr, &stagingMemory), "validation staging allocation");
    Check(vkBindBufferMemory(device, staging, stagingMemory, 0), "validation bind staging");
    VkCommandPoolCreateInfo pool{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    pool.queueFamilyIndex = family;
    Check(vkCreateCommandPool(device, &pool, nullptr, &commands), "validation command pool");
    VkCommandBufferAllocateInfo commandAllocate{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    commandAllocate.commandPool = commands;
    commandAllocate.commandBufferCount = 1;
    commandAllocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    VkCommandBuffer command{};
    Check(vkAllocateCommandBuffers(device, &commandAllocate, &command), "validation command buffer");
    VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    Check(vkBeginCommandBuffer(command, &begin), "validation begin commands");
    VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
    barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_FOREIGN_EXT;
    barrier.dstQueueFamilyIndex = family;
    barrier.oldLayout = VK_IMAGE_LAYOUT_GENERAL;
    barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    barrier.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    barrier.image = image;
    barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
    VkBufferImageCopy copy{};
    copy.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    copy.imageExtent = {width, height, 1};
    vkCmdCopyImageToBuffer(command, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, staging, 1, &copy);
    barrier.srcQueueFamilyIndex = family;
    barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_FOREIGN_EXT;
    barrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    barrier.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    barrier.srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    barrier.dstAccessMask = 0;
    vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
    Check(vkEndCommandBuffer(command), "validation end commands");
    VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    submit.commandBufferCount = 1;
    submit.pCommandBuffers = &command;
    Check(vkQueueSubmit(queue, 1, &submit, VK_NULL_HANDLE), "validation submit");
    submitted = true;
    Check(vkQueueWaitIdle(queue), "validation wait queue");
    Check(vkMapMemory(device, stagingMemory, 0, buffer.size, 0, &mapping), "validation map staging");
    std::memcpy(destination, mapping, buffer.size);
  } catch (const std::exception &failure) {
    std::snprintf(error, errorSize, "%s", failure.what());
    result = 1;
  }
  if (submitted) vkQueueWaitIdle(queue);
  if (mapping) vkUnmapMemory(device, stagingMemory);
  if (commands) vkDestroyCommandPool(device, commands, nullptr);
  if (staging) vkDestroyBuffer(device, staging, nullptr);
  if (stagingMemory) vkFreeMemory(device, stagingMemory, nullptr);
  if (image) vkDestroyImage(device, image, nullptr);
  if (memory) vkFreeMemory(device, memory, nullptr);
  if (fd >= 0) close(fd);
  return result;
}
