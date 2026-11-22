#ifdef _WIN32

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <aclapi.h>

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

namespace {

// Self-contained stable Node-API v1 C ABI declarations:
// avoids node.lib and Electron header downloads while supporting node.exe,
// electron.exe, and renamed portable hosts (e.g., MakoKaraGenerator.exe).
struct napi_env__;
struct napi_value__;
struct napi_ref__;
struct napi_callback_info__;

using napi_env = napi_env__*;
using napi_value = napi_value__*;
using napi_ref = napi_ref__*;
using napi_callback_info = napi_callback_info__*;

enum napi_status : int {
    napi_ok = 0,
};

enum napi_property_attributes : int {
    napi_default = 0,
};

constexpr size_t kNapiAutoLength = SIZE_MAX;

using napi_callback = napi_value(__cdecl*)(napi_env env, napi_callback_info info);
using napi_finalize = void(__cdecl*)(napi_env env, void* finalize_data, void* finalize_hint);

struct napi_property_descriptor {
    const char* utf8name;
    napi_value name;
    napi_callback method;
    napi_callback getter;
    napi_callback setter;
    napi_value value;
    napi_property_attributes attributes;
    void* data;
};

struct NapiFunctions {
    napi_status(__cdecl* throw_error)(napi_env, const char*, const char*) = nullptr;
    napi_status(__cdecl* get_cb_info)(napi_env, napi_callback_info, size_t*, napi_value*, napi_value*, void**) = nullptr;
    napi_status(__cdecl* get_value_string_utf8)(napi_env, napi_value, char*, size_t, size_t*) = nullptr;
    napi_status(__cdecl* get_value_int64)(napi_env, napi_value, int64_t*) = nullptr;
    napi_status(__cdecl* create_object)(napi_env, napi_value*) = nullptr;
    napi_status(__cdecl* wrap)(napi_env, napi_value, void*, napi_finalize, void*, napi_ref*) = nullptr;
    napi_status(__cdecl* unwrap)(napi_env, napi_value, void**) = nullptr;
    napi_status(__cdecl* create_int64)(napi_env, int64_t, napi_value*) = nullptr;
    napi_status(__cdecl* set_named_property)(napi_env, napi_value, const char*, napi_value) = nullptr;
    napi_status(__cdecl* get_named_property)(napi_env, napi_value, const char*, napi_value*) = nullptr;
    napi_status(__cdecl* get_boolean)(napi_env, bool, napi_value*) = nullptr;
    napi_status(__cdecl* create_function)(napi_env, const char*, size_t, napi_callback, void*, napi_value*) = nullptr;
    napi_status(__cdecl* is_buffer)(napi_env, napi_value, bool*) = nullptr;
    napi_status(__cdecl* get_buffer_info)(napi_env, napi_value, void**, size_t*) = nullptr;
    napi_status(__cdecl* call_function)(napi_env, napi_value, napi_value, size_t, const napi_value*, napi_value*) = nullptr;
    napi_status(__cdecl* define_properties)(napi_env, napi_value, size_t, const napi_property_descriptor*) = nullptr;
};

NapiFunctions g_napi{};
std::once_flag g_napi_once;
bool g_napi_ready = false;

template <typename Fn>
bool LoadSymbol(HMODULE host, const char* name, Fn& target) {
    FARPROC proc = ::GetProcAddress(host, name);
    if (!proc) return false;
    target = reinterpret_cast<Fn>(proc);
    return true;
}

bool EnsureNapiResolved() {
    std::call_once(g_napi_once, []() {
        HMODULE host = ::GetModuleHandleW(nullptr);
        if (!host) {
            g_napi_ready = false;
            return;
        }
        bool ok = true;
        ok = ok && LoadSymbol(host, "napi_throw_error", g_napi.throw_error);
        ok = ok && LoadSymbol(host, "napi_get_cb_info", g_napi.get_cb_info);
        ok = ok && LoadSymbol(host, "napi_get_value_string_utf8", g_napi.get_value_string_utf8);
        ok = ok && LoadSymbol(host, "napi_get_value_int64", g_napi.get_value_int64);
        ok = ok && LoadSymbol(host, "napi_create_object", g_napi.create_object);
        ok = ok && LoadSymbol(host, "napi_wrap", g_napi.wrap);
        ok = ok && LoadSymbol(host, "napi_unwrap", g_napi.unwrap);
        ok = ok && LoadSymbol(host, "napi_create_int64", g_napi.create_int64);
        ok = ok && LoadSymbol(host, "napi_set_named_property", g_napi.set_named_property);
        ok = ok && LoadSymbol(host, "napi_get_named_property", g_napi.get_named_property);
        ok = ok && LoadSymbol(host, "napi_get_boolean", g_napi.get_boolean);
        ok = ok && LoadSymbol(host, "napi_create_function", g_napi.create_function);
        ok = ok && LoadSymbol(host, "napi_is_buffer", g_napi.is_buffer);
        ok = ok && LoadSymbol(host, "napi_get_buffer_info", g_napi.get_buffer_info);
        ok = ok && LoadSymbol(host, "napi_call_function", g_napi.call_function);
        ok = ok && LoadSymbol(host, "napi_define_properties", g_napi.define_properties);
        g_napi_ready = ok;
    });
    return g_napi_ready;
}

struct ShmMapping {
    void* addr = nullptr;
    HANDLE section = nullptr;
    size_t byte_length = 0;
    bool closed = false;
};

void CloseMapping(ShmMapping* mapping) {
    if (!mapping || mapping->closed) return;
    if (mapping->addr != nullptr) {
        ::UnmapViewOfFile(mapping->addr);
        mapping->addr = nullptr;
    }
    if (mapping->section != nullptr) {
        ::CloseHandle(mapping->section);
        mapping->section = nullptr;
    }
    mapping->closed = true;
}

void __cdecl Finalizer(napi_env env, void* finalize_data, void* finalize_hint) {
    (void)env;
    (void)finalize_hint;
    auto* mapping = static_cast<ShmMapping*>(finalize_data);
    if (mapping) {
        CloseMapping(mapping);
        delete mapping;
    }
}

void ThrowError(napi_env env, const char* msg) {
    g_napi.throw_error(env, nullptr, msg);
}

bool IsValidWindowsShmName(const std::string& name) {
    static constexpr const char kPrefix[] = "Local\\mkg-shm-";
    static constexpr size_t kPrefixLen = sizeof(kPrefix) - 1;
    if (name.size() <= kPrefixLen) return false;
    if (name.compare(0, kPrefixLen, kPrefix) != 0) return false;
    const size_t suffix_len = name.size() - kPrefixLen;
    if (suffix_len < 24 || suffix_len > 64) return false;
    for (size_t i = kPrefixLen; i < name.size(); ++i) {
        const char ch = name[i];
        const bool is_lower_hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
        if (!is_lower_hex) return false;
    }
    return true;
}

bool VerifySectionOwnerMatchesCurrentUser(HANDLE section) {
    HANDLE token = nullptr;
    if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token) || !token) {
        return false;
    }

    DWORD needed = 0;
    ::GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
    if (needed == 0 || ::GetLastError() != ERROR_INSUFFICIENT_BUFFER) {
        ::CloseHandle(token);
        return false;
    }

    std::vector<uint8_t> user_buf(needed);
    if (!::GetTokenInformation(token, TokenUser, user_buf.data(), needed, &needed)) {
        ::CloseHandle(token);
        return false;
    }
    ::CloseHandle(token);

    const auto* token_user = reinterpret_cast<const TOKEN_USER*>(user_buf.data());
    PSID current_sid = token_user->User.Sid;
    if (!current_sid) return false;

    PSID owner_sid = nullptr;
    PSECURITY_DESCRIPTOR sd = nullptr;
    const DWORD status = ::GetSecurityInfo(
        section,
        SE_KERNEL_OBJECT,
        OWNER_SECURITY_INFORMATION,
        &owner_sid,
        nullptr,
        nullptr,
        nullptr,
        &sd);
    if (status != ERROR_SUCCESS || !owner_sid || !sd) {
        if (sd) ::LocalFree(sd);
        return false;
    }

    const bool matched = ::EqualSid(owner_sid, current_sid) != 0;
    ::LocalFree(sd);
    return matched;
}

