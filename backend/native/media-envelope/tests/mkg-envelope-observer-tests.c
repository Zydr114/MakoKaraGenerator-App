/* Controlled tests of the actual included implementation. No user manager,
 * cgroup, /proc, helper or subprocess is accessed. Link libsystemd only for
 * production functions outside the controlled paths exercised here. */
#define _GNU_SOURCE
#include "../mkg-envelope-bridge.h"
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

static int controlled_open_user(sd_bus **out);
static int controlled_interactive(sd_bus *bus, int enabled);
static int controlled_timeout(sd_bus *bus, uint64_t timeout);
static int controlled_new(sd_bus *bus, sd_bus_message **out,
                          const char *destination, const char *path,
                          const char *interface, const char *member);
static int controlled_call(sd_bus *bus, sd_bus_message *message, uint64_t timeout,
                           sd_bus_error *error, sd_bus_message **reply);
static sd_bus_message *controlled_message_unref(sd_bus_message *message);
static sd_bus *controlled_bus_close(sd_bus *bus);
static int controlled_close(int fd);
static int controlled_pipe2(int descriptors[2], int flags);

#define sd_bus_open_user controlled_open_user
#define sd_bus_set_allow_interactive_authorization controlled_interactive
#define sd_bus_set_method_call_timeout controlled_timeout
#define sd_bus_message_new_method_call controlled_new
#define sd_bus_call controlled_call
#define sd_bus_message_unref controlled_message_unref
#define sd_bus_close_unref controlled_bus_close
#define close controlled_close
#define pipe2 controlled_pipe2
#include "../mkg-envelope-bridge.c"
#undef sd_bus_open_user
#undef sd_bus_set_allow_interactive_authorization
#undef sd_bus_set_method_call_timeout
#undef sd_bus_message_new_method_call
#undef sd_bus_call
#undef sd_bus_message_unref
#undef sd_bus_close_unref
#undef close
#undef pipe2

#define HOST_BUS ((sd_bus *)(uintptr_t)0x101)
#define WORKER_BUS ((sd_bus *)(uintptr_t)0x202)
#define REQUEST ((sd_bus_message *)(uintptr_t)0x303)
#define REPLY ((sd_bus_message *)(uintptr_t)0x404)
static const uint64_t limit = UINT64_C(67108864);
static ctx *call_owner;
static const char *wanted_member;
static int call_result, new_result, call_count, bus_close_count, fd_close_count;
static int pipe_count, open_count, expected_ref_state;
static unsigned int checks;

