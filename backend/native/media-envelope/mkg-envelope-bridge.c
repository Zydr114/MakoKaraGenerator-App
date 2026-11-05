/* Linux cgroup-v2 + direct systemd user manager. Never launches a broker. */
#define _GNU_SOURCE
#include "mkg-envelope-bridge.h"
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <limits.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <systemd/sd-bus.h>
#include <time.h>
#include <unistd.h>

#define DEST "org.freedesktop.systemd1"
#define MGRPATH "/org/freedesktop/systemd1"
#define MGR DEST ".Manager"
#define UNIT DEST ".Unit"
#define SERVICE DEST ".Service"
#define CHECK(x)                                                               \
  do {                                                                         \
    int _r = (x);                                                              \
    if (_r < 0) {                                                              \
      r = _r;                                                                  \
      goto fail;                                                               \
    }                                                                          \
  } while (0)
typedef struct {
  sd_bus *bus;
  int child[4], cg;
  char unit[256], path[512], nonce[33];
  char group[2048];
  uint64_t device, inode, last_oom;
  int referenced, start_attempted, removed, start_rejected;
  uint32_t captured_pid;
  uint64_t captured_ticks;
} ctx;

static int copy(char *to, size_t size, const char *s) {
  if (!s || strlen(s) >= size)
    return -EOVERFLOW;
  memcpy(to, s, strlen(s) + 1);
  return 0;
}
static void close_children(ctx *c) {
  for (int i = 0; i < 4; i++)
    if (c->child[i] >= 0) {
      close(c->child[i]);
      c->child[i] = -1;
    }
}
static int call(ctx *c, sd_bus_message *m, sd_bus_message **reply) {
  sd_bus_error e = SD_BUS_ERROR_NULL;
  int r = sd_bus_call(c->bus, m, 1000000, &e,
                      reply); /* bounded 1s; no interactive authorization */
  sd_bus_error_free(&e);
  return r;
}
static int method(ctx *c, const char *path, const char *iface,
                  const char *name) {
  sd_bus_message *m = NULL, *reply = NULL;
  int r;
  CHECK(sd_bus_message_new_method_call(c->bus, &m, DEST, path, iface, name));
  r = call(c, m, &reply);
fail:
  sd_bus_message_unref(reply);
  sd_bus_message_unref(m);
  return r;
}
static int lookup(ctx *c) {
  sd_bus_message *m = NULL, *reply = NULL;
  int r;
  const char *p = NULL;
  CHECK(sd_bus_message_new_method_call(c->bus, &m, DEST, MGRPATH, MGR,
                                       "GetUnit"));
  CHECK(sd_bus_message_append_basic(m, 's', c->unit));
  sd_bus_error error = SD_BUS_ERROR_NULL;
  r = sd_bus_call(c->bus, m, 1000000, &error, &reply);
  int missing =
      sd_bus_error_has_name(&error, "org.freedesktop.systemd1.NoSuchUnit");
  sd_bus_error_free(&error);
  if (r < 0) {
    if (missing)
      r = -ENOENT;
    goto fail;
  }
  CHECK(sd_bus_message_read_basic(reply, 'o', &p));
  CHECK(copy(c->path, sizeof(c->path), p));
  r = 0;
fail:
  sd_bus_message_unref(reply);
  sd_bus_message_unref(m);
  return r;
}
static int property(ctx *c, const char *iface, const char *name, char type,
                    void *out) {
  sd_bus_error e = SD_BUS_ERROR_NULL;
  int r = sd_bus_get_property_trivial(c->bus, DEST, c->path, iface, name, &e,
                                      type, out);
  sd_bus_error_free(&e);
  return r;
}
static int text(ctx *c, const char *iface, const char *name, char *out,
                size_t size) {
  char *p = NULL;
  sd_bus_error e = SD_BUS_ERROR_NULL;
  int r =
      sd_bus_get_property_string(c->bus, DEST, c->path, iface, name, &e, &p);
  sd_bus_error_free(&e);
  if (r >= 0)
    r = copy(out, size, p);
  free(p);
  return r;
}
static int invocation(ctx *c, char out[33]) {
  sd_bus_message *m = NULL;
  sd_bus_error e = SD_BUS_ERROR_NULL;
  const void *data = NULL;
  size_t size = 0;
  int r = sd_bus_get_property(c->bus, DEST, c->path, UNIT, "InvocationID", &e,
                              &m, "ay");
  sd_bus_error_free(&e);
  if (r >= 0)
    r = sd_bus_message_read_array(m, 'y', &data, &size);
  if (r >= 0 && size != 16)
    r = -EPROTO;
  if (r >= 0) {
    const unsigned char *b = data;
    for (size_t i = 0; i < 16; i++)
      snprintf(out + i * 2, 3, "%02x", b[i]);
  }
  sd_bus_message_unref(m);
  return r;
}
static int prop_begin(sd_bus_message *m, const char *name,
                      const char *signature) {
  int r = sd_bus_message_open_container(m, 'r', "sv");
  if (r < 0)
    return r;
  r = sd_bus_message_append_basic(m, 's', name);
  if (r < 0)
    return r;
  return sd_bus_message_open_container(m, 'v', signature);
}
static int prop_end(sd_bus_message *m) {
  int r = sd_bus_message_close_container(m);
  return r < 0 ? r : sd_bus_message_close_container(m);
}
static int prop_text(sd_bus_message *m, const char *name, const char *value) {
  int r = prop_begin(m, name, "s");
  if (r < 0)
    return r;
  r = sd_bus_message_append_basic(m, 's', value);
  return r < 0 ? r : prop_end(m);
}
static int prop_number(sd_bus_message *m, const char *name, char type,
                       const void *value) {
  char sig[2] = {type, 0};
  int r = prop_begin(m, name, sig);
  if (r < 0)
    return r;
  r = sd_bus_message_append_basic(m, type, value);
  return r < 0 ? r : prop_end(m);
}

