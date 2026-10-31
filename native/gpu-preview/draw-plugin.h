#pragma once
#include <cstdint>
#include <vulkan/vulkan.h>

// P0 probe ABI only. Caller owns every Vulkan object. Success leaves the image
// GENERAL, released to FOREIGN, with writes submitted. A reused target starts
// GENERAL/FOREIGN; a fresh target starts UNDEFINED. No pixel readback is
// allowed. gpuCopies is 0 for direct drawing or 1 for the approved single GPU
// copy.
extern "C" bool mako_gpu_preview_draw_v1(
    VkInstance instance, VkPhysicalDevice physical, VkDevice device,
    VkQueue queue, uint32_t family, VkImage image, uint32_t width,
    uint32_t height, uint64_t frameSeq, bool wasExternal, uint32_t *gpuCopies);
using MakoGpuPreviewDraw = decltype(&mako_gpu_preview_draw_v1);

// Called while all caller-owned Vulkan handles are alive and before dlclose.
extern "C" void mako_gpu_preview_shutdown_v1();
using MakoGpuPreviewShutdown = decltype(&mako_gpu_preview_shutdown_v1);
