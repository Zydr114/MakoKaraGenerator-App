#pragma once
#include "gpu-preview.h"
#include <stdexcept>
#include <string>
#include <vector>

namespace mkg::ipc {
struct Failure : std::runtime_error {
  mkg_gpu_status status;
  Failure(mkg_gpu_status code, const char *message)
      : std::runtime_error(message), status(code) {}
};
struct Packet {
  bool available = false;
  std::string bytes;
  std::vector<int> fds;
  Packet() = default;
  Packet(const Packet &) = delete;
  Packet &operator=(const Packet &) = delete;
  Packet(Packet &&other) noexcept;
  ~Packet();
};
mkg_gpu_status Send(int socket, const void *bytes, uint32_t length,
                    const int32_t *fds, uint32_t fdCount);
Packet Receive(int socket);
uint32_t PeerPidSameUid(int socket);
} // namespace mkg::ipc
