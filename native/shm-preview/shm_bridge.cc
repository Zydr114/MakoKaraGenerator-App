#include <node_api.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <fcntl.h>
#include <linux/magic.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/vfs.h>
#include <unistd.h>
#include <string>
#include <vector>

#ifdef _WIN32
#error "Windows shm_bridge.node (OpenFileMappingW/MapViewOfFile/UnmapViewOfFile + GetProcAddress dynamic N-API) is deferred under Method B."
#elif defined(__linux__)

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

    int fd = ::open(name.c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) {
        ThrowError(env, "Failed to open shm file");
        return nullptr;
    }

    struct stat st;
    struct statfs sfs;
    if (::fstat(fd, &st) < 0 || ::fstatfs(fd, &sfs) < 0) {
        ::close(fd);
        ThrowError(env, "Failed to stat shm file");
        return nullptr;
    }

    if (!S_ISREG(st.st_mode)) {
        ::close(fd);
        ThrowError(env, "Not a regular file");
        return nullptr;
    }
    if (st.st_uid != ::getuid()) {
        ::close(fd);
        ThrowError(env, "UID mismatch");
        return nullptr;
    }
    if ((st.st_mode & 0777) != 0600) {
        ::close(fd);
        ThrowError(env, "Invalid permissions (must be 0600)");
        return nullptr;
    }
    if (st.st_size <= 0 || static_cast<uint64_t>(st.st_size) != static_cast<uint64_t>(expected_bytes)) {
        ::close(fd);
        ThrowError(env, "File size mismatch or zero");
        return nullptr;
    }
    if (static_cast<unsigned long>(sfs.f_type) != static_cast<unsigned long>(TMPFS_MAGIC)) {
        ::close(fd);
        ThrowError(env, "File is not on tmpfs");
        return nullptr;
    }

    void* addr = ::mmap(nullptr, expected_bytes, PROT_READ, MAP_SHARED, fd, 0);
    if (addr == MAP_FAILED) {
        ::close(fd);
        ThrowError(env, "Failed to mmap shm file");
        return nullptr;
    }

    int ul = ::unlink(name.c_str());
    ::close(fd);
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
