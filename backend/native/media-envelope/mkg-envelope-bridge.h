#pragma once
#include <stdint.h>

typedef struct {
  char unit[256], path[512], invocation[33], description[128], cgroup[2048];
  char type[32], kill_mode[32], restart[32], oom_policy[32], active[32],
      result[64];
  uint64_t start_ticks, cg_device, cg_inode, memory_max, effective_max,
      swap_max;
  uint64_t kernel_max, kernel_swap, oom_kills;
  uint32_t main_pid;
  int32_t main_in_group, cg_exists, cg_populated, cg_removed, oom_group;
  int32_t accounting, delegate, exec_code, exec_status, kill_available,
      controls_complete;
} mkg_snapshot;

/* Fixed Linux-x64 ABI. Negative errno values only; no native error text is
 * exported. */
int mkg_envelope_abi_size(void);
int mkg_envelope_open(void **out, int parent_fds[4], int64_t *pipe_backing);
int mkg_envelope_start(void *context, const char *nonce, const char *gate,
                       const char *tool, const char *const *arguments,
                       uint32_t count, uint64_t limit);
int mkg_envelope_snapshot(void *context, mkg_snapshot *out);
int mkg_envelope_start_absent(
    void *context); /* same ordered connection: 1 absent, 0 found, <0 unknown */
int mkg_envelope_kill(void *context, const char *invocation, uint64_t device,
                      uint64_t inode);
int mkg_envelope_unref(void *context, const char *invocation, uint64_t device,
                       uint64_t inode);
void mkg_envelope_close(void *context); /* never kills; requires proven drain or
                                           no attempted start */
/* Nonblocking owned descriptor, poll at most timeout_ms; -EAGAIN means retry.
 */
int mkg_pipe_io(int fd, void *buffer, uint32_t count, int writing,
                int timeout_ms);
