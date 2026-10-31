#include <cstring>
#include <errno.h>
#include <fcntl.h>
#include <node_api.h>
#include <string>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#include <unordered_set>
#include <vector>

namespace {
constexpr size_t kMaxPacket = 16384, kMaxFds = 4;
struct State {
  std::unordered_set<int> received;
};
struct Connection {
  int fd = -1;
  pid_t pid = 0;
};
napi_value Fail(napi_env e, const char *what) {
  napi_throw_error(e, nullptr, what);
  return nullptr;
}
napi_value Number(napi_env e, int v) {
  napi_value n;
  napi_create_int32(e, v, &n);
  return n;
}
napi_value Undefined(napi_env e) {
  napi_value v;
  napi_get_undefined(e, &v);
  return v;
}
bool String(napi_env e, napi_value v, std::string &s) {
  napi_valuetype t;
  napi_typeof(e, v, &t);
  if (t != napi_string)
    return false;
  size_t n;
  napi_get_value_string_utf8(e, v, nullptr, 0, &n);
  if (n > kMaxPacket)
    return false;
  std::vector<char> b(n + 1);
  napi_get_value_string_utf8(e, v, b.data(), b.size(), &n);
  s.assign(b.data(), n);
  return true;
}
Connection *Conn(napi_env e, napi_value v) {
  void *p = nullptr;
  if (napi_get_value_external(e, v, &p) != napi_ok || !p)
    return nullptr;
  return static_cast<Connection *>(p);
}
void FinalizeConnection(napi_env, void *p, void *) {
  auto *c = static_cast<Connection *>(p);
  if (c->fd >= 0)
    ::close(c->fd);
  delete c;
}
void FinalizeState(void *p) {
  auto *s = static_cast<State *>(p);
  for (int fd : s->received)
    ::close(fd);
  delete s;
}
napi_value Connect(napi_env e, napi_callback_info info) {
  size_t n = 1;
  napi_value a[1];
  napi_get_cb_info(e, info, &n, a, nullptr, nullptr);
  std::string path;
  if (n != 1 || !String(e, a[0], path) || path.empty() ||
      path.find('\0') != std::string::npos ||
      path.size() >= sizeof(sockaddr_un::sun_path))
    return Fail(e, "invalid socket path");
  int fd = socket(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
  if (fd < 0)
    return Fail(e, "socket failed");
  sockaddr_un addr{};
  addr.sun_family = AF_UNIX;
  std::memcpy(addr.sun_path, path.c_str(), path.size() + 1);
  if (connect(fd, reinterpret_cast<sockaddr *>(&addr), sizeof(addr)) < 0) {
    ::close(fd);
    return Fail(e, "connect failed");
  }
  ucred cred{};
  socklen_t len = sizeof(cred);
  if (getsockopt(fd, SOL_SOCKET, SO_PEERCRED, &cred, &len) < 0 ||
      cred.uid != getuid()) {
    ::close(fd);
    return Fail(e, "producer must have same uid");
  }
  int flags = fcntl(fd, F_GETFL);
  if (flags < 0 || fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0) {
    ::close(fd);
    return Fail(e, "nonblocking setup failed");
  }
  auto *c = new Connection{fd, cred.pid};
  napi_value result;
  napi_create_external(e, c, FinalizeConnection, nullptr, &result);
  return result;
}
napi_value Receive(napi_env e, napi_callback_info info) {
  size_t n = 1;
  napi_value a[1];
  void *data;
  napi_get_cb_info(e, info, &n, a, nullptr, &data);
  auto *c = n == 1 ? Conn(e, a[0]) : nullptr;
  if (!c || c->fd < 0)
    return Fail(e, "closed connection");
  char bytes[kMaxPacket];
  alignas(cmsghdr) char control[CMSG_SPACE(kMaxFds * sizeof(int))]{};
  iovec io{bytes, sizeof(bytes)};
  msghdr msg{};
  msg.msg_iov = &io;
  msg.msg_iovlen = 1;
  msg.msg_control = control;
  msg.msg_controllen = sizeof(control);
  ssize_t len = recvmsg(c->fd, &msg, MSG_DONTWAIT | MSG_CMSG_CLOEXEC);
  if (len < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
    napi_value v;
    napi_get_null(e, &v);
    return v;
  }
  if (len < 0)
    return Fail(e, "recvmsg failed");
  std::vector<int> fds;
  bool invalid = false;
  for (cmsghdr *h = CMSG_FIRSTHDR(&msg); h; h = CMSG_NXTHDR(&msg, h)) {
    if (h->cmsg_level != SOL_SOCKET || h->cmsg_type != SCM_RIGHTS ||
        h->cmsg_len < CMSG_LEN(0)) {
      invalid = true;
      continue;
    }
    size_t count = (h->cmsg_len - CMSG_LEN(0)) / sizeof(int);
    const auto *values = reinterpret_cast<int *>(CMSG_DATA(h));
    fds.insert(fds.end(), values, values + count);
  }
  auto cleanup = [&]() {
    for (int fd : fds)
      ::close(fd);
  };
  if (len == 0) {
    cleanup();
    return Fail(e,
                "producer disconnected; outstanding slots must be quarantined");
  }
  if (invalid || (msg.msg_flags & (MSG_TRUNC | MSG_CTRUNC)) ||
      fds.size() > kMaxFds) {
    cleanup();
    return Fail(e, "invalid or truncated packet");
  }
  napi_value global, json, parse, payload, metadata;
  napi_get_global(e, &global);
  napi_get_named_property(e, global, "JSON", &json);
  napi_get_named_property(e, json, "parse", &parse);
  napi_create_string_utf8(e, bytes, static_cast<size_t>(len), &payload);
  if (napi_call_function(e, json, parse, 1, &payload, &metadata) != napi_ok) {
    cleanup();
    return nullptr;
  }
  napi_valuetype type;
  napi_typeof(e, metadata, &type);
  bool isArray = false;
  napi_is_array(e, metadata, &isArray);
  napi_value nullValue;
  napi_get_null(e, &nullValue);
  bool null = false;
  napi_strict_equals(e, metadata, nullValue, &null);
  if (type != napi_object || isArray || null) {
    cleanup();
    return Fail(e, "packet metadata must be a plain JSON object");
  }
  auto *s = static_cast<State *>(data);
  napi_value result, array;
  napi_create_object(e, &result);
  napi_create_array_with_length(e, fds.size(), &array);
  for (size_t i = 0; i < fds.size(); i++) {
    s->received.insert(fds[i]);
    napi_set_element(e, array, i, Number(e, fds[i]));
  }
  napi_set_named_property(e, result, "metadata", metadata);
  napi_set_named_property(e, result, "fds", array);
  napi_set_named_property(e, result, "peerPid", Number(e, c->pid));
  return result;
}
napi_value Send(napi_env e, napi_callback_info info) {
  size_t n = 2;
  napi_value a[2];
  napi_get_cb_info(e, info, &n, a, nullptr, nullptr);
  auto *c = n == 2 ? Conn(e, a[0]) : nullptr;
  std::string bytes;
  if (!c || c->fd < 0 || !String(e, a[1], bytes) || bytes.empty())
    return Fail(e, "send expects connection and bounded JSON string");
  ssize_t sent =
      send(c->fd, bytes.data(), bytes.size(), MSG_NOSIGNAL | MSG_DONTWAIT);
  if (sent != static_cast<ssize_t>(bytes.size()))
    return Fail(e, "send failed; do not infer producer slot release");
  return Undefined(e);
}
napi_value CloseFd(napi_env e, napi_callback_info info) {
  size_t n = 1;
  napi_value a[1];
  void *data;
  napi_get_cb_info(e, info, &n, a, nullptr, &data);
  int32_t fd;
  if (n != 1 || napi_get_value_int32(e, a[0], &fd) != napi_ok)
    return Fail(e, "invalid fd");
  auto *s = static_cast<State *>(data);
  if (s->received.erase(fd) != 1)
    return Fail(e, "fd is not owned by this broker or already closed");
  ::close(fd);
  return Undefined(e);
}
napi_value Close(napi_env e, napi_callback_info info) {
  size_t n = 1;
  napi_value a[1];
  napi_get_cb_info(e, info, &n, a, nullptr, nullptr);
  auto *c = n == 1 ? Conn(e, a[0]) : nullptr;
  if (!c)
    return Fail(e, "invalid connection");
  if (c->fd >= 0) {
    ::close(c->fd);
    c->fd = -1;
  }
  return Undefined(e);
}
napi_value Init(napi_env e, napi_value exports) {
  auto *s = new State;
  if (napi_add_env_cleanup_hook(e, FinalizeState, s) != napi_ok) {
    delete s;
    return Fail(e, "cleanup registration failed");
  }
  const struct {
    const char *name;
    napi_callback fn;
  } methods[] = {{"connect", Connect},
                 {"receive", Receive},
                 {"send", Send},
                 {"closeFd", CloseFd},
                 {"close", Close}};
  for (auto m : methods) {
    napi_value fn;
    napi_create_function(e, m.name, NAPI_AUTO_LENGTH, m.fn, s, &fn);
    napi_set_named_property(e, exports, m.name, fn);
  }
  return exports;
}
} // namespace
NAPI_MODULE(NODE_GYP_MODULE_NAME, Init)