int mkg_envelope_abi_size(void) { return (int)sizeof(mkg_snapshot); }

int mkg_envelope_open(void **out, int fds[4], int64_t *backing) {
  if (!out || !fds || !backing)
    return -EINVAL;
  ctx *c = calloc(1, sizeof(*c));
  if (!c)
    return -ENOMEM;
  c->cg = -1;
  for (int i = 0; i < 4; i++) {
    c->child[i] = -1;
    fds[i] = -1;
  }
  int r = sd_bus_open_user(&c->bus);
  if (r < 0)
    goto fail;
  CHECK(sd_bus_set_allow_interactive_authorization(c->bus, 0));
  CHECK(sd_bus_set_method_call_timeout(
      c->bus, 100000)); /* each property read bounded 100ms */
  *backing = 0;
  for (int i = 0; i < 4; i++) {
    int p[2];
    if (pipe2(p, O_CLOEXEC)) {
      r = -errno;
      goto fail;
    }
    if (fcntl(p[0], F_SETPIPE_SZ, 4096) < 0 ||
        fcntl(p[0], F_GETPIPE_SZ) != 4096) {
      close(p[0]);
      close(p[1]);
      r = -ENOTSUP;
      goto fail;
    }
    *backing += 4096;
    c->child[i] = p[i == 0 ? 0 : 1];
    fds[i] = p[i == 0 ? 1 : 0];
    int flags = fcntl(fds[i], F_GETFL);
    if (flags < 0 || fcntl(fds[i], F_SETFL, flags | O_NONBLOCK) < 0) {
      r = -errno;
      goto fail;
    }
  }
  *out = c;
  return 0;
fail:
  for (int i = 0; i < 4; i++)
    if (fds[i] >= 0) {
      close(fds[i]);
      fds[i] = -1;
    }
  close_children(c);
  sd_bus_close_unref(c->bus);
  free(c);
  return r;
}

