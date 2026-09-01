// landlock-exec: confines a spawned command with Linux Landlock (unprivileged, kernel 5.13+).
//   landlock-exec <mode> <allow-root> -- <command> [args...]
//   mode: workspace-write  → writes only under <allow-root>; reads/executes anywhere
//         read-only        → no writes anywhere
// Fails closed: any setup error prints "[sandbox: setup failed: ...]" to stderr and exits 126.
#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <stdint.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/prctl.h>
#include <sys/syscall.h>

#ifndef LANDLOCK_ACCESS_FS_WRITE_FILE
#define LANDLOCK_ACCESS_FS_WRITE_FILE   (1ULL << 1)
#define LANDLOCK_ACCESS_FS_READ_FILE    (1ULL << 2)
#define LANDLOCK_ACCESS_FS_READ_DIR     (1ULL << 3)
#define LANDLOCK_ACCESS_FS_REMOVE_DIR   (1ULL << 4)
#define LANDLOCK_ACCESS_FS_REMOVE_FILE  (1ULL << 5)
#define LANDLOCK_ACCESS_FS_MAKE_CHAR    (1ULL << 6)
#define LANDLOCK_ACCESS_FS_MAKE_DIR     (1ULL << 7)
#define LANDLOCK_ACCESS_FS_MAKE_REG     (1ULL << 8)
#define LANDLOCK_ACCESS_FS_MAKE_SOCK    (1ULL << 9)
#define LANDLOCK_ACCESS_FS_MAKE_FIFO    (1ULL << 10)
#define LANDLOCK_ACCESS_FS_MAKE_BLOCK   (1ULL << 11)
#define LANDLOCK_ACCESS_FS_MAKE_SYM     (1ULL << 12)
#define LANDLOCK_ACCESS_FS_TRUNCATE     (1ULL << 14)
#define LANDLOCK_CREATE_RULESET_VERSION (1U << 0)
#endif
#ifndef LANDLOCK_RULE_PATH_BENEATH
#define LANDLOCK_RULE_PATH_BENEATH 1
#endif

struct landlock_ruleset_attr {
    uint64_t handled_access_fs;
};

struct landlock_path_beneath_attr {
    uint64_t allowed_access;
    int32_t parent_fd;
} __attribute__((packed));

static int landlock_create_ruleset(const struct landlock_ruleset_attr *attr, size_t size, uint32_t flags) {
    return (int)syscall(SYS_landlock_create_ruleset, attr, size, flags);
}

static int landlock_add_rule(int ruleset_fd, int rule_type, const void *rule, uint32_t flags) {
    return (int)syscall(SYS_landlock_add_rule, ruleset_fd, rule_type, rule, flags);
}

static int landlock_restrict_self(int ruleset_fd, uint32_t flags) {
    return (int)syscall(SYS_landlock_restrict_self, ruleset_fd, flags);
}

static uint64_t write_access_set(int include_truncate) {
    uint64_t set = LANDLOCK_ACCESS_FS_WRITE_FILE
        | LANDLOCK_ACCESS_FS_REMOVE_DIR
        | LANDLOCK_ACCESS_FS_REMOVE_FILE
        | LANDLOCK_ACCESS_FS_MAKE_CHAR
        | LANDLOCK_ACCESS_FS_MAKE_DIR
        | LANDLOCK_ACCESS_FS_MAKE_REG
        | LANDLOCK_ACCESS_FS_MAKE_SOCK
        | LANDLOCK_ACCESS_FS_MAKE_FIFO
        | LANDLOCK_ACCESS_FS_MAKE_BLOCK
        | LANDLOCK_ACCESS_FS_MAKE_SYM;
    if (include_truncate) set |= LANDLOCK_ACCESS_FS_TRUNCATE;
    return set;
}

static int try_create_ruleset(uint64_t handled) {
    struct landlock_ruleset_attr attr = { .handled_access_fs = handled };
    return landlock_create_ruleset(&attr, sizeof(attr), 0);
}

static int allow_under(int ruleset_fd, uint64_t access, const char *path) {
    int fd = open(path, O_PATH | O_CLOEXEC);
    if (fd < 0) return -1;
    struct landlock_path_beneath_attr rule = { .allowed_access = access, .parent_fd = fd };
    int rc = landlock_add_rule(ruleset_fd, LANDLOCK_RULE_PATH_BENEATH, &rule, 0);
    close(fd);
    return rc;
}

static void fail(const char *what) {
    fprintf(stderr, "[sandbox: setup failed: %s: %s]\n", what, strerror(errno));
    exit(126);
}

int main(int argc, char **argv) {
    if (argc < 5 || strcmp(argv[3], "--") != 0) {
        fprintf(stderr, "usage: landlock-exec <workspace-write|read-only> <allow-root> -- <cmd> [args...]\n");
        return 125;
    }
    const char *mode = argv[1];
    const char *root = argv[2];

    int include_truncate = 1;
    int ruleset_fd = try_create_ruleset(write_access_set(1));
    if (ruleset_fd < 0) {
        include_truncate = 0; // older kernel without TRUNCATE support
        ruleset_fd = try_create_ruleset(write_access_set(0));
        if (ruleset_fd < 0) fail("landlock_create_ruleset (kernel without Landlock?)");
    }

    if (strcmp(mode, "workspace-write") == 0) {
        if (allow_under(ruleset_fd, write_access_set(include_truncate), root) != 0) {
            fail("landlock_add_rule for workspace root");
        }
        // The harness home must stay writable for spill/attachments? No: keep strict.
    } else if (strcmp(mode, "read-only") == 0) {
        // no allow rules: writes are denied everywhere
    } else {
        fprintf(stderr, "[sandbox: unknown mode %s]\n", mode);
        return 125;
    }

    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0) fail("PR_SET_NO_NEW_PRIVS");
    if (landlock_restrict_self(ruleset_fd, 0) != 0) fail("landlock_restrict_self");

    execvp(argv[4], &argv[4]);
    fail("execvp");
    return 127;
}
