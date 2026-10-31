#pragma once
#include "gpu-preview.h"
#include <stdexcept>
#include <string>
#include <vulkan/vulkan.h>

namespace mkg {
struct Failure : std::runtime_error {
  mkg_gpu_status status;
  VkResult result;
  Failure(mkg_gpu_status code, const std::string &message,
          VkResult vk = VK_SUCCESS)
      : std::runtime_error(message), status(code), result(vk) {}
};
void Check(VkResult result, const char *operation);
struct Context {
  VkInstance instance{};
  VkPhysicalDevice physical{};
  VkDevice device{};
  VkQueue queue{};
  uint32_t family = 0;
};
struct Image {
  VkImage image{};
  VkDeviceMemory memory{};
  VkDeviceSize allocation{}, stride{}, offset{}, size{};
  bool external = false;
};
constexpr VkImageUsageFlags kTargetUsage =
    VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
    VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
constexpr uint64_t kAbsolutePoolBudget = 192ull * 1024 * 1024;
void QueryCapabilities(const Context &context, uint32_t width, uint32_t height,
                       bool requireBlit, mkg_gpu_pool_info *info);
void AllocateImage(const Context &context, uint32_t width, uint32_t height,
                   uint64_t remainingBudget, Image &image);
void DestroyImage(const Context &context, Image &image) noexcept;
int ExportFd(const Context &context, const Image &image);
void Acquire(VkCommandBuffer command, const Context &context,
             const Image &image, VkImageLayout layout, VkAccessFlags access,
             VkPipelineStageFlags stage);
void Release(VkCommandBuffer command, const Context &context,
             const Image &image, VkImageLayout layout, VkAccessFlags access,
             VkPipelineStageFlags stage);
} // namespace mkg
