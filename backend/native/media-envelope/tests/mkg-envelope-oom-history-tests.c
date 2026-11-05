/* Calls the actual production memory.events observer with deterministic I/O.
 * This does not start a service or allocate memory pressure to cause real OOM. */
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

static int controlled_openat(int dir, const char *path, int flags, ...);
static ssize_t controlled_pread(int fd, void *buffer, size_t count, off_t offset);
static int controlled_close(int fd);
#define openat controlled_openat
#define pread controlled_pread
#define close controlled_close
#include "../mkg-envelope-bridge.c"
#undef openat
#undef pread
#undef close

enum io_kind { IO_OPENAT, IO_PREAD, IO_CLOSE };
typedef struct {
  enum io_kind kind;
  ssize_t result;
  int error;
  const char *bytes;
} io_step;
static io_step steps[3];
static size_t step_count, step_index;
static const char *case_name;
static int case_errno;
static unsigned int completed_cases;

#define PINNED_FD 73
#define EVENTS_FD 81

static void check(int condition, const char *detail) {
  if (!condition) {
    fprintf(stderr, "oom-history-test-failed: %s errno=%d step=%zu/%zu: %s\n",
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
  check(step_count < sizeof(steps) / sizeof(steps[0]), "I/O script capacity");
  steps[step_count++] = step;
}
static io_step *take(enum io_kind kind) {
  check(step_index < step_count, "unexpected syscall");
  io_step *step = &steps[step_index++];
  check(step->kind == kind, "exact syscall order");
  return step;
}
static void expect_open_error(int error) {
  push((io_step){.kind = IO_OPENAT, .result = -1, .error = error});
}
static void expect_read(const char *bytes, ssize_t result, int error) {
  push((io_step){.kind = IO_OPENAT, .result = EVENTS_FD});
  push((io_step){.kind = IO_PREAD, .result = result, .error = error, .bytes = bytes});
  /* This deliberately changes errno after a failed read. */
  push((io_step){.kind = IO_CLOSE, .result = 0, .error = ENOTTY});
}
static void expect_text(const char *bytes) {
  expect_read(bytes, (ssize_t)strlen(bytes), 0);
}
static int controlled_openat(int dir, const char *path, int flags, ...) {
  io_step *step = take(IO_OPENAT);
  check(dir == PINNED_FD, "same pinned directory FD");
  check(!strcmp(path, "memory.events"), "exact counter name");
  check(flags == (O_RDONLY | O_CLOEXEC | O_NOFOLLOW), "counter read flags");
  errno = step->error;
  return (int)step->result;
}
static ssize_t controlled_pread(int fd, void *buffer, size_t count, off_t offset) {
  io_step *step = take(IO_PREAD);
  check(fd == EVENTS_FD && offset == 0, "exact counter read descriptor/offset");
  if (step->result > 0) {
    check(step->bytes != NULL && (size_t)step->result <= count, "counter read backing");
    memcpy(buffer, step->bytes, (size_t)step->result);
  }
  errno = step->error;
  return step->result;
}
static int controlled_close(int fd) {
  io_step *step = take(IO_CLOSE);
  check(fd == EVENTS_FD, "counter descriptor close");
  errno = step->error;
  return (int)step->result;
}
static ctx owner(uint64_t history) {
  ctx c = {.cg = PINNED_FD, .device = 28, .inode = 916,
           .captured_pid = 437, .captured_ticks = 98765, .last_oom = history,
           .referenced = 1, .start_attempted = 1};
  check(copy(c.group, sizeof(c.group), "/owned/mako-media-test.service") == 0,
        "owned cgroup");
  check(copy(c.nonce, sizeof(c.nonce), "0123456789abcdef0123456789abcdef") == 0,
        "owned nonce");
  return c;
}
static mkg_snapshot sample(void) {
  mkg_snapshot s = {.main_pid = 437, .start_ticks = 98765,
                    .cg_device = 28, .cg_inode = 916, .main_in_group = 1,
                    .cg_exists = 1, .cg_populated = 1, .controls_complete = 1,
                    .memory_max = 67108864, .effective_max = 67108864};
  check(copy(s.active, sizeof(s.active), "active") == 0, "original manager state");
  check(copy(s.invocation, sizeof(s.invocation),
             "11111111111111111111111111111111") == 0, "original invocation");
  check(copy(s.cgroup, sizeof(s.cgroup), "/owned/mako-media-test.service") == 0,
        "original snapshot group");
  return s;
}
static void assert_sample(ctx *c, int expected_result, uint64_t expected_count) {
  ctx expected_owner = *c;
  expected_owner.last_oom = expected_count;
  mkg_snapshot s = sample(), expected = s;
  expected.oom_kills = expected_count;
  char scratch[4096];
  int result = observe_oom_history(c, &s, scratch, sizeof(scratch));
  check(result == expected_result, "original I/O status preserved");
  check(c->last_oom == expected_count && s.oom_kills == expected_count,
        "history and published counter remain monotone");
#define OWNER_SAME(field) \
  check(c->field == expected_owner.field, "owner field unchanged: " #field)
  OWNER_SAME(bus); OWNER_SAME(cg); OWNER_SAME(device); OWNER_SAME(inode);
  OWNER_SAME(referenced); OWNER_SAME(start_attempted); OWNER_SAME(removed);
  OWNER_SAME(start_rejected); OWNER_SAME(captured_pid); OWNER_SAME(captured_ticks);
#undef OWNER_SAME
  check(!memcmp(c->child, expected_owner.child, sizeof(c->child)), "owned pipe FDs unchanged");
  check(!memcmp(c->unit, expected_owner.unit, sizeof(c->unit)), "unit unchanged");
  check(!memcmp(c->path, expected_owner.path, sizeof(c->path)), "object path unchanged");
  check(!memcmp(c->group, expected_owner.group, sizeof(c->group)), "pin path unchanged");
  check(!memcmp(c->nonce, expected_owner.nonce, sizeof(c->nonce)), "nonce unchanged");
#define SAMPLE_TEXT(field) \
  check(!memcmp(s.field, expected.field, sizeof(s.field)), "sample text unchanged: " #field)
  SAMPLE_TEXT(unit); SAMPLE_TEXT(path); SAMPLE_TEXT(invocation); SAMPLE_TEXT(description);
  SAMPLE_TEXT(cgroup); SAMPLE_TEXT(type); SAMPLE_TEXT(kill_mode); SAMPLE_TEXT(restart);
  SAMPLE_TEXT(oom_policy); SAMPLE_TEXT(active); SAMPLE_TEXT(result);
#undef SAMPLE_TEXT
#define SAMPLE_SAME(field) \
  check(s.field == expected.field, "sample field unchanged: " #field)
  SAMPLE_SAME(start_ticks); SAMPLE_SAME(cg_device); SAMPLE_SAME(cg_inode);
  SAMPLE_SAME(memory_max); SAMPLE_SAME(effective_max); SAMPLE_SAME(swap_max);
  SAMPLE_SAME(kernel_max); SAMPLE_SAME(kernel_swap); SAMPLE_SAME(main_pid);
  SAMPLE_SAME(main_in_group); SAMPLE_SAME(cg_exists); SAMPLE_SAME(cg_populated);
  SAMPLE_SAME(cg_removed); SAMPLE_SAME(oom_group); SAMPLE_SAME(accounting);
  SAMPLE_SAME(delegate); SAMPLE_SAME(exec_code); SAMPLE_SAME(exec_status);
  SAMPLE_SAME(kill_available); SAMPLE_SAME(controls_complete);
#undef SAMPLE_SAME
  check(step_index == step_count, "all expected calls consumed");
  completed_cases++;
}
static void test_known_history_survives_unavailable_counter(void) {
  static const int errors[] = {ENOENT, EACCES, EPERM, EIO, EINTR, EBADF, ESRCH};
  for (size_t i = 0; i < sizeof(errors) / sizeof(errors[0]); i++) {
    ctx c = owner(7);
    begin_case("known OOM history survives counter open error", errors[i]);
    expect_open_error(errors[i]);
    assert_sample(&c, -errors[i], 7);

    c = owner(7);
    begin_case("known OOM history survives counter read error", errors[i]);
    expect_read(NULL, -1, errors[i]);
    assert_sample(&c, -errors[i], 7);
  }
}
static void test_readable_samples_cannot_regress_history(void) {
  static const struct {
    const char *text;
    uint64_t expected;
  } cases[] = {
      {"oom_kill 12\n", 12}, {"oom_kill 7\n", 7}, {"oom_kill 3\n", 7},
      {"oom_kill 0\n", 7}, {"low 0\nhigh 0\noom 0\n", 7}, {"", 7},
      {"oom_kill invalid\n", 7}, {"oom_kill 18446744073709551615\n", UINT64_MAX}};
  for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); i++) {
    ctx c = owner(7);
    begin_case("readable sample never decreases observed history", 0);
    expect_text(cases[i].text);
    assert_sample(&c, 0, cases[i].expected);
  }
  ctx c = owner(UINT64_MAX);
  begin_case("maximum history never wraps or regresses", 0);
  expect_text("oom_kill 0\n");
  assert_sample(&c, 0, UINT64_MAX);
}
static void test_sequence_and_no_invented_first_oom(void) {
  ctx c = owner(0);
  begin_case("no first OOM is invented on unreadable counter", EACCES);
  expect_open_error(EACCES);
  assert_sample(&c, -EACCES, 0);

  begin_case("first observed OOM is remembered", 0);
  expect_text("low 0\noom 2\noom_kill 2\noom_group_kill 0\n");
  assert_sample(&c, 0, 2);

  begin_case("next snapshot counter missing retains observed OOM", ENOENT);
  expect_open_error(ENOENT);
  assert_sample(&c, -ENOENT, 2);

  begin_case("next snapshot lower sample retains observed OOM", 0);
  expect_text("oom_kill 1\n");
  assert_sample(&c, 0, 2);

  begin_case("next snapshot missing field retains observed OOM", 0);
  expect_text("oom 0\n");
  assert_sample(&c, 0, 2);

  begin_case("later greater sample advances same owner history", 0);
  expect_text("oom_kill 5\n");
  assert_sample(&c, 0, 5);

  begin_case("overflowed read retains history and original failure", 0);
  char oversized[4095];
  memset(oversized, 'x', sizeof(oversized));
  expect_read(oversized, sizeof(oversized), 0);
  assert_sample(&c, -EOVERFLOW, 5);
}
int main(void) {
  test_known_history_survives_unavailable_counter();
  test_readable_samples_cannot_regress_history();
  test_sequence_and_no_invented_first_oom();
  printf("%u deterministic OOM history cases passed\n", completed_cases);
  return 0;
}
