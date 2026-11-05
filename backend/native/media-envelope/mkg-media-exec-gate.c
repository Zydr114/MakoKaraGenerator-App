/* Same-cgroup/same-PID gate. Never forks or starts a shell. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <unistd.h>

int main(int argc, char **argv) {
  /* argv: gate -- tool absolute-path arguments... ; systemd passes one fd3. */
  if (argc < 3 || strcmp(argv[1], "--") || argv[2][0] != '/')
    return 125;
  char *fds = getenv("LISTEN_FDS"), *pid = getenv("LISTEN_PID"),
       *names = getenv("LISTEN_FDNAMES");
  if (!fds || strcmp(fds, "1") || !pid || strtol(pid, NULL, 10) != getpid() ||
      !names || strcmp(names, "mkg-metadata"))
    return 125;
  if (fcntl(3, F_GETFD) < 0 || fcntl(3, F_SETFD, 0) < 0)
    return 125;
  if (setenv("MKG_MEDIA_METADATA_FD", "3", 1) || unsetenv("LISTEN_FDS") ||
      unsetenv("LISTEN_PID") || unsetenv("LISTEN_FDNAMES"))
    return 125;
  if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0))
    return 125;
  unsigned char gate;
  ssize_t n;
  do {
    n = read(STDIN_FILENO, &gate, 1);
  } while (n < 0 && errno == EINTR);
  if (n != 1 || gate != 0xa7)
    return 125; /* parent death/abort -> EOF, no decoder exec */
  execv(argv[2], &argv[2]);
  return errno == ENOMEM
             ? 124
             : 126; /* safe exit code; do not echo paths/arguments */
}