int mkg_envelope_start(void *context, const char *nonce, const char *gate,
                       const char *tool, const char *const *arguments,
                       uint32_t count, uint64_t limit) {
  ctx *c = context;
  sd_bus_message *m = NULL, *reply = NULL;
  int r = 0, yes = 1, no = 0;
  uint64_t zero = 0, stop = 2000000;
  if (!c || c->start_attempted || !nonce || strlen(nonce) != 32 || !gate ||
      gate[0] != '/' || !tool || tool[0] != '/' || count > 128 || !limit ||
      limit % 4096)
    return -EINVAL;
  for (int i = 0; i < 32; i++)
    if (!((nonce[i] >= '0' && nonce[i] <= '9') ||
          (nonce[i] >= 'a' && nonce[i] <= 'f')))
      return -EINVAL;
  copy(c->nonce, sizeof(c->nonce), nonce);
  snprintf(c->unit, sizeof(c->unit), "mako-media-%s.service", nonce);
  char description[128];
  snprintf(description, sizeof(description), "Mako media envelope %s", nonce);
  CHECK(sd_bus_message_new_method_call(c->bus, &m, DEST, MGRPATH, MGR,
                                       "StartTransientUnit"));
  CHECK(sd_bus_message_append_basic(m, 's', c->unit));
  CHECK(sd_bus_message_append_basic(m, 's', "fail"));
  CHECK(sd_bus_message_open_container(m, 'a', "(sv)"));
  CHECK(prop_text(m, "Description", description));
  CHECK(prop_text(m, "Type", "exec"));
  CHECK(prop_text(m, "Restart", "no"));
  CHECK(prop_text(m, "KillMode", "control-group"));
  CHECK(prop_text(m, "OOMPolicy", "kill"));
  CHECK(prop_text(m, "CollectMode", "inactive-or-failed"));
  CHECK(prop_number(m, "AddRef", 'b', &yes));
  CHECK(prop_number(m, "MemoryAccounting", 'b', &yes));
  CHECK(prop_number(m, "Delegate", 'b', &no));
  CHECK(prop_number(m, "MemoryMax", 't', &limit));
  CHECK(prop_number(m, "MemorySwapMax", 't', &zero));
  CHECK(prop_number(m, "TimeoutStopUSec", 't', &stop));
  CHECK(prop_number(m, "SendSIGKILL", 'b', &yes));
  CHECK(prop_number(m, "NoNewPrivileges", 'b', &yes));
  CHECK(prop_number(m, "StandardInputFileDescriptor", 'h', &c->child[0]));
  CHECK(prop_number(m, "StandardOutputFileDescriptor", 'h', &c->child[1]));
  CHECK(prop_number(m, "StandardErrorFileDescriptor", 'h', &c->child[2]));
  CHECK(prop_begin(m, "ExtraFileDescriptors", "a(hs)"));
  CHECK(sd_bus_message_open_container(m, 'a', "(hs)"));
  CHECK(sd_bus_message_open_container(m, 'r', "hs"));
  CHECK(sd_bus_message_append_basic(m, 'h', &c->child[3]));
  CHECK(sd_bus_message_append_basic(m, 's', "mkg-metadata"));
  CHECK(sd_bus_message_close_container(m));
  CHECK(sd_bus_message_close_container(m));
  CHECK(prop_end(m));
  CHECK(prop_begin(m, "ExecStart", "a(sasb)"));
  CHECK(sd_bus_message_open_container(m, 'a', "(sasb)"));
  CHECK(sd_bus_message_open_container(m, 'r', "sasb"));
  CHECK(sd_bus_message_append_basic(m, 's', gate));
  CHECK(sd_bus_message_open_container(m, 'a', "s"));
  CHECK(sd_bus_message_append_basic(m, 's', gate));
  CHECK(sd_bus_message_append_basic(m, 's', "--"));
  CHECK(sd_bus_message_append_basic(m, 's', tool));
  for (uint32_t i = 0; i < count; i++)
    CHECK(sd_bus_message_append_basic(m, 's', arguments[i]));
  CHECK(sd_bus_message_close_container(m));
  CHECK(sd_bus_message_append_basic(m, 'b', &no));
  CHECK(sd_bus_message_close_container(m));
  CHECK(sd_bus_message_close_container(m));
  CHECK(prop_end(m));
  CHECK(sd_bus_message_close_container(m));
  CHECK(sd_bus_message_open_container(m, 'a', "(sa(sv))"));
  CHECK(sd_bus_message_close_container(m));
  c->start_attempted = 1;
  sd_bus_error start_error = SD_BUS_ERROR_NULL;
  r = sd_bus_call(c->bus, m, 1000000, &start_error, &reply);
  if (r < 0) {
    /* An explicit manager reply is conclusive rejection. Local transport/
     * timeout errors are reconciled by GetUnit on this SAME ordered bus. */
    if (sd_bus_error_is_set(&start_error) &&
        !sd_bus_error_has_name(&start_error, SD_BUS_ERROR_NO_REPLY) &&
        !sd_bus_error_has_name(&start_error, SD_BUS_ERROR_DISCONNECTED) &&
        !sd_bus_error_has_name(&start_error, SD_BUS_ERROR_TIMEOUT))
      c->start_rejected = 1;
    sd_bus_error_free(&start_error);
    goto fail;
  }
  sd_bus_error_free(&start_error);
  c->referenced = 1; /* AddRef was applied atomically, not a racy later Ref. */
  CHECK(lookup(c));
  r = 0;
fail:
  close_children(c);
  sd_bus_message_unref(reply);
  sd_bus_message_unref(m);
  return r;
}