napi_value __cdecl IsClosedCallback(napi_env env, napi_callback_info info) {
    size_t argc = 0;
    napi_value thiz = nullptr;
    g_napi.get_cb_info(env, info, &argc, nullptr, &thiz, nullptr);
    ShmMapping* m = nullptr;
    g_napi.unwrap(env, thiz, reinterpret_cast<void**>(&m));
    napi_value val = nullptr;
    g_napi.get_boolean(env, m == nullptr || m->closed, &val);
    return val;
}

napi_value __cdecl ReadSlotCallback(napi_env env, napi_callback_info info) {
    size_t argc = 3;
    napi_value args[3] = {nullptr, nullptr, nullptr};
    napi_value thiz = nullptr;
    if (g_napi.get_cb_info(env, info, &argc, args, &thiz, nullptr) != napi_ok || argc < 3) {
        ThrowError(env, "Expected 3 arguments: byteOffset, exactFrameBytes, targetBuffer");
        return nullptr;
    }

    ShmMapping* m = nullptr;
    if (g_napi.unwrap(env, thiz, reinterpret_cast<void**>(&m)) != napi_ok || !m || m->closed || !m->addr) {
        ThrowError(env, "Mapping is closed");
        return nullptr;
    }

    int64_t byte_offset_i64 = 0;
    int64_t exact_bytes_i64 = 0;
    if (g_napi.get_value_int64(env, args[0], &byte_offset_i64) != napi_ok || byte_offset_i64 < 0) {
        ThrowError(env, "byteOffset must be a non-negative integer");
        return nullptr;
    }
    if (g_napi.get_value_int64(env, args[1], &exact_bytes_i64) != napi_ok || exact_bytes_i64 <= 0) {
        ThrowError(env, "exactFrameBytes must be a positive integer");
        return nullptr;
    }

    bool is_buf = false;
    if (g_napi.is_buffer(env, args[2], &is_buf) != napi_ok || !is_buf) {
        ThrowError(env, "targetBuffer must be a Node Buffer");
        return nullptr;
    }

    void* dst = nullptr;
    size_t dst_len = 0;
    if (g_napi.get_buffer_info(env, args[2], &dst, &dst_len) != napi_ok) {
        ThrowError(env, "Failed to get buffer info");
        return nullptr;
    }

    if (!dst || dst_len != static_cast<size_t>(exact_bytes_i64)) {
        ThrowError(env, "targetBuffer length must match exactFrameBytes");
        return nullptr;
    }

    const size_t byte_offset = static_cast<size_t>(byte_offset_i64);
    const size_t exact_bytes = static_cast<size_t>(exact_bytes_i64);
    if (byte_offset > m->byte_length || exact_bytes > m->byte_length - byte_offset) {
        ThrowError(env, "Out of bounds read");
        return nullptr;
    }

    std::atomic_thread_fence(std::memory_order_acquire);
    std::memcpy(dst, static_cast<const uint8_t*>(m->addr) + byte_offset, exact_bytes);
    return nullptr;
}