static void check(int condition, const char *message) {
  checks++;
  if (!condition) {
    fprintf(stderr, "observer-test-failed: %s\n", message);
    exit(1);
  }
}
static int controlled_open_user(sd_bus **out) {
  open_count++;
  *out = HOST_BUS;
  return 0;
}
static int controlled_interactive(sd_bus *bus, int enabled) {
  check(bus == HOST_BUS && enabled == 0, "own bus disables interactive auth");
  return 0;
}
static int controlled_timeout(sd_bus *bus, uint64_t timeout) {
  check(bus == HOST_BUS && timeout == 100000, "own bus property timeout");
  return 0;
}
static int controlled_new(sd_bus *bus, sd_bus_message **out,
                          const char *destination, const char *path,
                          const char *interface, const char *member) {
  check(call_owner && bus == HOST_BUS && bus == call_owner->bus,
        "reference request uses independent Host bus");
  check(!strcmp(destination, DEST) && !strcmp(path, call_owner->path) &&
            !strcmp(interface, UNIT) && !strcmp(member, wanted_member),
        "reference request uses exact pinned owner object path and method");
  if (new_result < 0)
    return new_result;
  *out = REQUEST;
  return 0;
}
static int controlled_call(sd_bus *bus, sd_bus_message *message, uint64_t timeout,
                           sd_bus_error *error, sd_bus_message **reply) {
  (void)error;
  check(bus == HOST_BUS && message == REQUEST && timeout == 1000000,
        "bounded call stays on independent connection");
  check(call_owner->observer_ref_state == expected_ref_state,
        "uncertainty is published BEFORE manager call can accept");
  call_count++;
  if (call_result >= 0)
    *reply = REPLY;
  return call_result;
}
static sd_bus_message *controlled_message_unref(sd_bus_message *message) {
  check(!message || message == REQUEST || message == REPLY, "owned method messages only");
  return NULL;
}
static sd_bus *controlled_bus_close(sd_bus *bus) {
  check(bus == HOST_BUS, "close never touches Worker connection");
  bus_close_count++;
  return NULL;
}
static int controlled_close(int fd) {
  check(fd == 73, "close only original cgroup descriptor");
  fd_close_count++;
  return 0;
}
static int controlled_pipe2(int descriptors[2], int flags) {
  (void)descriptors;
  (void)flags;
  pipe_count++;
  check(0, "observer never creates native pipes");
  return -1;
}
static mkg_observer_identity identity(void) {
  mkg_observer_identity e = {0};
  copy(e.nonce, sizeof(e.nonce), "11111111111111111111111111111111");
  copy(e.unit, sizeof(e.unit), "mako-media-11111111111111111111111111111111.service");
  copy(e.path, sizeof(e.path), "/org/freedesktop/systemd1/unit/owned");
  copy(e.invocation, sizeof(e.invocation), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
  snprintf(e.group, sizeof(e.group), "/user.slice/%s", e.unit);
  e.main_pid = 424242;
  e.start_ticks = 991;
  e.cg_device = 31;
  e.cg_inode = 47;
  return e;
}
static mkg_snapshot live_snapshot(void) {
  mkg_observer_identity e = identity();
  mkg_snapshot s = {0};
  copy(s.unit, sizeof(s.unit), e.unit);
  copy(s.path, sizeof(s.path), e.path);
  copy(s.invocation, sizeof(s.invocation), e.invocation);
  snprintf(s.description, sizeof(s.description), "Mako media envelope %s", e.nonce);
  copy(s.cgroup, sizeof(s.cgroup), e.group);
  copy(s.type, sizeof(s.type), "exec");
  copy(s.kill_mode, sizeof(s.kill_mode), "control-group");
  copy(s.restart, sizeof(s.restart), "no");
  copy(s.oom_policy, sizeof(s.oom_policy), "kill");
  copy(s.active, sizeof(s.active), "active");
  s.main_pid = e.main_pid;
  s.start_ticks = e.start_ticks;
  s.cg_device = e.cg_device;
  s.cg_inode = e.cg_inode;
  s.main_in_group = s.cg_exists = s.cg_populated = s.oom_group = 1;
  s.accounting = s.kill_available = s.controls_complete = 1;
  s.memory_max = s.effective_max = s.kernel_max = limit;
  return s;
}
static ctx *new_owner(void) {
  void *raw = NULL;
  mkg_observer_identity e = identity();
  check(mkg_envelope_observer_open(&raw, &e, limit) == 0 && raw,
        "observer open creates owned no-Ref context");
  ctx *c = raw;
  check(c->bus == HOST_BUS && c->observer && !c->start_attempted &&
            !c->referenced && c->cg == -1 &&
            c->observer_ref_state == OBSERVER_REF_NONE,
        "observer is independent from Worker start/reference state");
  for (int i = 0; i < 4; i++)
    check(c->child[i] == -1, "no child endpoint allocated");
  check(!strcmp(c->observer_expected.nonce, e.nonce) &&
            !strcmp(c->observer_expected.unit, e.unit) &&
            !strcmp(c->observer_expected.path, e.path) &&
            !strcmp(c->observer_expected.invocation, e.invocation) &&
            !strcmp(c->observer_expected.group, e.group) &&
            c->observer_expected.main_pid == e.main_pid &&
            c->observer_expected.start_ticks == e.start_ticks &&
            c->observer_expected.cg_device == e.cg_device &&
            c->observer_expected.cg_inode == e.cg_inode, "immutable expected owner stored");
  return c;
}
static void expected_validation(void) {
  mkg_observer_identity e = identity(), changed;
  check(observer_expected_valid(&e, limit), "valid full expected identity");
  changed = e; changed.nonce[0] = 'A';
  check(!observer_expected_valid(&changed, limit), "reject uppercase nonce");
  changed = e; copy(changed.unit, sizeof(changed.unit), "other.service");
  check(!observer_expected_valid(&changed, limit), "nonce must derive exact unit");
  changed = e; copy(changed.path, sizeof(changed.path), "/bad//object");
  check(!observer_expected_valid(&changed, limit), "reject malformed object path");
  changed = e; memset(changed.invocation, '0', 32);
  check(!observer_expected_valid(&changed, limit), "reject zero invocation");
  changed = e; snprintf(changed.group, sizeof(changed.group), "/../%s", e.unit);
  check(!observer_expected_valid(&changed, limit), "reject group traversal");
  changed = e; changed.main_pid = 0;
  check(!observer_expected_valid(&changed, limit), "held admission needs captured live PID");
  changed = e; changed.start_ticks = 0;
  check(!observer_expected_valid(&changed, limit), "held admission needs start ticks");
  changed = e; changed.cg_inode = 0;
  check(!observer_expected_valid(&changed, limit), "held admission needs original inode");
  changed = e; memset(changed.path, 'x', sizeof(changed.path));
  check(!observer_expected_valid(&changed, limit), "fixed string must be terminated");
  check(!observer_expected_valid(&e, limit + 1), "limit must be page aligned");
}
static void owner_and_control_validation(void) {
  ctx owner = {.observer_expected = identity()};
  mkg_snapshot original = live_snapshot(), s;
  check(observer_snapshot_matches(&owner, &original) == 0, "original complete owner");
#define REJECT_OWNER(change, detail) do { s = original; change; \
  check(observer_snapshot_matches(&owner, &s) == -ESTALE, detail); } while (0)
  REJECT_OWNER(s.unit[0] = 'z', "unit replacement");
  REJECT_OWNER(s.path[1] = 'z', "object path replacement");
  REJECT_OWNER(s.invocation[0] = 'b', "invocation replacement");
  REJECT_OWNER(s.description[0] = 'z', "nonce description replacement");
  REJECT_OWNER(s.cgroup[1] = 'z', "group replacement");
  REJECT_OWNER(s.cg_device++, "device replacement");
  REJECT_OWNER(s.cg_inode++, "inode replacement");
  REJECT_OWNER(s.main_pid++, "live PID replacement");
  REJECT_OWNER(s.start_ticks++, "live start tick replacement");
  REJECT_OWNER(s.main_in_group = 0, "live membership mismatch");
#undef REJECT_OWNER
  s = original; s.main_pid = 0; s.start_ticks = 0; s.main_in_group = 0;
  check(observer_snapshot_matches(&owner, &s) == 0 && !observer_drain_confirmed(&s),
        "sequential exit race allowed without inventing drain");
  check(observer_controls_match(&original, limit), "all hard controls satisfied");
#define REJECT_CONTROL(change, detail) do { s = original; change; \
  check(!observer_controls_match(&s, limit), detail); } while (0)
  REJECT_CONTROL(s.controls_complete = 0, "incomplete controls");
  REJECT_CONTROL(s.kill_available = 0, "cgroup.kill unavailable");
  REJECT_CONTROL(s.main_pid = 0, "held PID vanished");
  REJECT_CONTROL(s.start_ticks = 0, "held start ticks vanished");
  REJECT_CONTROL(s.main_in_group = 0, "held membership mismatch");
  REJECT_CONTROL(s.cg_exists = 0, "held pinned controls missing");
  REJECT_CONTROL(s.cg_inode = 0, "held inode missing");
  REJECT_CONTROL(s.memory_max++, "manager memory max mismatch");
  REJECT_CONTROL(s.kernel_max--, "kernel memory max mismatch");
  REJECT_CONTROL(s.effective_max = 0, "effective memory max missing");
  REJECT_CONTROL(s.effective_max++, "ancestor memory max too large");
  REJECT_CONTROL(s.swap_max = 1, "manager swap not disabled");
  REJECT_CONTROL(s.kernel_swap = 1, "kernel swap not disabled");
  REJECT_CONTROL(s.oom_group = 0, "oom.group missing");
  REJECT_CONTROL(s.accounting = 0, "memory accounting missing");
  REJECT_CONTROL(s.delegate = 1, "delegation enabled");
  REJECT_CONTROL(s.type[0] = 'z', "Type not exec");
  REJECT_CONTROL(s.kill_mode[0] = 'z', "KillMode not control-group");
  REJECT_CONTROL(s.restart[0] = 'z', "Restart not no");
  REJECT_CONTROL(s.oom_policy[0] = 'z', "OOMPolicy not kill");
  REJECT_CONTROL(copy(s.active, sizeof(s.active), "activating"), "gate not active");
