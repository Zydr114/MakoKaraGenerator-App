#define _GNU_SOURCE
#include <errno.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <unistd.h>
#include <systemd/sd-bus.h>

/* Probe-only read authority. Never Ref/Unref/Start/Stop/Kill a bus unit. */
#define DEST "org.freedesktop.systemd1"
#define UNIT "org.freedesktop.systemd1.Unit"
typedef struct { char sender[256]; uint32_t pid; } ref_entry;
typedef struct {
  char invocation[33], witness[256], version[256];
  uint32_t count;
  ref_entry entries[8];
} ref_sample;
int mkg_probe_sample_size(void) { return (int)sizeof(ref_sample); }

int mkg_probe_witness_open(void **out) {
  if (!out) return -EINVAL;
  *out = NULL;
  sd_bus *bus = NULL;
  int r = sd_bus_open_user(&bus);
  if (r < 0) return r;
  r = sd_bus_set_allow_interactive_authorization(bus, 0);
  if (r >= 0) r = sd_bus_set_method_call_timeout(bus, 1000000);
  if (r < 0) { sd_bus_unref(bus); return r; }
  *out = bus;
  return 0;
}
void mkg_probe_witness_close(void *bus) { sd_bus_unref(bus); }
static int copy(char *to, size_t cap, const char *from) {
  if (!from || strlen(from) >= cap) return -EOVERFLOW;
  memcpy(to, from, strlen(from) + 1);
  return 0;
}
static int invocation(sd_bus *bus, const char *path, char out[33]) {
  sd_bus_message *reply = NULL;
  int r = sd_bus_get_property(bus, DEST, path, UNIT, "InvocationID", NULL,
                            &reply, "ay");
  const void *bytes = NULL;
  size_t count = 0;
  if (r >= 0) r = sd_bus_message_read_array(reply, 'y', &bytes, &count);
  if (r >= 0 && count != 16) r = -EPROTO;
  if (r >= 0) {
    static const char hex[] = "0123456789abcdef";
    const unsigned char *v = bytes;
    for (size_t i = 0; i < 16; i++) {
      out[2*i] = hex[v[i] >> 4]; out[2*i+1] = hex[v[i] & 15];
    }
    out[32] = 0;
  }
  sd_bus_message_unref(reply);
  return r;
}
int mkg_probe_witness_sample(void *context, const char *path,
                             const char *expected, ref_sample *out) {
  sd_bus *bus = context;
  if (!bus || !path || !expected || strlen(expected) != 32 || !out)
    return -EINVAL;
  memset(out, 0, sizeof(*out));
  int r = invocation(bus, path, out->invocation);
  if (r < 0) return r;
  if (strcmp(out->invocation, expected)) return -ESTALE;
  const char *unique = NULL;
  r = sd_bus_get_unique_name(bus, &unique);
  if (r >= 0) r = copy(out->witness, sizeof(out->witness), unique);
  char *version = NULL;
  if (r >= 0) r = sd_bus_get_property_string(bus, DEST,
      "/org/freedesktop/systemd1", "org.freedesktop.systemd1.Manager",
      "Version", NULL, &version);
  if (r >= 0) r = copy(out->version, sizeof(out->version), version);
  free(version);
  if (r < 0) return r;
  sd_bus_message *reply = NULL;
  r = sd_bus_get_property(bus, DEST, path, UNIT, "Refs", NULL, &reply, "as");
  if (r >= 0) r = sd_bus_message_enter_container(reply, 'a', "s");
  const char *sender = NULL;
  while (r >= 0 && (r = sd_bus_message_read(reply, "s", &sender)) > 0) {
    if (out->count == 8) { r = -EOVERFLOW; break; }
    ref_entry *entry = &out->entries[out->count];
    r = copy(entry->sender, sizeof(entry->sender), sender);
    if (r < 0 || sender[0] != ':') { if (r >= 0) r = -EPROTO; break; }
    for (uint32_t i = 0; i < out->count; i++)
      if (!strcmp(entry->sender, out->entries[i].sender)) r = -EPROTO;
    if (r < 0) break;
    sd_bus_message *pid_reply = NULL;
    r = sd_bus_call_method(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus",
        "org.freedesktop.DBus", "GetConnectionUnixProcessID", NULL,
        &pid_reply, "s", entry->sender);
    if (r >= 0) r = sd_bus_message_read(pid_reply, "u", &entry->pid);
    sd_bus_message_unref(pid_reply);
    if (r >= 0 && entry->pid == 0) r = -EPROTO;
    if (r >= 0) out->count++;
  }
  sd_bus_message_unref(reply);
  char after[33];
  if (r >= 0) r = invocation(bus, path, after);
  if (r >= 0 && strcmp(after, expected)) r = -ESTALE;
  return r < 0 ? r : 0;
}
int mkg_probe_pidfd_open(uint32_t pid) {
  int fd = (int)syscall(SYS_pidfd_open, pid, 0);
  return fd < 0 ? -errno : fd;
}
int mkg_probe_pidfd_exited(int fd) {
  if (fd < 0) return -EINVAL;
  struct pollfd p = {.fd = fd, .events = POLLIN};
  int r;
  do r = poll(&p, 1, 0); while (r < 0 && errno == EINTR);
  if (r < 0) return -errno;
  if (p.revents & (POLLERR|POLLNVAL)) return -EIO;
  return (p.revents & (POLLIN|POLLHUP)) ? 1 : 0;
}
int mkg_probe_owned_pidfd_kill(int fd) {
  if (fd < 0) return -EINVAL;
  return syscall(SYS_pidfd_send_signal, fd, SIGKILL, NULL, 0) < 0 ? -errno : 0;
}
int mkg_probe_close_fd(int fd) { return close(fd) < 0 ? -errno : 0; }
int mkg_probe_peer_is_self_uid(int fd) {
  struct ucred cred;
  socklen_t size = sizeof(cred);
  if (getsockopt(fd, SOL_SOCKET, SO_PEERCRED, &cred, &size) < 0) return -errno;
  return size == sizeof(cred) && cred.uid == getuid() ? 1 : 0;
}