napi_value __cdecl CloseCallback(napi_env env, napi_callback_info info) {
    size_t argc = 0;
    napi_value thiz = nullptr;
    g_napi.get_cb_info(env, info, &argc, nullptr, &thiz, nullptr);
    ShmMapping* m = nullptr;
    if (g_napi.unwrap(env, thiz, reinterpret_cast<void**>(&m)) == napi_ok && m) {
        CloseMapping(m);
    }
    return nullptr;
}

napi_value __cdecl OpenReadOnly(napi_env env, napi_callback_info info) {
    size_t argc = 2;
    napi_value args[2] = {nullptr, nullptr};
    if (g_napi.get_cb_info(env, info, &argc, args, nullptr, nullptr) != napi_ok || argc < 2) {
        ThrowError(env, "Expected 2 arguments: resourceName, expectedBytes");
        return nullptr;
    }

    size_t name_len = 0;
    if (g_napi.get_value_string_utf8(env, args[0], nullptr, 0, &name_len) != napi_ok) {
        ThrowError(env, "resourceName must be a string");
        return nullptr;
    }
    if (name_len == 0 || name_len > 256) {
        ThrowError(env, "resourceName length must be between 1 and 256");
        return nullptr;
    }
    std::vector<char> name_buf(name_len + 1);
    size_t copied = 0;
    if (g_napi.get_value_string_utf8(env, args[0], name_buf.data(), name_buf.size(), &copied) != napi_ok) {
        ThrowError(env, "Failed to read resourceName");
        return nullptr;
    }
    std::string name(name_buf.data(), copied);
    if (name.find('\0') != std::string::npos) {
        ThrowError(env, "resourceName cannot contain null bytes");
        return nullptr;
    }
    if (name.find("..") != std::string::npos) {
        ThrowError(env, "resourceName cannot contain '..'");
        return nullptr;
    }
    if (!IsValidWindowsShmName(name)) {
        ThrowError(env, "resourceName must match Local\\mkg-shm-<24..64 lower-hex>");
        return nullptr;
    }

    int64_t expected_bytes_i64 = 0;
    if (g_napi.get_value_int64(env, args[1], &expected_bytes_i64) != napi_ok) {
        ThrowError(env, "expectedBytes must be an integer");
        return nullptr;
    }
    if (expected_bytes_i64 <= 0 || expected_bytes_i64 > 67108864LL) {
        ThrowError(env, "expectedBytes must be in [1, 67108864]");
        return nullptr;
    }
    const size_t expected_bytes = static_cast<size_t>(expected_bytes_i64);

    const int wide_chars = ::MultiByteToWideChar(
        CP_UTF8, MB_ERR_INVALID_CHARS, name.data(), static_cast<int>(name.size()), nullptr, 0);
    if (wide_chars <= 0) {
        ThrowError(env, "Invalid UTF-8 resourceName");
        return nullptr;
    }
    std::wstring wide_name(static_cast<size_t>(wide_chars), L'\0');
    if (::MultiByteToWideChar(
            CP_UTF8, MB_ERR_INVALID_CHARS, name.data(), static_cast<int>(name.size()), wide_name.data(), wide_chars) != wide_chars) {
        ThrowError(env, "Failed to convert resourceName to UTF-16");
        return nullptr;
    }

    HANDLE section = ::OpenFileMappingW(FILE_MAP_READ | READ_CONTROL, FALSE, wide_name.c_str());
    if (!section) {
        ThrowError(env, "Failed to open shm section");
        return nullptr;
    }

    if (!VerifySectionOwnerMatchesCurrentUser(section)) {
        ::CloseHandle(section);
        ThrowError(env, "Section owner SID mismatch");
        return nullptr;
    }

    // Map full section so VirtualQuery observes actual kernel page-aligned section size.
    void* addr = ::MapViewOfFile(section, FILE_MAP_READ, 0, 0, 0);
    if (!addr) {
        ::CloseHandle(section);
        ThrowError(env, "Failed to map shm section");
        return nullptr;
    }

    SYSTEM_INFO sys_info{};
    ::GetSystemInfo(&sys_info);
    const size_t page_size = sys_info.dwPageSize ? static_cast<size_t>(sys_info.dwPageSize) : 4096u;
    MEMORY_BASIC_INFORMATION mbi{};
    if (::VirtualQuery(addr, &mbi, sizeof(mbi)) == 0) {
        ::UnmapViewOfFile(addr);
        ::CloseHandle(section);
        ThrowError(env, "Failed to query shm section");
        return nullptr;
    }

    const size_t expected_aligned = ((expected_bytes + page_size - 1u) / page_size) * page_size;
    if (mbi.RegionSize != expected_aligned) {
        ::UnmapViewOfFile(addr);
        ::CloseHandle(section);
        ThrowError(env, "Section size mismatch");
        return nullptr;
    }

    auto* mapping = new ShmMapping{addr, section, expected_bytes, false};

    napi_value result = nullptr;
    if (g_napi.create_object(env, &result) != napi_ok) {
        CloseMapping(mapping);
        delete mapping;
        ThrowError(env, "Failed to create result object");
        return nullptr;
    }

    if (g_napi.wrap(env, result, mapping, Finalizer, nullptr, nullptr) != napi_ok) {
        CloseMapping(mapping);
        delete mapping;
        ThrowError(env, "Failed to wrap mapping");
        return nullptr;
    }

    napi_value byte_len_val = nullptr;
    g_napi.create_int64(env, static_cast<int64_t>(expected_bytes), &byte_len_val);
    g_napi.set_named_property(env, result, "byteLength", byte_len_val);

    napi_value is_closed_fn = nullptr;
    g_napi.create_function(env, "isClosed", kNapiAutoLength, IsClosedCallback, nullptr, &is_closed_fn);
    g_napi.set_named_property(env, result, "isClosed", is_closed_fn);

    napi_value read_slot_fn = nullptr;
    g_napi.create_function(env, "readSlot", kNapiAutoLength, ReadSlotCallback, nullptr, &read_slot_fn);
    g_napi.set_named_property(env, result, "readSlot", read_slot_fn);

    napi_value close_fn = nullptr;
    g_napi.create_function(env, "close", kNapiAutoLength, CloseCallback, nullptr, &close_fn);
    g_napi.set_named_property(env, result, "close", close_fn);

    return result;
}

