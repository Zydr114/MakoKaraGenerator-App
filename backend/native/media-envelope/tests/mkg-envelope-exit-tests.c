/* Deterministic syscall-boundary tests of the actual production functions.
 * No service, real /proc entry, cgroup, subprocess or native helper is used. */
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

static int controlled_open(const char *path, int flags, ...);
static int controlled_openat(int dir, const char *path, int flags, ...);
static ssize_t controlled_read(int fd, void *buffer, size_t count);
static ssize_t controlled_pread(int fd, void *buffer, size_t count, off_t offset);
static ssize_t controlled_write(int fd, const void *buffer, size_t count);
static int controlled_close(int fd);

/* Only this translation unit substitutes these syscall names. The production
 * target keeps the normal syscalls and has no test flag or environment hook. */
#define open controlled_open
#define openat controlled_openat
#define read controlled_read
#define pread controlled_pread
#define write controlled_write
#define close controlled_close
#include "../mkg-envelope-bridge.c"
#undef open
#undef openat
#undef read
#undef pread
#undef write
#undef close

enum io_kind { IO_OPEN, IO_OPENAT, IO_READ, IO_PREAD, IO_WRITE, IO_CLOSE };
typedef struct {
  enum io_kind kind;
  const char *path;
  int fd, flags;
  ssize_t result;
  int error;
  const char *bytes;
} io_step;

static io_step steps[16];
static size_t step_count, step_index;
static const char *case_name;
static int case_errno;
static unsigned int completed_cases;

static void check(int condition, const char *detail) {
  if (!condition) {
    fprintf(stderr, "exit-test-failed: %s errno=%d step=%zu/%zu: %s\n",
            case_name, case_errno, step_index, step_count, detail);
    exit(1);
  }
}
static void begin_case(const char *name, int error) {
  case_name = name;
  case_errno = error;
  step_count = step_index = 0;
}
static void push(io_step step) {
  check(step_count < sizeof(steps) / sizeof(steps[0]), "script capacity");
  steps[step_count++] = step;
}
static io_step *take(enum io_kind kind) {
  check(step_index < step_count, "unexpected syscall");
  io_step *step = &steps[step_index++];
  check(step->kind == kind, "syscall order");
  return step;
}
static void finish_case(void) {
  check(step_index == step_count, "all scripted calls were consumed");
  completed_cases++;
}
static void expect_open(const char *path, int result, int error) {
  push((io_step){.kind = IO_OPEN, .path = path,
                 .flags = O_RDONLY | O_CLOEXEC | O_NOFOLLOW,
                 .result = result, .error = error});
}
static void expect_openat(int dir, const char *path, int flags, int result,
                          int error) {
  push((io_step){.kind = IO_OPENAT, .path = path, .fd = dir, .flags = flags,
                 .result = result, .error = error});
}
static void expect_read(enum io_kind kind, int fd, const char *bytes,
                        ssize_t result, int error) {
  push((io_step){.kind = kind, .fd = fd, .result = result, .error = error,
                 .bytes = bytes});
}
static void expect_close(int fd) {
  /* Poison errno even on successful close: negative read/write errno must have
   * been saved before this subsequent syscall. */
  push((io_step){.kind = IO_CLOSE, .fd = fd, .result = 0, .error = ENOTTY});
}
static int controlled_open(const char *path, int flags, ...) {
  io_step *step = take(IO_OPEN);
  check(!strcmp(path, step->path), "exact proc path");
  check(flags == step->flags, "proc flags");
  errno = step->error;
  return (int)step->result;
}
static int controlled_openat(int dir, const char *path, int flags, ...) {
  io_step *step = take(IO_OPENAT);
  check(dir == step->fd, "same pinned directory FD");
  check(!strcmp(path, step->path), "exact pinned control name");
  check(flags == step->flags, "pinned control flags");
  errno = step->error;
  return (int)step->result;
}
static ssize_t read_result(enum io_kind kind, int fd, void *buffer,
                            size_t count) {
  io_step *step = take(kind);
  check(fd == step->fd, "read descriptor");
  if (step->result > 0) {
    check(step->bytes != NULL, "read backing");
    check((size_t)step->result <= count, "read capacity");
    memcpy(buffer, step->bytes, (size_t)step->result);
  }
  errno = step->error;
  return step->result;
}
static ssize_t controlled_read(int fd, void *buffer, size_t count) {
  return read_result(IO_READ, fd, buffer, count);
}
static ssize_t controlled_pread(int fd, void *buffer, size_t count, off_t offset) {
  check(offset == 0, "control read starts at zero");
  return read_result(IO_PREAD, fd, buffer, count);
}
static ssize_t controlled_write(int fd, const void *buffer, size_t count) {
  io_step *step = take(IO_WRITE);
  check(fd == step->fd, "kill descriptor");
  check(count == 2 && !memcmp(buffer, "1\n", 2), "exact cgroup kill payload");
  errno = step->error;
  return step->result;
}
static int controlled_close(int fd) {
  io_step *step = take(IO_CLOSE);
  check(fd == step->fd, "close descriptor");
  errno = step->error;
  return (int)step->result;
}

