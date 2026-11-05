#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* This helper is a bounded acceptance fixture, never a production decoder. */
static const size_t allocation_bytes = 40U * 1024U * 1024U;
static int metadata_fd = -1;
static char control_group[2048];
static uint64_t start_ticks(void);

static int write_all(int fd, const void *buffer, size_t length) {
  const unsigned char *next = buffer;
  while (length) {
    ssize_t written = write(fd, next, length);
    if (written < 0 && errno == EINTR)
      continue;
    if (written <= 0)
      return -1;
    next += (size_t)written;
    length -= (size_t)written;
  }
  return 0;
}
static int record(const char *kind, uint64_t amount) {
  char text[512];
  int count = snprintf(
      text, sizeof(text),
      "{\"kind\":\"%s\",\"pid\":%ld,\"ppid\":%ld,\"startTicks\":%" PRIu64
      ",\"bytes\":%" PRIu64 "}\n",
      kind, (long)getpid(), (long)getppid(), start_ticks(), amount);
  return count <= 0 || (size_t)count >= sizeof(text)
             ? -1
             : write_all(metadata_fd, text, (size_t)count);
}
static int read_small(const char *path, char *buffer, size_t capacity) {
  int fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
  if (fd < 0)
    return -1;
  ssize_t count = read(fd, buffer, capacity - 1);
  int closed = close(fd);
  if (count <= 0 || (size_t)count >= capacity - 1 || closed)
    return -1;
  buffer[count] = 0;
  return 0;
}
static uint64_t start_ticks(void) {
  char text[8192];
  if (read_small("/proc/self/stat", text, sizeof(text)))
    return 0;
  char *end = strrchr(text, ')'), *saved = NULL;
  if (!end)
    return 0;
  char *token = strtok_r(end + 2, " ", &saved);
  for (int i = 0; i < 19 && token; i++)
    token = strtok_r(NULL, " ", &saved);
  return token ? strtoull(token, NULL, 10) : 0;
}
static uint64_t kernel_value(const char *name) {
  char path[2304], text[128];
  int length =
      snprintf(path, sizeof(path), "/sys/fs/cgroup%s/%s", control_group, name);
  if (length <= 0 || (size_t)length >= sizeof(path) ||
      read_small(path, text, sizeof(text)))
    return UINT64_MAX;
  return strtoull(text, NULL, 10);
}
static int hello(const char *mode) {
  char text[4096], line[4096];
  if (read_small("/proc/self/cgroup", text, sizeof(text)) ||
      strncmp(text, "0::/", 4))
    return -1;
  char *newline = strchr(text, '\n');
  if (!newline || newline[1])
    return -1;
  *newline = 0;
  if (strlen(text + 3) >= sizeof(control_group))
    return -1;
  strcpy(control_group, text + 3);
  /* cgroup/argv names are our own generated ASCII, never arbitrary user text.
   */
  if (strchr(control_group, '"') || strchr(control_group, '\\'))
    return -1;
  int count = snprintf(line, sizeof(line),
                       "{\"kind\":\"hello\",\"mode\":\"%s\",\"pid\":%ld,"
                       "\"ppid\":%ld,\"startTicks\":%" PRIu64
                       ",\"controlGroup\":\"%s\",\"memoryMax\":%" PRIu64
                       ",\"swapMax\":%" PRIu64 ",\"oomGroup\":%" PRIu64 "}\n",
                       mode, (long)getpid(), (long)getppid(), start_ticks(),
                       control_group, kernel_value("memory.max"),
                       kernel_value("memory.swap.max"),
                       kernel_value("memory.oom.group"));
  return count <= 0 || (size_t)count >= sizeof(line)
             ? -1
             : write_all(metadata_fd, line, (size_t)count);
}
static int authorize(void) {
  char expected[3];
  size_t count = 0;
  while (count < sizeof(expected)) {
    ssize_t read_count =
        read(STDIN_FILENO, expected + count, sizeof(expected) - count);
    if (read_count < 0 && errno == EINTR)
      continue;
    if (read_count <= 0)
      return -1;
    count += (size_t)read_count;
  }
  return memcmp(expected, "GO\n", sizeof(expected));
}
static void wait_bounded(void) {
  struct timespec delay = {.tv_sec = 1, .tv_nsec = 0};
  for (;;)
    nanosleep(&delay, NULL); /* alarm terminates this fixture after 15s */
}
static void *allocate_and_touch(void) {
  volatile unsigned char *memory =
      mmap(NULL, allocation_bytes, PROT_READ | PROT_WRITE,
           MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
  if (memory == MAP_FAILED)
    return NULL;
  for (size_t i = 0; i < allocation_bytes; i += 4096)
    memory[i] = (unsigned char)(i / 4096 + 1);
  return (void *)memory;
}
static int subtree_oom(void) {
  int barrier[2];
  if (pipe2(barrier, O_CLOEXEC))
    return 121;
  pid_t child = fork();
  if (child < 0) {
    close(barrier[0]);
    close(barrier[1]);
    return 121;
  }
  if (!child) {
    alarm(15);
    close(barrier[1]);
    if (hello("oom-subtree-child") || record("child-ready", 0))
      _exit(122);
    char go;
    if (read(barrier[0], &go, 1) != 1)
      _exit(122);
    close(barrier[0]);
    if (record("child-allocation-begin", allocation_bytes))
      _exit(122);
    void *memory = allocate_and_touch();
    if (!memory)
      _exit(123);
    if (record("child-allocation-complete", allocation_bytes))
      _exit(122);
    wait_bounded();
    _exit(124);
  }
  close(barrier[0]);
  if (record("parent-allocation-begin", allocation_bytes))
    return 122;
  void *memory = allocate_and_touch();
  if (!memory)
    return 123;
  if (record("parent-allocation-complete", allocation_bytes))
    return 122;
  if (write_all(barrier[1], "G", 1))
    return 122;
  close(barrier[1]);
  int status;
  while (waitpid(child, &status, 0) < 0 && errno == EINTR) {
  }
  /* A healthy aggregate envelope kills this parent too. Reaching here is a
   * failed fixture outcome, never manufactured CapacityExceeded evidence. */
  (void)munmap(memory, allocation_bytes);
  return 124;
}
int main(int argc, char **argv) {
  if (argc != 2)
    return 125;
  const char *mode = argv[1];
  if (strcmp(mode, "echo") && strcmp(mode, "nonzero") && strcmp(mode, "wait") &&
      strcmp(mode, "stall") && strcmp(mode, "oom-subtree"))
    return 125;
  const char *fd = getenv("MKG_MEDIA_METADATA_FD");
  if (!fd || strcmp(fd, "3") || fcntl(3, F_GETFD) < 0)
    return 125;
  metadata_fd = 3;
  if (signal(SIGALRM, SIG_DFL) == SIG_ERR)
    return 125;
  alarm(15); /* Exact owned fixture stops even if its parent probe fails. */
  if (hello(mode) || authorize())
    return 126;
  if (write_all(STDERR_FILENO, "probe-diagnostic\n", 17))
    return 126;
  if (!strcmp(mode, "oom-subtree"))
    return subtree_oom();
  if (!strcmp(mode, "wait") || !strcmp(mode, "stall")) {
    if (record("waiting", 0))
      return 126;
    wait_bounded();
  }
  if (write_all(STDOUT_FILENO, "MKGPROBE-OUT\n", 13) || record("complete", 0))
    return 126;
  return !strcmp(mode, "nonzero") ? 17 : 0;
}
