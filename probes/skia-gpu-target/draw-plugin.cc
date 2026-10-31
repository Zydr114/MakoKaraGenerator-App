#include <vulkan/vulkan.h>
#include "include/c/gr_context.h"
#include "include/c/sk_surface.h"
#include "include/c/sk_canvas.h"
#include "include/c/sk_paint.h"
#include "include/c/sk_colorspace.h"
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
void Check(VkResult result, const char* operation) {
    if (result != VK_SUCCESS) throw std::runtime_error(operation);
}
gr_vk_func_ptr GetProc(void*, const char* name, vk_instance_t* instance, vk_device_t* device) {
    return reinterpret_cast<gr_vk_func_ptr>(device
        ? vkGetDeviceProcAddr(reinterpret_cast<VkDevice>(device), name)
        : vkGetInstanceProcAddr(reinterpret_cast<VkInstance>(instance), name));
}
struct SkiaContext {
    VkDevice device{};
    gr_vk_extensions_t* extensions{};
    gr_direct_context_t* direct{};
    gr_vk_backendcontext_t backend{}; // The C ABI procedure closure borrows this value.
    VkCommandPool pool{};
    void Init(VkInstance instance, VkPhysicalDevice physical, VkDevice vkDevice, VkQueue queue, uint32_t family) {
        if (device) {
            if (device != vkDevice) throw std::runtime_error("one-device P0 plugin cannot switch device");
            return;
        }
        device = vkDevice;
#ifdef MAKO_SKIA_BLIT
        VkFormatProperties2 format{VK_STRUCTURE_TYPE_FORMAT_PROPERTIES_2};
        VkDrmFormatModifierPropertiesListEXT modifierList{VK_STRUCTURE_TYPE_DRM_FORMAT_MODIFIER_PROPERTIES_LIST_EXT};
        format.pNext = &modifierList;
        vkGetPhysicalDeviceFormatProperties2(physical, VK_FORMAT_B8G8R8A8_UNORM, &format);
        std::vector<VkDrmFormatModifierPropertiesEXT> modifiers(modifierList.drmFormatModifierCount);
        modifierList.pDrmFormatModifierProperties = modifiers.data();
        vkGetPhysicalDeviceFormatProperties2(physical, VK_FORMAT_B8G8R8A8_UNORM, &format);
        bool destinationSupported = std::any_of(modifiers.begin(), modifiers.end(), [](const auto& modifier) {
            return modifier.drmFormatModifier == 0 && modifier.drmFormatModifierPlaneCount == 1 &&
                (modifier.drmFormatModifierTilingFeatures & VK_FORMAT_FEATURE_BLIT_DST_BIT);
        });
        if (!(format.formatProperties.optimalTilingFeatures & VK_FORMAT_FEATURE_BLIT_SRC_BIT) || !destinationSupported)
            throw std::runtime_error("BGRA optimal source / linear-modifier destination lacks blit format features");
#endif
        const char* names[] = {VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME,
            VK_EXT_EXTERNAL_MEMORY_DMA_BUF_EXTENSION_NAME, VK_EXT_IMAGE_DRM_FORMAT_MODIFIER_EXTENSION_NAME,
            VK_EXT_QUEUE_FAMILY_FOREIGN_EXTENSION_NAME};
        extensions = gr_vk_extensions_new();
        gr_vk_extensions_init(extensions, GetProc, nullptr, reinterpret_cast<vk_instance_t*>(instance),
            reinterpret_cast<vk_physical_device_t*>(physical), 0, nullptr, 4, names);
        backend.fInstance = reinterpret_cast<vk_instance_t*>(instance);
        backend.fPhysicalDevice = reinterpret_cast<vk_physical_device_t*>(physical);
        backend.fDevice = reinterpret_cast<vk_device_t*>(device);
        backend.fQueue = reinterpret_cast<vk_queue_t*>(queue);
        backend.fGraphicsQueueIndex = family;
        backend.fMaxAPIVersion = VK_API_VERSION_1_2;
        backend.fVkExtensions = extensions;
        backend.fGetProc = GetProc;
        direct = gr_direct_context_make_vulkan(backend);
        if (!direct) throw std::runtime_error("Skia Vulkan GRContext creation failed");
        VkCommandPoolCreateInfo info{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
        info.flags = VK_COMMAND_POOL_CREATE_TRANSIENT_BIT;
        info.queueFamilyIndex = family;
        Check(vkCreateCommandPool(device, &info, nullptr, &pool), "Skia bridge command pool failed");
    }
    void Acquire(VkQueue queue, VkImage image, uint32_t family, bool external) {
        VkCommandBufferAllocateInfo allocate{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
        allocate.commandPool = pool;
        allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        allocate.commandBufferCount = 1;
        VkCommandBuffer command{};
        Check(vkAllocateCommandBuffers(device, &allocate, &command), "Skia acquire allocate failed");
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        Check(vkBeginCommandBuffer(command, &begin), "Skia acquire begin failed");
        VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
        barrier.oldLayout = external ? VK_IMAGE_LAYOUT_GENERAL : VK_IMAGE_LAYOUT_UNDEFINED;
        barrier.newLayout = VK_IMAGE_LAYOUT_GENERAL;
        barrier.srcQueueFamilyIndex = external ? VK_QUEUE_FAMILY_FOREIGN_EXT : VK_QUEUE_FAMILY_IGNORED;
        barrier.dstQueueFamilyIndex = external ? family : VK_QUEUE_FAMILY_IGNORED;
        barrier.dstAccessMask = VK_ACCESS_MEMORY_WRITE_BIT;
        barrier.image = image;
        barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
        vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
            VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
        Check(vkEndCommandBuffer(command), "Skia acquire end failed");
        VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
        submit.commandBufferCount = 1;
        submit.pCommandBuffers = &command;
        Check(vkQueueSubmit(queue, 1, &submit, VK_NULL_HANDLE), "Skia acquire submit failed");
        Check(vkQueueWaitIdle(queue), "Skia acquire completion failed");
        vkFreeCommandBuffers(device, pool, 1, &command);
    }
    void Blit(VkQueue queue, VkImage source, VkImage destination, uint32_t width, uint32_t height, uint32_t family) {
        VkCommandBufferAllocateInfo allocate{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
        allocate.commandPool = pool; allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; allocate.commandBufferCount = 1;
        VkCommandBuffer command{};
        Check(vkAllocateCommandBuffers(device, &allocate, &command), "blit command allocate failed");
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        Check(vkBeginCommandBuffer(command, &begin), "blit command begin failed");
        VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
        barrier.oldLayout = VK_IMAGE_LAYOUT_GENERAL; barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT; barrier.image = destination;
        barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
        vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            0, 0, nullptr, 0, nullptr, 1, &barrier);
        VkImageBlit region{};
        region.srcSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
        region.dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
        // The production surface uses BottomLeft. Preserve its draw semantics,
        // then put logical top-left into the shared image's first physical row.
        region.srcOffsets[0] = {0, int32_t(height), 0};
        region.srcOffsets[1] = {int32_t(width), 0, 1};
        region.dstOffsets[1] = {int32_t(width), int32_t(height), 1};
        vkCmdBlitImage(command, source, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            destination, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region, VK_FILTER_NEAREST);
        barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT; barrier.dstAccessMask = 0;
        barrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL; barrier.newLayout = VK_IMAGE_LAYOUT_GENERAL;
        barrier.srcQueueFamilyIndex = family; barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_FOREIGN_EXT;
        vkCmdPipelineBarrier(command, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
            0, 0, nullptr, 0, nullptr, 1, &barrier);
        Check(vkEndCommandBuffer(command), "blit command end failed");
        VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO}; submit.commandBufferCount = 1; submit.pCommandBuffers = &command;
        Check(vkQueueSubmit(queue, 1, &submit, VK_NULL_HANDLE), "blit submit failed");
        Check(vkQueueWaitIdle(queue), "blit completion failed");
        vkFreeCommandBuffers(device, pool, 1, &command);
    }
    void Shutdown() {
        if (!device) return;
        vkDeviceWaitIdle(device);
        if (direct) gr_recording_context_unref(reinterpret_cast<gr_recording_context_t*>(direct));
        if (extensions) gr_vk_extensions_delete(extensions);
        if (pool) vkDestroyCommandPool(device, pool, nullptr);
        device = {}; direct = {}; extensions = {}; pool = {};
    }
} context;
}