#define PROC_PID 437
#define STAT_FD 11
#define PROC_GROUP_FD 12
#define PINNED_CGROUP_FD 73
#define KILL_FD 81
#define EVENTS_FD 82
#define READ_FLAGS (O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
#define KILL_FLAGS (O_WRONLY | O_CLOEXEC | O_NOFOLLOW)
static const char stat_path[] = "/proc/437/stat";
static const char group_path[] = "/proc/437/cgroup";
static const char owned_group[] = "/owned/mako-media-test.service";
static const char proc_stat[] =
    "437 (controlled helper) S 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 "
    "98765 23\n";
static const char proc_group[] = "0::/owned/mako-media-test.service\n";

static void expect_stat(void) {
  expect_open(stat_path, STAT_FD, 0);
  expect_read(IO_READ, STAT_FD, proc_stat, (ssize_t)strlen(proc_stat), 0);
  expect_close(STAT_FD);
}
static void assert_proc(int expected_result, uint64_t expected_ticks,
                         int expected_member) {
  uint64_t ticks = 123;
  int member = 1;
  int result = proc_identity(PROC_PID, owned_group, &ticks, &member);
  check(result == expected_result, "proc result preserves exact negative errno");
  check(ticks == expected_ticks, "ticks are atomic with complete proc sample");
  check(member == expected_member, "proc membership");
  finish_case();
}
static void test_proc_exit_and_errors(void) {
  static const int errors[] = {EACCES, EPERM, EIO, ESRCH, EINTR, EBADF, ENOENT};
  begin_case("exit between stat and proc cgroup open", ENOENT);
  expect_stat();
  expect_open(group_path, -1, ENOENT);
  assert_proc(0, 0, 0);

  begin_case("stat already gone", ENOENT);
  expect_open(stat_path, -1, ENOENT);
  assert_proc(0, 0, 0);

  for (size_t i = 0; i < sizeof(errors) / sizeof(errors[0]); i++) {
    int error = errors[i];
    if (error != ENOENT) {
      begin_case("stat open error is not gone", error);
      expect_open(stat_path, -1, error);
      assert_proc(-error, 0, 0);

      begin_case("proc cgroup open error is not gone", error);
      expect_stat();
      expect_open(group_path, -1, error);
      assert_proc(-error, 0, 0);
    }

    begin_case("stat read error retained across close", error);
    expect_open(stat_path, STAT_FD, 0);
    expect_read(IO_READ, STAT_FD, NULL, -1, error);
    expect_close(STAT_FD);
    assert_proc(-error, 0, 0);

    begin_case("proc cgroup read error retained across close", error);
    expect_stat();
    expect_open(group_path, PROC_GROUP_FD, 0);
    expect_read(IO_READ, PROC_GROUP_FD, NULL, -1, error);
    expect_close(PROC_GROUP_FD);
    assert_proc(-error, 0, 0);
  }

  begin_case("empty stat is an I/O error", 0);
  expect_open(stat_path, STAT_FD, 0);
  expect_read(IO_READ, STAT_FD, NULL, 0, 0);
  expect_close(STAT_FD);
  assert_proc(-EIO, 0, 0);

  begin_case("malformed stat remains an error", 0);
  expect_open(stat_path, STAT_FD, 0);
  expect_read(IO_READ, STAT_FD, "malformed", 9, 0);
  expect_close(STAT_FD);
  assert_proc(-EPROTO, 0, 0);

  begin_case("stat ending right at closing paren rejects without OOB read", 0);
  expect_open(stat_path, STAT_FD, 0);
  expect_read(IO_READ, STAT_FD, "437 (cmd)", 9, 0);
  expect_close(STAT_FD);
  assert_proc(-EPROTO, 0, 0);

  begin_case("stat ending right after space rejects without OOB read", 0);
  expect_open(stat_path, STAT_FD, 0);
  expect_read(IO_READ, STAT_FD, "437 (cmd) ", 10, 0);
  expect_close(STAT_FD);
  assert_proc(-EPROTO, 0, 0);

  begin_case("stat with non-space after closing paren rejects", 0);
  expect_open(stat_path, STAT_FD, 0);
  static const char bad_delim_stat[] =
      "437 (cmd)X S 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 98765 23\n";
  expect_read(IO_READ, STAT_FD, bad_delim_stat,
              (ssize_t)strlen(bad_delim_stat), 0);
  expect_close(STAT_FD);
  assert_proc(-EPROTO, 0, 0);

  begin_case("empty proc cgroup is an I/O error", 0);
  expect_stat();
  expect_open(group_path, PROC_GROUP_FD, 0);
  expect_read(IO_READ, PROC_GROUP_FD, NULL, 0, 0);
  expect_close(PROC_GROUP_FD);
  assert_proc(-EIO, 0, 0);

  begin_case("complete live owner sample", 0);
  expect_stat();
  expect_open(group_path, PROC_GROUP_FD, 0);
  expect_read(IO_READ, PROC_GROUP_FD, proc_group, (ssize_t)strlen(proc_group), 0);
  expect_close(PROC_GROUP_FD);
  assert_proc(0, 98765, 1);

  begin_case("live membership mismatch is not gone", 0);
  expect_stat();
  expect_open(group_path, PROC_GROUP_FD, 0);
  static const char other_group[] = "0::/different/group\n";
  expect_read(IO_READ, PROC_GROUP_FD, other_group,
              (ssize_t)strlen(other_group), 0);
  expect_close(PROC_GROUP_FD);
  assert_proc(0, 98765, 0);
}