napi_value __cdecl ModuleReadSlot(napi_env env, napi_callback_info info) {
    size_t argc = 4;
    napi_value args[4] = {nullptr, nullptr, nullptr, nullptr};
    if (g_napi.get_cb_info(env, info, &argc, args, nullptr, nullptr) != napi_ok || argc < 4) {
        ThrowError(env, "Expected 4 arguments: mapping, byteOffset, exactFrameBytes, targetBuffer");
        return nullptr;
    }
    napi_value read_slot_fn = nullptr;
    if (g_napi.get_named_property(env, args[0], "readSlot", &read_slot_fn) != napi_ok) {
        ThrowError(env, "Invalid mapping object");
        return nullptr;
    }
    napi_value call_args[3] = {args[1], args[2], args[3]};
    napi_value result = nullptr;
    if (g_napi.call_function(env, args[0], read_slot_fn, 3, call_args, &result) != napi_ok) {
        return nullptr;
    }
    return result;
}

napi_value __cdecl ModuleClose(napi_env env, napi_callback_info info) {
    size_t argc = 1;
    napi_value args[1] = {nullptr};
    if (g_napi.get_cb_info(env, info, &argc, args, nullptr, nullptr) != napi_ok || argc < 1) {
        ThrowError(env, "Expected 1 argument: mapping");
        return nullptr;
    }
    napi_value close_fn = nullptr;
    if (g_napi.get_named_property(env, args[0], "close", &close_fn) != napi_ok) {
        ThrowError(env, "Invalid mapping object");
        return nullptr;
    }
    napi_value result = nullptr;
    if (g_napi.call_function(env, args[0], close_fn, 0, nullptr, &result) != napi_ok) {
        return nullptr;
    }
    return result;
}