extern "C" void mako_gpu_preview_shutdown_v1() { context.Shutdown(); }

extern "C" bool mako_gpu_preview_draw_v1(VkInstance instance, VkPhysicalDevice physical,
    VkDevice device, VkQueue queue, uint32_t family, VkImage image, uint32_t width, uint32_t height,
    uint64_t sequence, bool wasExternal, uint32_t* gpuCopies) {
    sk_surface_t* surface{};
    gr_backendrendertarget_t* target{};
    sk_colorspace_t* colorSpace{};
    sk_paint_t* paint{};
    try {
        if (!gpuCopies) throw std::runtime_error("gpuCopies output missing");
        *gpuCopies = 0;
        context.Init(instance, physical, device, queue, family);
        context.Acquire(queue, image, family, wasExternal);
#ifndef MAKO_SKIA_BLIT
        gr_vk_imageinfo_t info{};
        info.fImage = reinterpret_cast<uint64_t>(image);
        // Borrow only. Never pass VkDeviceMemory / fBackendMemory or an adopt flag.
        info.fImageTiling = VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT;
        info.fImageLayout = VK_IMAGE_LAYOUT_GENERAL;
        info.fFormat = VK_FORMAT_B8G8R8A8_UNORM;
        info.fImageUsageFlags = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
            VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        info.fSampleCount = 1;
        info.fLevelCount = 1;
        info.fCurrentQueueFamily = family;
        info.fSharingMode = VK_SHARING_MODE_EXCLUSIVE;
#endif
#ifdef MAKO_SKIA_BLIT
        const char* requestedColorSpace = std::getenv("MAKO_P0_SKIA_COLORSPACE");
        colorSpace = requestedColorSpace && std::string(requestedColorSpace) == "srgb" ? sk_colorspace_new_srgb() : nullptr;
        sk_imageinfo_t sourceInfo{colorSpace, int32_t(width), int32_t(height), BGRA_8888_SK_COLORTYPE, PREMUL_SK_ALPHATYPE};
        surface = sk_surface_new_render_target(reinterpret_cast<gr_recording_context_t*>(context.direct),
            true, &sourceInfo, 0, BOTTOM_LEFT_GR_SURFACE_ORIGIN, nullptr, false);
#else
        target = gr_backendrendertarget_new_vulkan(width, height, &info);
        colorSpace = sk_colorspace_new_srgb();
        surface = sk_surface_new_backend_render_target(reinterpret_cast<gr_recording_context_t*>(context.direct),
            target, TOP_LEFT_GR_SURFACE_ORIGIN, BGRA_8888_SK_COLORTYPE, colorSpace, nullptr);
#endif
        if (!surface) throw std::runtime_error("Skia cannot wrap exportable DRM-modifier target");
        auto canvas = sk_surface_get_canvas(surface);
        paint = sk_paint_new();
        sk_paint_set_antialias(paint, false);
        sk_paint_set_blendmode(paint, SRC_SK_BLENDMODE);
        auto rect = [&](uint32_t x, uint32_t y, uint32_t w, uint32_t h, uint32_t color) {
            sk_rect_t bounds{float(x), float(y), float(x+w), float(y+h)};
            sk_paint_set_color(paint, color);
            sk_canvas_draw_rect(canvas, &bounds, paint);
        };
        sk_canvas_clear(canvas, 0xff202020);
        rect(0, 0, width/2, height/2, 0xffff0000);
        rect(width/2, 0, width-width/2, height/2, 0xff00ff00);
        rect(0, height/2, width/2, height-height/2, 0xff0000ff);
        rect(width/2, height/2, width-width/2, height-height/2, 0xffffffff);
        uint32_t band = std::max(1u, height/8);
        for (uint32_t i = 0; i < 8; ++i) {
            uint32_t v = (i*255+3)/7;
            rect(i*width/8, height/2-band/2, (i+1)*width/8-i*width/8, band,
                0xff000000 | (v<<16) | (v<<8) | v);
        }
        rect(width/4, height*3/4, width/4, band, 0x80ff00ff);
        rect(width/2, height*3/4, width/4, band, 0x00000000);
        for (uint32_t bit=0; bit<16; ++bit)
            rect(bit*width/16, 0, (bit+1)*width/16-bit*width/16, std::max(1u,height/16),
                ((sequence>>bit)&1) ? 0xffffffff : 0xff000000);
#ifdef MAKO_SKIA_BLIT
        if (!gr_mako_direct_context_flush_surface_vk_state(context.direct, surface,
                VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_QUEUE_FAMILY_IGNORED))
            throw std::runtime_error("Skia source flush/submit did not complete");
        gr_vk_imageinfo_t actual{};
        if (!sk_mako_surface_get_vk_image_info(surface, &actual) || !actual.fImage || actual.fSampleCount != 1 ||
            actual.fImageLayout != VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL || actual.fCurrentQueueFamily != family ||
            actual.fFormat != VK_FORMAT_B8G8R8A8_UNORM || actual.fImageTiling != VK_IMAGE_TILING_OPTIMAL ||
            !(actual.fImageUsageFlags & VK_IMAGE_USAGE_TRANSFER_SRC_BIT))
            throw std::runtime_error("Skia source VkImage or mutable state invalid");
#else
        if (!gr_mako_direct_context_flush_surface_vk_state(context.direct, surface,
                VK_IMAGE_LAYOUT_GENERAL, VK_QUEUE_FAMILY_FOREIGN_EXT))
            throw std::runtime_error("Skia flush/submit did not complete");
        gr_vk_imageinfo_t actual{};
        if (!sk_mako_surface_get_vk_image_info(surface, &actual) || actual.fImage != info.fImage ||
            actual.fImageLayout != VK_IMAGE_LAYOUT_GENERAL || actual.fCurrentQueueFamily != VK_QUEUE_FAMILY_FOREIGN_EXT)
            throw std::runtime_error("Skia mutable state differs from completed external release");
#endif
        // get-backend-info may flush pending work; this surface has no further commands.
        if (!gr_direct_context_submit(context.direct, true)) throw std::runtime_error("Skia info submit failed");
#ifdef MAKO_SKIA_BLIT
        context.Blit(queue, reinterpret_cast<VkImage>(actual.fImage), image, width, height, family);
        *gpuCopies = 1;
#endif
        sk_paint_delete(paint); paint = {};
        sk_surface_unref(surface); surface = {};
        if (target) gr_backendrendertarget_delete(target);
        target = {};
        if (colorSpace) sk_colorspace_unref(colorSpace);
        colorSpace = {};
        std::fprintf(stderr, "{\"type\":\"skia-frame\",\"frameSeq\":%llu,\"sourceLayout\":%u,\"sourceQueueFamily\":%u,\"cpuPixelReadbacks\":0,\"gpuBlits\":%u}\n",
            static_cast<unsigned long long>(sequence), actual.fImageLayout, actual.fCurrentQueueFamily, *gpuCopies);
        return true;
    } catch (const std::exception& error) {
        if (paint) sk_paint_delete(paint);
        if (surface) sk_surface_unref(surface);
        if (target) gr_backendrendertarget_delete(target);
        if (colorSpace) sk_colorspace_unref(colorSpace);
        std::fprintf(stderr, "Skia P0 plugin failed: %s\n", error.what());
        return false;
    }
}