#undef REJECT_CONTROL
  s = original; s.main_pid = 0; s.cg_populated = 0; s.cg_exists = 0; s.cg_removed = 1;
  check(!observer_drain_confirmed(&s), "removed original pin does not imply manager terminal");
  copy(s.active, sizeof(s.active), "failed");
  check(observer_drain_confirmed(&s), "terminal and original pin removed");
  s.cg_removed = 0;
  check(!observer_drain_confirmed(&s), "terminal without original pin evidence is not drained");
  s.cg_exists = 1;
  check(observer_drain_confirmed(&s), "terminal and original pin empty");
  s.cg_populated = 1;
  check(!observer_drain_confirmed(&s), "terminal with descendants remains retained");
}
static void reference_lifecycle(void) {
  ctx worker = {.bus = WORKER_BUS, .referenced = 1, .start_attempted = 1};
  ctx *host = new_owner();
  host->cg = 73;
  call_owner = host; wanted_member = "Ref"; expected_ref_state = OBSERVER_REF_UNCERTAIN;
  new_result = 0; call_result = -ETIMEDOUT;
  check(observer_reference_call(host, 0) == -ETIMEDOUT &&
            host->observer_ref_state == OBSERVER_REF_UNCERTAIN,
        "Ref timeout retains uncertain own reference");
  int before_calls = call_count, before_bus = bus_close_count, before_fd = fd_close_count;
  check(mkg_envelope_observer_unref(host) == -EBUSY, "uncertain Ref cannot be Unref'd blindly");
  check(mkg_envelope_observer_close(host) == -EBUSY, "uncertain Ref cannot close");
  mkg_envelope_close(host);
  check(call_count == before_calls && bus_close_count == before_bus && fd_close_count == before_fd,
        "refusal preserves same bus and pin without manager requests");
  check(host->cg == 73 && host->bus == HOST_BUS && worker.referenced == 1,
        "failed cleanup preserves Host context and Worker AddRef independently");
  check(mkg_envelope_start_absent(host) == -EINVAL &&
            mkg_envelope_kill(host, "", 0, 0) == -EINVAL &&
            mkg_envelope_unref(host, "", 0, 0) == -EINVAL,
        "observer cannot enter original session authority or absence APIs");
  check(mkg_envelope_start(host, NULL, NULL, NULL, NULL, 0, 0) == -EINVAL,
        "observer cannot start through original API");
  check(mkg_envelope_observer_close(&worker) == -EINVAL &&
            mkg_envelope_observer_unref(&worker) == -EINVAL && worker.referenced == 1,
        "observer own-ref methods cannot release Worker reference");
  /* Test teardown only: a controlled manager reconciliation supplies certainty;
   * production has no setter, environment bypass or uncertain-ref close route. */
  host->observer_ref_state = OBSERVER_REF_NONE;
  check(mkg_envelope_observer_close(host) == 0, "unreferenced context can close");

  host = new_owner(); host->cg = 73; call_owner = host;
  wanted_member = "Ref"; expected_ref_state = OBSERVER_REF_UNCERTAIN; call_result = 0;
  check(observer_reference_call(host, 0) == 0 && host->observer_ref_state == OBSERVER_REF_HELD,
        "successful Ref records only Host's own reference");
  check(!host->referenced && !host->start_attempted, "Ref never synthesizes Worker Start state");
  check(mkg_envelope_observer_close(host) == -EBUSY, "held own Ref needs explicit drained Unref");
  wanted_member = "Unref"; expected_ref_state = OBSERVER_UNREF_UNCERTAIN; call_result = -EIO;
  check(observer_reference_call(host, 1) == -EIO && host->observer_ref_state == OBSERVER_UNREF_UNCERTAIN,
        "Unref failure retains uncertainty and context");
  before_calls = call_count;
  check(mkg_envelope_observer_unref(host) == -EBUSY && call_count == before_calls,
        "ambiguous Unref is never duplicated");
  check(mkg_envelope_observer_close(host) == -EBUSY && worker.referenced == 1,
        "ambiguous Unref cannot drop pin or Worker's AddRef");
  host->observer_ref_state = OBSERVER_REF_RELEASED; /* controlled teardown only */
  check(mkg_envelope_observer_close(host) == 0, "known released own ref closes");

  host = new_owner(); call_owner = host; wanted_member = "Ref";
  new_result = -ENOMEM; before_calls = call_count;
  check(observer_reference_call(host, 0) == -ENOMEM &&
            host->observer_ref_state == OBSERVER_REF_NONE && call_count == before_calls,
        "message allocation failure before send has no Ref side effect");
  check(mkg_envelope_observer_close(host) == 0, "pre-send failure may close safely");
  new_result = 0;
}
int main(void) {
  expected_validation();
  owner_and_control_validation();
  reference_lifecycle();
  check(open_count == 3 && pipe_count == 0, "each observer uses a fresh bus and zero pipes");
  check(bus_close_count == 3 && fd_close_count == 2, "only controlled safe contexts close");
  printf("%u controlled native observer assertions passed\n", checks);
  return 0;
}