static void expect_kill_open_error(int error) {
  expect_openat(PINNED_CGROUP_FD, "cgroup.kill", KILL_FLAGS, -1, error);
}
static void expect_events(const char *bytes) {
  expect_openat(PINNED_CGROUP_FD, "cgroup.events", READ_FLAGS, EVENTS_FD, 0);
  expect_read(IO_PREAD, EVENTS_FD, bytes, (ssize_t)strlen(bytes), 0);
  expect_close(EVENTS_FD);
}
static void assert_kill(int initial_removed, int expected_result,
                         int expected_removed) {
  ctx owner = {.cg = PINNED_CGROUP_FD, .removed = initial_removed};
  int result = kill_pinned_cgroup(&owner);
  check(result == expected_result, "kill result preserves exact negative errno");
  check(owner.cg == PINNED_CGROUP_FD, "kill retains original pinned FD");
  check(owner.removed == expected_removed, "removed requires current pinned absence");
  finish_case();
}
static void test_pinned_kill_exit_and_errors(void) {
  static const int errors[] = {EACCES, EPERM, EIO, ESRCH, EINTR, EBADF, ENOENT};
  begin_case("kill control disappeared with pinned cgroup", ENOENT);
  expect_kill_open_error(ENOENT);
  expect_openat(PINNED_CGROUP_FD, "cgroup.events", READ_FLAGS, -1, ENOENT);
  assert_kill(0, 0, 1);

  begin_case("previous removed flag still rechecks pinned absence", ENOENT);
  expect_kill_open_error(ENOENT);
  expect_openat(PINNED_CGROUP_FD, "cgroup.events", READ_FLAGS, -1, ENOENT);
  assert_kill(1, 0, 1);

  static const char *const existing[] = {"populated 1\n", "populated 0\n", ""};
  for (size_t i = 0; i < sizeof(existing) / sizeof(existing[0]); i++) {
    begin_case("missing kill with existing controls remains error", ENOENT);
    expect_kill_open_error(ENOENT);
    expect_events(existing[i]);
    assert_kill(0, -ENOENT, 0);
  }
  begin_case("cached removed cannot excuse readable pinned controls", ENOENT);
  expect_kill_open_error(ENOENT);
  expect_events("populated 1\n");
  assert_kill(1, -ENOENT, 1);

  for (size_t i = 0; i < sizeof(errors) / sizeof(errors[0]); i++) {
    int error = errors[i];
    if (error != ENOENT) {
      begin_case("kill open error cannot become successful no-op", error);
      expect_kill_open_error(error);
      assert_kill(0, -error, 0);

      begin_case("pinned absence probe open error is retained", error);
      expect_kill_open_error(ENOENT);
      expect_openat(PINNED_CGROUP_FD, "cgroup.events", READ_FLAGS, -1, error);
      assert_kill(0, -error, 0);
    }

    begin_case("pinned absence probe read error is retained", error);
    expect_kill_open_error(ENOENT);
    expect_openat(PINNED_CGROUP_FD, "cgroup.events", READ_FLAGS, EVENTS_FD, 0);
    expect_read(IO_PREAD, EVENTS_FD, NULL, -1, error);
    expect_close(EVENTS_FD);
    /* ENOENT on the already opened events FD is also confirmed absence; every
     * other negative errno remains an error. */
    assert_kill(0, error == ENOENT ? 0 : -error, error == ENOENT ? 1 : 0);

    begin_case("kill write error retained across close", error);
    expect_openat(PINNED_CGROUP_FD, "cgroup.kill", KILL_FLAGS, KILL_FD, 0);
    push((io_step){.kind = IO_WRITE, .fd = KILL_FD, .result = -1,
                   .error = error});
    expect_close(KILL_FD);
    assert_kill(0, -error, 0);
  }

  begin_case("complete pinned kill write", 0);
  expect_openat(PINNED_CGROUP_FD, "cgroup.kill", KILL_FLAGS, KILL_FD, 0);
  push((io_step){.kind = IO_WRITE, .fd = KILL_FD, .result = 2});
  expect_close(KILL_FD);
  assert_kill(0, 0, 0);

  for (int short_count = 0; short_count < 2; short_count++) {
    begin_case("short kill write is never success", 0);
    expect_openat(PINNED_CGROUP_FD, "cgroup.kill", KILL_FLAGS, KILL_FD, 0);
    push((io_step){.kind = IO_WRITE, .fd = KILL_FD, .result = short_count});
    expect_close(KILL_FD);
    assert_kill(0, -EIO, 0);
  }

  begin_case("oversized pinned events remains an error", 0);
  char oversized[4095];
  memset(oversized, 'x', sizeof(oversized));
  expect_kill_open_error(ENOENT);
  expect_openat(PINNED_CGROUP_FD, "cgroup.events", READ_FLAGS, EVENTS_FD, 0);
  expect_read(IO_PREAD, EVENTS_FD, oversized, sizeof(oversized), 0);
  expect_close(EVENTS_FD);
  assert_kill(0, -EOVERFLOW, 0);
}

int main(int argc, char **argv) {
  int proc = argc == 1 || (argc == 2 && !strcmp(argv[1], "proc"));
  int kill = argc == 1 || (argc == 2 && !strcmp(argv[1], "kill"));
  if (!proc && !kill) {
    fprintf(stderr, "usage: %s [proc|kill]\n", argv[0]);
    return 2;
  }
  if (proc)
    test_proc_exit_and_errors();
  if (kill)
    test_pinned_kill_exit_and_errors();
  printf("%u deterministic native exit cases passed\n", completed_cases);
  return 0;
}