napi_value Init(napi_env env, napi_value exports) {
    napi_property_descriptor desc[] = {
        {"openReadOnly", nullptr, OpenReadOnly, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"readSlot", nullptr, ModuleReadSlot, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"close", nullptr, ModuleClose, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    if (g_napi.define_properties(env, exports, 3, desc) != napi_ok) {
        return nullptr;
    }
    return exports;
}

}  // namespace

extern "C" __declspec(dllexport) napi_value __cdecl napi_register_module_v1(napi_env env, napi_value exports) {
    if (!EnsureNapiResolved()) {
        return nullptr;
    }
    return Init(env, exports);
}

#elif defined(__linux__)

#include <node_api.h>
#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <fcntl.h>
#include <linux/magic.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/vfs.h>
#include <unistd.h>
#include <string>
#include <vector>

struct ScopedFd {
    int fd = -1;
    ScopedFd() = default;
    explicit ScopedFd(int f) : fd(f) {}
    ~ScopedFd() { reset(); }
    ScopedFd(const ScopedFd&) = delete;
    ScopedFd& operator=(const ScopedFd&) = delete;
    void reset() {
        if (fd >= 0) {
            ::close(fd);
            fd = -1;
        }
    }
};

static bool IsValidLinuxShmBasename(const std::string& base) {
    static constexpr const char kPrefix[] = "mkg-shm-";
    static constexpr size_t kPrefixLen = sizeof(kPrefix) - 1;
    if (base.size() <= kPrefixLen) return false;
    if (base.compare(0, kPrefixLen, kPrefix) != 0) return false;
    const size_t suffix_len = base.size() - kPrefixLen;
    if (suffix_len < 24 || suffix_len > 64) return false;
    for (size_t i = kPrefixLen; i < base.size(); ++i) {
        const char ch = base[i];
        const bool is_lower_hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
        if (!is_lower_hex) return false;
    }
    return true;
}

static bool IsAllowedLinuxShmDir(const std::string& dir) {
    if (dir == "/dev/shm") {
        return true;
    }
    const char* xdg_raw = std::getenv("XDG_RUNTIME_DIR");
    if (!xdg_raw || xdg_raw[0] != '/') {
        return false;
    }
    std::string xdg(xdg_raw);
    while (!xdg.empty() && xdg.back() == '/') {
        xdg.pop_back();
    }
    if (xdg.empty() || xdg.find("..") != std::string::npos) {
        return false;
    }
    return dir == xdg;
}

struct ShmMapping {
    void* addr;
    size_t byte_length;
    bool closed;
};

static void Finalizer(napi_env env, void* finalize_data, void* finalize_hint) {
    (void)env;
    (void)finalize_hint;
    auto* mapping = static_cast<ShmMapping*>(finalize_data);
    if (mapping) {
        if (!mapping->closed && mapping->addr != MAP_FAILED && mapping->addr != nullptr) {
            ::munmap(mapping->addr, mapping->byte_length);
            mapping->addr = nullptr;
            mapping->closed = true;
        }
        delete mapping;
    }
}

static void ThrowError(napi_env env, const char* msg) {
    napi_throw_error(env, nullptr, msg);
}

static napi_value OpenReadOnly(napi_env env, napi_callback_info info) {
    size_t argc = 2;
    napi_value args[2];
    if (napi_get_cb_info(env, info, &argc, args, nullptr, nullptr) != napi_ok || argc < 2) {
        ThrowError(env, "Expected 2 arguments: resourceName, expectedBytes");
        return nullptr;
    }

    size_t name_len = 0;
    if (napi_get_value_string_utf8(env, args[0], nullptr, 0, &name_len) != napi_ok) {
        ThrowError(env, "resourceName must be a string");
        return nullptr;
    }
    if (name_len == 0 || name_len > 256) {
        ThrowError(env, "resourceName length must be between 1 and 256");
        return nullptr;
    }
    std::vector<char> name_buf(name_len + 1);
    size_t copied = 0;
    if (napi_get_value_string_utf8(env, args[0], name_buf.data(), name_buf.size(), &copied) != napi_ok) {
        ThrowError(env, "Failed to read resourceName");
        return nullptr;
    }
    std::string name(name_buf.data(), copied);
    if (name.find('\0') != std::string::npos) {
        ThrowError(env, "resourceName cannot contain null bytes");
        return nullptr;
    }
    if (name.find("..") != std::string::npos) {
        ThrowError(env, "resourceName cannot contain '..'");
        return nullptr;
    }

    const size_t slash_pos = name.rfind('/');
    if (slash_pos == std::string::npos || slash_pos == 0) {
        ThrowError(env, "resourceName directory must be /dev/shm or XDG_RUNTIME_DIR");
        return nullptr;
    }
    const std::string dir = name.substr(0, slash_pos);
    const std::string base = name.substr(slash_pos + 1);
    if (!IsAllowedLinuxShmDir(dir)) {
        ThrowError(env, "resourceName directory must be /dev/shm or XDG_RUNTIME_DIR");
        return nullptr;
    }
    if (!IsValidLinuxShmBasename(base)) {
        ThrowError(env, "resourceName must match mkg-shm-<24..64 lower-hex>");
        return nullptr;
    }

    int64_t expected_bytes_i64 = 0;
    if (napi_get_value_int64(env, args[1], &expected_bytes_i64) != napi_ok) {
        ThrowError(env, "expectedBytes must be an integer");
        return nullptr;
    }
    if (expected_bytes_i64 <= 0 || expected_bytes_i64 > 67108864LL) {
        ThrowError(env, "expectedBytes must be in [1, 67108864]");
        return nullptr;
    }
    size_t expected_bytes = static_cast<size_t>(expected_bytes_i64);

    ScopedFd dirfd(::open(dir.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC));
    if (dirfd.fd < 0) {
        ThrowError(env, "Failed to open shm directory");
        return nullptr;
    }

    ScopedFd fd(::openat(dirfd.fd, base.c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW));
    if (fd.fd < 0) {
        ThrowError(env, "Failed to open shm file");
        return nullptr;
    }

    struct stat st;
    struct statfs sfs;
    if (::fstat(fd.fd, &st) < 0 || ::fstatfs(fd.fd, &sfs) < 0) {
        ThrowError(env, "Failed to stat shm file");
        return nullptr;
    }

    if (!S_ISREG(st.st_mode)) {
        ThrowError(env, "Not a regular file");
        return nullptr;
    }
    if (st.st_uid != ::getuid()) {
        ThrowError(env, "UID mismatch");
        return nullptr;
    }
    if ((st.st_mode & 0777) != 0600) {
        ThrowError(env, "Invalid permissions (must be 0600)");
        return nullptr;
    }
    if (st.st_size <= 0 || static_cast<uint64_t>(st.st_size) != static_cast<uint64_t>(expected_bytes)) {
        ThrowError(env, "File size mismatch or zero");
        return nullptr;
    }
    if (static_cast<unsigned long>(sfs.f_type) != static_cast<unsigned long>(TMPFS_MAGIC)) {
        ThrowError(env, "File is not on tmpfs");
        return nullptr;
    }

    void* addr = ::mmap(nullptr, expected_bytes, PROT_READ, MAP_SHARED, fd.fd, 0);
    if (addr == MAP_FAILED) {
        ThrowError(env, "Failed to mmap shm file");
        return nullptr;
    }

    int ul = ::unlinkat(dirfd.fd, base.c_str(), 0);
    fd.reset();
    dirfd.reset();
    if (ul != 0) {
        ::munmap(addr, expected_bytes);
        ThrowError(env, "Failed to unlink shm file");
        return nullptr;
    }

    auto* mapping = new ShmMapping{addr, expected_bytes, false};

    napi_value result;
    if (napi_create_object(env, &result) != napi_ok) {
        ::munmap(addr, expected_bytes);
        delete mapping;
        ThrowError(env, "Failed to create result object");
        return nullptr;
    }

    if (napi_wrap(env, result, mapping, Finalizer, nullptr, nullptr) != napi_ok) {
        ::munmap(addr, expected_bytes);
        delete mapping;
        ThrowError(env, "Failed to wrap mapping");
        return nullptr;
    }

    napi_value byte_len_val;
    napi_create_int64(env, static_cast<int64_t>(expected_bytes), &byte_len_val);
    napi_set_named_property(env, result, "byteLength", byte_len_val);

    auto is_closed_cb = [](napi_env env, napi_callback_info info) -> napi_value {
        size_t argc = 0;
        napi_value thiz;
        napi_get_cb_info(env, info, &argc, nullptr, &thiz, nullptr);
        ShmMapping* m = nullptr;
        napi_unwrap(env, thiz, reinterpret_cast<void**>(&m));
        napi_value val;
        napi_get_boolean(env, m == nullptr || m->closed, &val);
        return val;
    };
    napi_value is_closed_fn;
    napi_create_function(env, "isClosed", NAPI_AUTO_LENGTH, is_closed_cb, nullptr, &is_closed_fn);
    napi_set_named_property(env, result, "isClosed", is_closed_fn);

    auto read_slot_cb = [](napi_env env, napi_callback_info info) -> napi_value {
        size_t argc = 3;
        napi_value args[3];
        napi_value thiz;
        if (napi_get_cb_info(env, info, &argc, args, &thiz, nullptr) != napi_ok || argc < 3) {
            ThrowError(env, "Expected 3 arguments: byteOffset, exactFrameBytes, targetBuffer");
            return nullptr;
        }

        ShmMapping* m = nullptr;
        if (napi_unwrap(env, thiz, reinterpret_cast<void**>(&m)) != napi_ok || !m || m->closed) {
            ThrowError(env, "Mapping is closed");
            return nullptr;
        }

        int64_t byte_offset_i64 = 0;
        int64_t exact_bytes_i64 = 0;
        if (napi_get_value_int64(env, args[0], &byte_offset_i64) != napi_ok || byte_offset_i64 < 0) {
            ThrowError(env, "byteOffset must be a non-negative integer");
            return nullptr;
        }
        if (napi_get_value_int64(env, args[1], &exact_bytes_i64) != napi_ok || exact_bytes_i64 <= 0) {
            ThrowError(env, "exactFrameBytes must be a positive integer");
            return nullptr;
        }

        bool is_buf = false;
        if (napi_is_buffer(env, args[2], &is_buf) != napi_ok || !is_buf) {
            ThrowError(env, "targetBuffer must be a Node Buffer");
            return nullptr;
        }

        void* dst = nullptr;
        size_t dst_len = 0;
        if (napi_get_buffer_info(env, args[2], &dst, &dst_len) != napi_ok) {
            ThrowError(env, "Failed to get buffer info");
            return nullptr;
        }

        if (!dst || dst_len != static_cast<size_t>(exact_bytes_i64)) {
            ThrowError(env, "targetBuffer length must match exactFrameBytes");
            return nullptr;
        }

        size_t byte_offset = static_cast<size_t>(byte_offset_i64);
        size_t exact_bytes = static_cast<size_t>(exact_bytes_i64);

        if (byte_offset > m->byte_length || exact_bytes > m->byte_length - byte_offset) {
            ThrowError(env, "Out of bounds read");
            return nullptr;
        }

        std::atomic_thread_fence(std::memory_order_acquire);
        std::memcpy(dst, static_cast<const uint8_t*>(m->addr) + byte_offset, exact_bytes);

        return nullptr;
    };
    napi_value read_slot_fn;
    napi_create_function(env, "readSlot", NAPI_AUTO_LENGTH, read_slot_cb, nullptr, &read_slot_fn);
    napi_set_named_property(env, result, "readSlot", read_slot_fn);

    auto close_cb = [](napi_env env, napi_callback_info info) -> napi_value {
        size_t argc = 0;
        napi_value thiz;
        napi_get_cb_info(env, info, &argc, nullptr, &thiz, nullptr);
        ShmMapping* m = nullptr;
        if (napi_unwrap(env, thiz, reinterpret_cast<void**>(&m)) == napi_ok && m) {
            if (!m->closed && m->addr != MAP_FAILED && m->addr != nullptr) {
                ::munmap(m->addr, m->byte_length);
                m->addr = nullptr;
                m->closed = true;
            }
        }
        return nullptr;
    };
    napi_value close_fn;
    napi_create_function(env, "close", NAPI_AUTO_LENGTH, close_cb, nullptr, &close_fn);
    napi_set_named_property(env, result, "close", close_fn);

    return result;
}

static napi_value ModuleReadSlot(napi_env env, napi_callback_info info) {
    size_t argc = 4;
    napi_value args[4];
    if (napi_get_cb_info(env, info, &argc, args, nullptr, nullptr) != napi_ok || argc < 4) {
        ThrowError(env, "Expected 4 arguments: mapping, byteOffset, exactFrameBytes, targetBuffer");
        return nullptr;
    }
    napi_value read_slot_fn;
    if (napi_get_named_property(env, args[0], "readSlot", &read_slot_fn) != napi_ok) {
        ThrowError(env, "Invalid mapping object");
        return nullptr;
    }
    napi_value call_args[3] = { args[1], args[2], args[3] };
    napi_value result;
    if (napi_call_function(env, args[0], read_slot_fn, 3, call_args, &result) != napi_ok) {
        return nullptr;
    }
    return result;
}

static napi_value ModuleClose(napi_env env, napi_callback_info info) {
    size_t argc = 1;
    napi_value args[1];
    if (napi_get_cb_info(env, info, &argc, args, nullptr, nullptr) != napi_ok || argc < 1) {
        ThrowError(env, "Expected 1 argument: mapping");
        return nullptr;
    }
    napi_value close_fn;
    if (napi_get_named_property(env, args[0], "close", &close_fn) != napi_ok) {
        ThrowError(env, "Invalid mapping object");
        return nullptr;
    }
    napi_value result;
    if (napi_call_function(env, args[0], close_fn, 0, nullptr, &result) != napi_ok) {
        return nullptr;
    }
    return result;
}

static napi_value Init(napi_env env, napi_value exports) {
    napi_property_descriptor desc[] = {
        {"openReadOnly", nullptr, OpenReadOnly, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"readSlot", nullptr, ModuleReadSlot, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"close", nullptr, ModuleClose, nullptr, nullptr, nullptr, napi_default, nullptr}
    };
    napi_define_properties(env, exports, 3, desc);
    return exports;
}

extern "C" {
    napi_value NAPI_CDECL napi_register_module_v1(napi_env env, napi_value exports) {
        return Init(env, exports);
    }
}

#else
#error "Unsupported platform for shm_bridge.node"
#endif