static int read_file_at(int dir, const char *name, char *buf, size_t size) {
  int fd = openat(dir, name, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
  if (fd < 0)
    return -errno;
  ssize_t n = pread(fd, buf, size - 1, 0);
  int saved = errno;
  close(fd);
  if (n < 0)
    return -saved;
  if ((size_t)n == size - 1)
    return -EOVERFLOW;
  buf[n] = 0;
  return 0;
}
static int pin_cgroup(ctx *c, const char *path) {
  if (c->cg >= 0)
    return 0;
  size_t path_len = strlen(path), unit_len = strlen(c->unit);
  if (path[0] != '/' || path_len > 2000 || strstr(path, "..") ||
      strstr(path, "//") || path_len <= unit_len ||
      path[path_len - unit_len - 1] != '/' ||
      strcmp(path + path_len - unit_len, c->unit))
    return -EPROTO;
  int dir =
      open("/sys/fs/cgroup", O_DIRECTORY | O_PATH | O_CLOEXEC | O_NOFOLLOW);
  if (dir < 0)
    return -errno;
  char rest[2048];
  copy(rest, sizeof(rest), path + 1);
  char *save = NULL, *part = strtok_r(rest, "/", &save);
  while (part) {
    int next = openat(dir, part, O_DIRECTORY | O_PATH | O_CLOEXEC | O_NOFOLLOW);
    close(dir);
    if (next < 0)
      return -errno;
    dir = next;
    part = strtok_r(NULL, "/", &save);
  }
  struct stat st;
  if (fstat(dir, &st)) {
    int e = errno;
    close(dir);
    return -e;
  }
  c->cg = dir;
  c->device = st.st_dev;
  c->inode = st.st_ino;
  return 0;
}
static int uint_file(ctx *c, const char *name, uint64_t *out) {
  char buf[256];
  int r = read_file_at(c->cg, name, buf, sizeof(buf));
  if (r < 0)
    return r;
  if (!strcmp(buf, "max\n")) {
    *out = UINT64_MAX;
    return 0;
  }
  char *end;
  errno = 0;
  unsigned long long n = strtoull(buf, &end, 10);
  if (errno || (*end != '\n' && *end))
    return -EPROTO;
  *out = n;
  return 0;
}
static int proc_identity(uint32_t pid, const char *group, uint64_t *ticks,
                         int *member) {
  char path[128], buf[8192];
  /* Publish a live sample only after both proc files were read. A process can
   * disappear between them; no partial start tick may escape that gone case. */
  *ticks = 0;
  *member = 0;
  snprintf(path, sizeof(path), "/proc/%u/stat", pid);
  int fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
  if (fd < 0)
    return errno == ENOENT ? 0 : -errno;
  ssize_t n = read(fd, buf, sizeof(buf) - 1);
  int saved = errno;
  close(fd);
  if (n < 0)
    return -saved;
  if (!n)
    return -EIO;
  buf[n] = 0;
  char *end = strrchr(buf, ')');
  if (!end)
    return -EPROTO;
  char *save = NULL, *part = strtok_r(end + 2, " ", &save);
  for (int i = 0; i < 19 && part; i++)
    part = strtok_r(NULL, " ", &save);
  if (!part)
    return -EPROTO;
  uint64_t observed_ticks = strtoull(part, NULL, 10);
  snprintf(path, sizeof(path), "/proc/%u/cgroup", pid);
  fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
  if (fd < 0)
    return errno == ENOENT ? 0 : -errno;
  n = read(fd, buf, sizeof(buf) - 1);
  saved = errno;
  close(fd);
  if (n < 0)
    return -saved;
  if (!n)
    return -EIO;
  buf[n] = 0;
  char expected[2100];
  snprintf(expected, sizeof(expected), "0::%s\n", group);
  *ticks = observed_ticks;
  *member = !strcmp(buf, expected);
  return 0;
}
int mkg_envelope_snapshot(void *context, mkg_snapshot *s) {
  ctx *c = context;
  int r = 0;
  memset(s, 0, sizeof(*s));
  if (!c || !c->start_attempted)
    return -EINVAL;
  CHECK(lookup(c));
  CHECK(copy(s->unit, sizeof(s->unit), c->unit));
  CHECK(copy(s->path, sizeof(s->path), c->path));
  CHECK(invocation(c, s->invocation));
  CHECK(text(c, UNIT, "Id", s->unit, sizeof(s->unit)));
  CHECK(text(c, UNIT, "Description", s->description, sizeof(s->description)));
  CHECK(text(c, UNIT, "ActiveState", s->active, sizeof(s->active)));
  CHECK(text(c, SERVICE, "Result", s->result, sizeof(s->result)));
  CHECK(property(c, SERVICE, "MainPID", 'u', &s->main_pid));
  CHECK(property(c, SERVICE, "ExecMainCode", 'i', &s->exec_code));
  CHECK(property(c, SERVICE, "ExecMainStatus", 'i', &s->exec_status));
  CHECK(text(c, SERVICE, "ControlGroup", s->cgroup, sizeof(s->cgroup)));
  int terminal = !strcmp(s->active, "inactive") || !strcmp(s->active, "failed");
  if (s->cgroup[0]) {
    CHECK(copy(c->group, sizeof(c->group), s->cgroup));
  } else if (c->cg >= 0 && terminal)
    CHECK(copy(s->cgroup, sizeof(s->cgroup), c->group));
  else if (terminal && !s->main_pid) {
    s->cg_removed = 1;
    goto controls;
  } else {
    r = -EPROTO;
    goto fail;
  }
  r = pin_cgroup(c, s->cgroup);
  if (r == -ENOENT && terminal && !s->main_pid && c->cg < 0) {
    s->cgroup[0] = 0;
    s->cg_removed = 1;
    r = 0;
    goto controls;
  }
  CHECK(r);
  s->cg_device = c->device;
  s->cg_inode = c->inode;
  struct stat st;
  CHECK(fstat(c->cg, &st) < 0 ? -errno : 0);
  if ((uint64_t)st.st_dev != c->device || (uint64_t)st.st_ino != c->inode) {
    r = -ESTALE;
    goto fail;
  }
  char events[4096];
  r = read_file_at(c->cg, "cgroup.events", events, sizeof(events));
  if (r == -ENOENT) {
    c->removed = 1;
    s->cg_removed = 1;
    s->oom_kills = c->last_oom;
    r = 0;
    goto controls;
  }
  CHECK(r);
  s->cg_exists = 1;
  int kill_fd = openat(c->cg, "cgroup.kill", O_WRONLY | O_CLOEXEC | O_NOFOLLOW);
  if (kill_fd >= 0) {
    s->kill_available = 1;
    close(kill_fd);
  }
  s->cg_populated = strstr(events, "populated 1\n") != NULL;
  r = read_file_at(c->cg, "memory.events", events, sizeof(events));
  if (r >= 0) {
    char *p = strstr(events, "oom_kill ");
    if (p) {
      s->oom_kills = strtoull(p + 9, NULL, 10);
      if (s->oom_kills > c->last_oom)
        c->last_oom = s->oom_kills;
    }
  }
controls:
  /* A missing capability must not prevent owner capture and safe gate drain. */
  s->controls_complete = 1;
#define CAP(x)                                                                 \
  do {                                                                         \
    if ((x) < 0)                                                               \
      s->controls_complete = 0;                                                \
  } while (0)
  CAP(text(c, SERVICE, "Type", s->type, sizeof(s->type)));
  CAP(text(c, SERVICE, "KillMode", s->kill_mode, sizeof(s->kill_mode)));
  CAP(text(c, SERVICE, "Restart", s->restart, sizeof(s->restart)));
  CAP(text(c, SERVICE, "OOMPolicy", s->oom_policy, sizeof(s->oom_policy)));
  CAP(property(c, SERVICE, "MemoryAccounting", 'b', &s->accounting));
  CAP(property(c, SERVICE, "Delegate", 'b', &s->delegate));
  CAP(property(c, SERVICE, "MemoryMax", 't', &s->memory_max));
  CAP(property(c, SERVICE, "EffectiveMemoryMax", 't', &s->effective_max));
  CAP(property(c, SERVICE, "MemorySwapMax", 't', &s->swap_max));
  if (s->cg_exists) {
    CAP(uint_file(c, "memory.max", &s->kernel_max));
    CAP(uint_file(c, "memory.swap.max", &s->kernel_swap));
    uint64_t oom = 0;
    CAP(uint_file(c, "memory.oom.group", &oom));
    s->oom_group = oom == 1;
  } else
    s->controls_complete = 0;
#undef CAP
  if (s->main_pid) {
    CHECK(proc_identity(s->main_pid, s->cgroup, &s->start_ticks,
                        &s->main_in_group));
    if (!s->start_ticks)
      s->main_pid = 0;
    else if (!c->captured_pid) {
      c->captured_pid = s->main_pid;
      c->captured_ticks = s->start_ticks;
    }
  }
  r = 0;
fail:
  return r;
}
int mkg_envelope_start_absent(void *context) {
  ctx *c = context;
  if (!c || !c->start_attempted)
    return 1;
  if (c->start_rejected)
    return 1;
  int r = lookup(c);
  if (r == -ENOENT)
    return 1;
  if (r < 0)
    return r;
  c->referenced = 1; /* Any accepted request contained AddRef. */
  return 0;
}
static int verify(ctx *c, const char *id, uint64_t device, uint64_t inode) {
  if (!c || c->device != device || c->inode != inode)
    return -ESTALE;
  char actual[33];
  int r = invocation(c, actual);
  if (r < 0)
    return r;
  if (strcmp(actual, id))
    return -ESTALE;
  char desc[128], expected[128];
  r = text(c, UNIT, "Description", desc, sizeof(desc));
  if (r < 0)
    return r;
  snprintf(expected, sizeof(expected), "Mako media envelope %s", c->nonce);
  if (strcmp(desc, expected))
    return -ESTALE;
  if (c->cg < 0) {
    mkg_snapshot s;
    r = mkg_envelope_snapshot(c, &s);
    if (r < 0)
      return r;
    return !s.main_pid && !s.cg_populated && s.cg_removed ? 0 : -ESTALE;
  }
  char group[2048];
  r = text(c, SERVICE, "ControlGroup", group, sizeof(group));
  if (r < 0)
    return r;
  if (group[0] && strcmp(group, c->group))
    return -ESTALE;
  uint32_t pid = 0;
  r = property(c, SERVICE, "MainPID", 'u', &pid);
  if (r < 0)
    return r;
  if (pid) {
    uint64_t ticks = 0;
    int member = 0;
    r = proc_identity(pid, c->group, &ticks, &member);
    if (r < 0)
      return r;
    if (ticks &&
        (pid != c->captured_pid || ticks != c->captured_ticks || !member))
      return -ESTALE;
  }
  struct stat st;
  if (fstat(c->cg, &st))
    return -errno;
  return (uint64_t)st.st_dev == device && (uint64_t)st.st_ino == inode
             ? 0
             : -ESTALE;
}
static int kill_pinned_cgroup(ctx *c) {
  /* Pinned inode: even if unit-name lookup races, no replacement cgroup is hit.
   */
  int fd = openat(c->cg, "cgroup.kill", O_WRONLY | O_CLOEXEC | O_NOFOLLOW);
  if (fd < 0) {
    int saved = errno;
    if (saved != ENOENT)
      return -saved;
    /* The cgroup can disappear after the last snapshot. Only absence of its
     * events file through the SAME pinned directory proves this no-op kill.
     * A missing kill control on an existing cgroup remains an error. This says
     * nothing about manager terminal state, pipe EOF or completed drain. */
    char events[4096];
    int r = read_file_at(c->cg, "cgroup.events", events, sizeof(events));
    if (r == -ENOENT) {
      c->removed = 1;
      return 0;
    }
    return r < 0 ? r : -saved;
  }
  ssize_t n = write(fd, "1\n", 2);
  int saved = errno;
  close(fd);
  return n == 2 ? 0 : n < 0 ? -saved : -EIO;
}
int mkg_envelope_kill(void *context, const char *id, uint64_t device,
                      uint64_t inode) {
  ctx *c = context;
  int r = verify(c, id, device, inode);
  if (r < 0)
    return r;
  if (c->cg < 0)
    return 0;
  return kill_pinned_cgroup(c);
}
int mkg_envelope_unref(void *context, const char *id, uint64_t device,
                       uint64_t inode) {
  ctx *c = context;
  int r = verify(c, id, device, inode);
  if (r < 0)
    return r;
  if (c->referenced) {
    r = method(c, c->path, UNIT, "Unref");
    if (r < 0)
      return r;
    c->referenced = 0;
  }
  return 0;
}
void mkg_envelope_close(void *context) {
  ctx *c = context;
  if (!c)
    return;
  close_children(c);
  if (c->cg >= 0)
    close(c->cg);
  sd_bus_close_unref(c->bus);
  free(c);
}

int mkg_pipe_io(int fd, void *buffer, uint32_t count, int writing,
                int timeout_ms) {
  if (fd < 0 || !buffer || !count || count > INT_MAX || timeout_ms < 0 ||
      timeout_ms > 100)
    return -EINVAL;
  struct pollfd p = {.fd = fd, .events = writing ? POLLOUT : POLLIN};
  int ready = poll(&p, 1, timeout_ms);
  if (ready < 0)
    return errno == EINTR ? -EAGAIN : -errno;
  if (!ready)
    return -EAGAIN;
  if (p.revents & POLLNVAL)
    return -EBADF;
  if (!writing) {
    ssize_t n = read(fd, buffer, count);
    return n < 0 ? -errno : (int)n;
  }
  /* Block SIGPIPE only on this call's thread; consume only a newly generated
   * signal after EPIPE. Do not change process-wide .NET signal dispositions. */
  sigset_t block, old, pending;
  sigemptyset(&block);
  sigaddset(&block, SIGPIPE);
  int mask = pthread_sigmask(SIG_BLOCK, &block, &old);
  if (mask)
    return -mask;
  sigpending(&pending);
  int was_pending = sigismember(&pending, SIGPIPE);
  ssize_t n = write(fd, buffer, count);
  int saved = errno;
  if (n < 0 && saved == EPIPE && !was_pending) {
    struct timespec zero = {0};
    while (sigtimedwait(&block, NULL, &zero) < 0 && errno == EINTR) {
    }
  }
  int restored = pthread_sigmask(SIG_SETMASK, &old, NULL);
  if (restored)
    return -restored;
  return n < 0 ? -saved : (int)n;
}
