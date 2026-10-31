#include "ipc-core.h"
#include <array>
#include <cerrno>
#include <cstring>
#include <sys/socket.h>
#include <unistd.h>

namespace mkg::ipc {
Packet::Packet(Packet &&other) noexcept
    : available(other.available), bytes(std::move(other.bytes)),
      fds(std::move(other.fds)) {
  other.fds.clear();
}
Packet::~Packet() {
  for (int fd : fds)
    close(fd);
}
mkg_gpu_status Send(int socket, const void *bytes, uint32_t length,
                    const int32_t *fds, uint32_t fdCount) {
  if (!bytes || length == 0 || length > MKG_GPU_MAX_PACKET_BYTES ||
      fdCount > MKG_GPU_MAX_PACKET_FDS || (fdCount && !fds))
    throw Failure(MKG_GPU_INVALID_ARGUMENT, "invalid bounded packet");
  for (uint32_t i = 0; i < fdCount; i++)
    if (fds[i] < 0)
      throw Failure(MKG_GPU_INVALID_ARGUMENT, "invalid packet fd");
  iovec io{const_cast<void *>(bytes), length};
  msghdr message{};
  message.msg_iov = &io;
  message.msg_iovlen = 1;
  alignas(
      cmsghdr) char control[CMSG_SPACE(MKG_GPU_MAX_PACKET_FDS * sizeof(int))]{};
  if (fdCount) {
    message.msg_control = control;
    message.msg_controllen = CMSG_SPACE(fdCount * sizeof(int));
    cmsghdr *header = CMSG_FIRSTHDR(&message);
    header->cmsg_level = SOL_SOCKET;
    header->cmsg_type = SCM_RIGHTS;
    header->cmsg_len = CMSG_LEN(fdCount * sizeof(int));
    std::memcpy(CMSG_DATA(header), fds, fdCount * sizeof(int));
  }
  ssize_t sent = sendmsg(socket, &message, MSG_DONTWAIT | MSG_NOSIGNAL);
  if (sent < 0 && (errno == EAGAIN || errno == EWOULDBLOCK))
    return MKG_GPU_WOULD_BLOCK;
  if (sent != static_cast<ssize_t>(length))
    throw Failure(MKG_GPU_IO_ERROR, "sendmsg failed; delivery unknown");
  return MKG_GPU_OK;
}
Packet Receive(int socket) {
  Packet packet;
  packet.fds.reserve(MKG_GPU_MAX_PACKET_FDS);
  std::array<char, MKG_GPU_MAX_PACKET_BYTES> bytes{};
  alignas(
      cmsghdr) char control[CMSG_SPACE(MKG_GPU_MAX_PACKET_FDS * sizeof(int))]{};
  iovec io{bytes.data(), bytes.size()};
  msghdr message{};
  message.msg_iov = &io;
  message.msg_iovlen = 1;
  message.msg_control = control;
  message.msg_controllen = sizeof(control);
  ssize_t length = recvmsg(socket, &message, MSG_DONTWAIT | MSG_CMSG_CLOEXEC);
  if (length < 0 && (errno == EAGAIN || errno == EWOULDBLOCK))
    return packet;
  if (length < 0)
    throw Failure(MKG_GPU_IO_ERROR, "recvmsg failed");
  bool invalid = false;
  for (cmsghdr *header = CMSG_FIRSTHDR(&message); header;
       header = CMSG_NXTHDR(&message, header)) {
    if (header->cmsg_level != SOL_SOCKET || header->cmsg_type != SCM_RIGHTS ||
        header->cmsg_len < CMSG_LEN(0)) {
      invalid = true;
      continue;
    }
    size_t byteCount = header->cmsg_len - CMSG_LEN(0);
    if (byteCount % sizeof(int))
      invalid = true;
    auto *fds = reinterpret_cast<int *>(CMSG_DATA(header));
    packet.fds.insert(packet.fds.end(), fds, fds + byteCount / sizeof(int));
  }
  if (length == 0)
    throw Failure(MKG_GPU_IO_ERROR,
                  "peer disconnected; outstanding leases unknown");
  if (invalid || (message.msg_flags & (MSG_TRUNC | MSG_CTRUNC)) ||
      packet.fds.size() > MKG_GPU_MAX_PACKET_FDS)
    throw Failure(MKG_GPU_IO_ERROR, "invalid or truncated packet");
  packet.bytes.assign(bytes.data(), static_cast<size_t>(length));
  packet.available = true;
  return packet;
}
uint32_t PeerPidSameUid(int socket) {
  ucred credentials{};
  socklen_t length = sizeof(credentials);
  if (getsockopt(socket, SOL_SOCKET, SO_PEERCRED, &credentials, &length) < 0 ||
      credentials.uid != getuid() || credentials.pid <= 0)
    throw Failure(MKG_GPU_IO_ERROR, "peer must have same uid and valid pid");
  return credentials.pid;
}
} // namespace mkg::ipc
