#define _GNU_SOURCE
#include <poll.h>
/* Declare the test double after the real header so its input assertions do not
 * inherit libc's write_only annotation on poll's output buffer. */
int probe_test_poll(struct pollfd *fds, nfds_t count, int timeout);
/* Controlled syscall-boundary tests. No pidfd, socket, bus, process or manager
 * is actually opened, signalled or contacted. Link libsystemd only for the
 * unused witness functions in the included real diagnostics translation unit. */
#define syscall probe_test_syscall
#define poll probe_test_poll
#define getsockopt probe_test_getsockopt
#define close probe_test_close
#define getuid probe_test_getuid
#include "../native/mkg-probe-diagnostics.c"
#undef syscall
#undef poll
#undef getsockopt
#undef close
#undef getuid
#include <assert.h>
#include <stdarg.h>
#include <stdio.h>

static int syscall_result, syscall_errno, syscall_calls;
static long syscall_number;
static uint32_t seen_pid;
static int seen_fd, seen_signal;
static short poll_events;
static int poll_errno, poll_calls, interrupt_once;
static int peer_errno, peer_short;
static uid_t peer_uid;

long probe_test_syscall(long number, ...) {
  va_list args;
  va_start(args, number);
  syscall_number = number;
  syscall_calls++;
  if (number == SYS_pidfd_open) {
    seen_pid = va_arg(args, uint32_t);
    assert(va_arg(args, int) == 0);
  } else if (number == SYS_pidfd_send_signal) {
    seen_fd = va_arg(args, int);
    seen_signal = va_arg(args, int);
    assert(va_arg(args, void *) == NULL);
    assert(va_arg(args, int) == 0);
  } else assert(0 && "unexpected syscall");
  va_end(args);
  errno = syscall_errno;
  return syscall_result;
}
int probe_test_poll(struct pollfd *fds, nfds_t count, int timeout) {
  assert(count == 1 && timeout == 0 && fds[0].events == POLLIN);
  poll_calls++;
  if (interrupt_once) { interrupt_once = 0; errno = EINTR; return -1; }
  if (poll_errno) { errno = poll_errno; return -1; }
  fds[0].revents = poll_events;
  return poll_events ? 1 : 0;
}
int probe_test_getsockopt(int fd, int level, int option, void *data, socklen_t *length) {
  assert(fd == 55 && level == SOL_SOCKET && option == SO_PEERCRED);
  if (peer_errno) { errno = peer_errno; return -1; }
  struct ucred *cred = data;
  memset(cred, 0, sizeof(*cred));
  cred->uid = peer_uid;
  *length = peer_short ? 0 : sizeof(*cred);
  return 0;
}
int probe_test_close(int fd) { assert(fd == 55); return 0; }
uid_t probe_test_getuid(void) { return 1000; }

int main(void) {
  char target[4] = "old";
  assert(copy(target, sizeof(target), "abcd") == -EOVERFLOW);
  assert(!strcmp(target, "old"));
  assert(copy(target, sizeof(target), NULL) == -EOVERFLOW);
  assert(copy(target, sizeof(target), "abc") == 0 && !strcmp(target, "abc"));
  ref_sample sample;
  assert(mkg_probe_witness_sample(NULL, "/unit", "11111111111111111111111111111111", &sample) == -EINVAL);

  syscall_result = 55;
  assert(mkg_probe_pidfd_open(424242) == 55);
  assert(syscall_number == SYS_pidfd_open && seen_pid == 424242);
  syscall_result = -1; syscall_errno = EPERM;
  assert(mkg_probe_pidfd_open(424242) == -EPERM);

  assert(mkg_probe_pidfd_exited(-1) == -EINVAL);
  poll_events = 0; poll_calls = 0; interrupt_once = 1;
  assert(mkg_probe_pidfd_exited(55) == 0 && poll_calls == 2);
  poll_events = POLLNVAL;
  assert(mkg_probe_pidfd_exited(55) == -EIO);
  poll_events = POLLERR;
  assert(mkg_probe_pidfd_exited(55) == -EIO);
  poll_events = POLLIN;
  assert(mkg_probe_pidfd_exited(55) == 1);
  poll_errno = EACCES;
  assert(mkg_probe_pidfd_exited(55) == -EACCES);

  syscall_calls = 0;
  assert(mkg_probe_owned_pidfd_kill(-1) == -EINVAL && syscall_calls == 0);
  syscall_result = 0; syscall_errno = 0;
  assert(mkg_probe_owned_pidfd_kill(55) == 0);
  assert(syscall_number == SYS_pidfd_send_signal && seen_fd == 55 && seen_signal == SIGKILL);
  syscall_result = -1; syscall_errno = ESRCH;
  assert(mkg_probe_owned_pidfd_kill(55) == -ESRCH);

  peer_uid = 1000;
  assert(mkg_probe_peer_is_self_uid(55) == 1);
  peer_uid = 1001;
  assert(mkg_probe_peer_is_self_uid(55) == 0);
  peer_uid = 1000; peer_short = 1;
  assert(mkg_probe_peer_is_self_uid(55) == 0);
  peer_short = 0; peer_errno = EACCES;
  assert(mkg_probe_peer_is_self_uid(55) == -EACCES);
  assert(mkg_probe_close_fd(55) == 0);
  puts("controlled probe diagnostics boundary checks passed");
  return 0;
}
