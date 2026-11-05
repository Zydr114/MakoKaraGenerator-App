/* Runs only the actual pure group-selection step from the production bridge.
 * No manager, /proc, cgroup, helper, subprocess or syscall injection is used. */
#include "../mkg-envelope-bridge.c"

static const char owned_group[] =
    "/owned/mako-media-0123456789abcdef0123456789abcdef.service";
static const char other_group[] =
    "/owned/mako-media-fedcba9876543210fedcba9876543210.service";
static const char *case_name;
static unsigned int completed_cases;

static void check(int condition, const char *detail) {
  if (!condition) {
    fprintf(stderr, "snapshot-test-failed: %s: %s\n", case_name, detail);
    exit(1);
  }
}
static ctx owner(int pinned) {
  ctx c = {.cg = pinned ? 73 : -1, .device = 28, .inode = 916,
           .captured_pid = 437, .captured_ticks = 98765};
  check(copy(c.group, sizeof(c.group), owned_group) == 0, "owner group");
  return c;
}
static mkg_snapshot sample(const char *active, uint32_t pid) {
  mkg_snapshot s = {.main_pid = pid, .start_ticks = pid ? 98765 : 0,
                    .cg_device = 28, .cg_inode = 916,
                    .main_in_group = pid != 0,
                    .cg_exists = 0, .cg_populated = 0, .cg_removed = 0,
                    .memory_max = 67108864, .effective_max = 67108864,
                    .controls_complete = 0, .exec_code = 0, .exec_status = 0};
  check(copy(s.active, sizeof(s.active), active) == 0, "active sample");
  check(copy(s.invocation, sizeof(s.invocation),
             "11111111111111111111111111111111") == 0, "invocation sample");
  check(copy(s.description, sizeof(s.description), "original description") == 0,
        "nonce description sample");
  check(copy(s.result, sizeof(s.result), "success") == 0, "original result");
  return s;
}
static int terminal(const char *active) {
  return !strcmp(active, "inactive") || !strcmp(active, "failed");
}
static void assert_decision(ctx *c, mkg_snapshot *s, int expected_result,
                            const ctx *expected_owner,
                            const mkg_snapshot *expected_snapshot) {
  int result = select_snapshot_group(c, s, terminal(s->active));
  check(result == expected_result, "exact group decision");
  /* Compare named fields rather than struct padding. The selector is not an
   * ownership or drain grant: all samples other than group stay untouched. */
#define SAME_OWNER(field) \
  check(c->field == expected_owner->field, "owner field: " #field)
  SAME_OWNER(bus); SAME_OWNER(cg); SAME_OWNER(device); SAME_OWNER(inode);
  SAME_OWNER(last_oom); SAME_OWNER(referenced); SAME_OWNER(start_attempted);
  SAME_OWNER(removed); SAME_OWNER(start_rejected); SAME_OWNER(captured_pid);
  SAME_OWNER(captured_ticks);
#undef SAME_OWNER
  check(!memcmp(c->child, expected_owner->child, sizeof(c->child)), "owned child FDs");
  check(!memcmp(c->unit, expected_owner->unit, sizeof(c->unit)), "owner unit");
  check(!memcmp(c->path, expected_owner->path, sizeof(c->path)), "owner object path");
  check(!memcmp(c->nonce, expected_owner->nonce, sizeof(c->nonce)), "owner nonce");
  check(!memcmp(c->group, expected_owner->group, sizeof(c->group)), "owner pinned group");
#define SAME_TEXT(field) \
  check(!memcmp(s->field, expected_snapshot->field, sizeof(s->field)), \
        "sample text: " #field)
  SAME_TEXT(unit); SAME_TEXT(path); SAME_TEXT(invocation); SAME_TEXT(description);
  SAME_TEXT(cgroup); SAME_TEXT(type); SAME_TEXT(kill_mode); SAME_TEXT(restart);
  SAME_TEXT(oom_policy); SAME_TEXT(active); SAME_TEXT(result);
#undef SAME_TEXT
#define SAME_SAMPLE(field) \
  check(s->field == expected_snapshot->field, "sample field: " #field)
  SAME_SAMPLE(start_ticks); SAME_SAMPLE(cg_device); SAME_SAMPLE(cg_inode);
  SAME_SAMPLE(memory_max); SAME_SAMPLE(effective_max); SAME_SAMPLE(swap_max);
  SAME_SAMPLE(kernel_max); SAME_SAMPLE(kernel_swap); SAME_SAMPLE(oom_kills);
  SAME_SAMPLE(main_pid); SAME_SAMPLE(main_in_group); SAME_SAMPLE(cg_exists);
  SAME_SAMPLE(cg_populated); SAME_SAMPLE(cg_removed); SAME_SAMPLE(oom_group);
  SAME_SAMPLE(accounting); SAME_SAMPLE(delegate); SAME_SAMPLE(exec_code);
  SAME_SAMPLE(exec_status); SAME_SAMPLE(kill_available); SAME_SAMPLE(controls_complete);
#undef SAME_SAMPLE
  completed_cases++;
}
static void test_pinned_group_retention(void) {
  static const char *const states[] = {
      "active", "activating", "deactivating", "reloading", "inactive", "failed"};
  static const uint32_t pids[] = {0, 437};
  for (size_t state = 0; state < sizeof(states) / sizeof(states[0]); state++) {
    for (size_t pid = 0; pid < sizeof(pids) / sizeof(pids[0]); pid++) {
      case_name = "pinned empty group after manager state sample";
      ctx c = owner(1), expected_owner = c;
      mkg_snapshot s = sample(states[state], pids[pid]), expected = s;
      check(copy(expected.cgroup, sizeof(expected.cgroup), owned_group) == 0,
            "expected retained group");
      assert_decision(&c, &s, 0, &expected_owner, &expected);

      case_name = "observed group equals original pin";
      c = owner(1);
      expected_owner = c;
      s = sample(states[state], pids[pid]);
      check(copy(s.cgroup, sizeof(s.cgroup), owned_group) == 0, "observed group");
      expected = s;
      assert_decision(&c, &s, 0, &expected_owner, &expected);

      case_name = "replacement nonempty group never overwrites pinned path";
      c = owner(1);
      expected_owner = c;
      s = sample(states[state], pids[pid]);
      check(copy(s.cgroup, sizeof(s.cgroup), other_group) == 0,
            "replacement observed group");
      expected = s;
      assert_decision(&c, &s, -ESTALE, &expected_owner, &expected);
    }
  }
}
static void test_initial_unpinned_start_is_not_relaxed(void) {
  static const char *const states[] = {
      "active", "activating", "deactivating", "inactive", "failed"};
  static const uint32_t pids[] = {0, 437};
  for (size_t state = 0; state < sizeof(states) / sizeof(states[0]); state++) {
    for (size_t pid = 0; pid < sizeof(pids) / sizeof(pids[0]); pid++) {
      case_name = "unpinned empty group keeps original failed-start rule";
      ctx c = owner(0), expected_owner = c;
      c.group[0] = expected_owner.group[0] = 0;
      mkg_snapshot s = sample(states[state], pids[pid]), expected = s;
      int absent = terminal(states[state]) && !pids[pid];
      if (absent)
        expected.cg_removed = 1;
      assert_decision(&c, &s, absent ? 1 : -EPROTO, &expected_owner, &expected);

      case_name = "unpinned nonempty sample still requires later pin and controls";
      c = owner(0);
      c.group[0] = 0;
      expected_owner = c;
      check(copy(expected_owner.group, sizeof(expected_owner.group),
                 owned_group) == 0, "expected initial group");
      s = sample(states[state], pids[pid]);
      check(copy(s.cgroup, sizeof(s.cgroup), owned_group) == 0,
            "initial observed group");
      expected = s;
      assert_decision(&c, &s, 0, &expected_owner, &expected);
    }
  }
}
static void test_invalid_pin_never_invents_group(void) {
  case_name = "invalid existing pin with empty remembered group is rejected";
  ctx c = owner(1);
  c.group[0] = 0;
  ctx expected_owner = c;
  mkg_snapshot s = sample("inactive", 0), expected = s;
  assert_decision(&c, &s, -ESTALE, &expected_owner, &expected);

  case_name = "invalid existing pin cannot adopt a new manager group";
  c = owner(1);
  c.group[0] = 0;
  expected_owner = c;
  s = sample("active", 437);
  check(copy(s.cgroup, sizeof(s.cgroup), owned_group) == 0,
        "manager group does not repair invalid pin");
  expected = s;
  assert_decision(&c, &s, -ESTALE, &expected_owner, &expected);
}

int main(void) {
  test_pinned_group_retention();
  test_initial_unpinned_start_is_not_relaxed();
  test_invalid_pin_never_invents_group();
  printf("%u deterministic snapshot group cases passed\n", completed_cases);
  return 0;
}
