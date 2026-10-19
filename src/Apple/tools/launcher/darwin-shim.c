/*
 * Linux stand-in for the macOS C library ("/usr/lib/libc.dylib", "/usr/lib/libSystem.dylib")
 * the iOS workload's tasks and trimmer steps P/Invoke, which also run off a Mac: SmartCopy's
 * stat(), PathUtils' realpath(), the file copies' copyfile(), ... The build loads it for those
 * names (NativeAliases.cs). Calls that mean the same on Linux (realpath, readlink, symlink,
 * unlink, chmod, strerror, isatty, ...) resolve through this library's glibc dependency; this
 * file defines the ones whose macOS form differs:
 *  - stat/lstat (and their x86_64 names stat$INODE64/lstat$INODE64): macOS's struct stat layout;
 *  - copyfile and its state functions, which glibc doesn't have;
 *  - sysctlbyname, which Linux doesn't have: "no such name".
 * Compiled with zig cc at build time.
 */
#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

/* macOS's struct stat (64-bit inodes), 144 bytes. */
struct darwin_timespec { int64_t tv_sec; int64_t tv_nsec; };
struct darwin_stat {
    int32_t st_dev;
    uint16_t st_mode;
    uint16_t st_nlink;
    uint64_t st_ino;
    uint32_t st_uid;
    uint32_t st_gid;
    int32_t st_rdev;
    int32_t pad;
    struct darwin_timespec st_atimespec, st_mtimespec, st_ctimespec, st_birthtimespec;
    int64_t st_size;
    int64_t st_blocks;
    int32_t st_blksize;
    uint32_t st_flags;
    uint32_t st_gen;
    int32_t st_lspare;
    int64_t st_qspare[2];
};
_Static_assert(sizeof(struct darwin_stat) == 144, "macOS struct stat is 144 bytes");

static int darwin_stat_at(const char *path, struct darwin_stat *out, int flags)
{
    struct stat st;
    if (fstatat(AT_FDCWD, path, &st, flags) != 0)
        return -1;
    memset(out, 0, sizeof *out);
    out->st_dev = (int32_t)st.st_dev;
    out->st_mode = (uint16_t)st.st_mode; /* the S_IF* and permission bits are the same */
    out->st_nlink = (uint16_t)st.st_nlink;
    out->st_ino = st.st_ino;
    out->st_uid = st.st_uid;
    out->st_gid = st.st_gid;
    out->st_rdev = (int32_t)st.st_rdev;
    out->st_atimespec = (struct darwin_timespec){ st.st_atim.tv_sec, st.st_atim.tv_nsec };
    out->st_mtimespec = (struct darwin_timespec){ st.st_mtim.tv_sec, st.st_mtim.tv_nsec };
    out->st_ctimespec = (struct darwin_timespec){ st.st_ctim.tv_sec, st.st_ctim.tv_nsec };
    out->st_birthtimespec = out->st_ctimespec;
    out->st_size = st.st_size;
    out->st_blocks = st.st_blocks;
    out->st_blksize = (int32_t)st.st_blksize;
    return 0;
}

int darwin_stat(const char *path, struct darwin_stat *out) __asm__("stat");
int darwin_stat(const char *path, struct darwin_stat *out) { return darwin_stat_at(path, out, 0); }
int darwin_stat64(const char *path, struct darwin_stat *out) __asm__("stat$INODE64");
int darwin_stat64(const char *path, struct darwin_stat *out) { return darwin_stat_at(path, out, 0); }
int darwin_lstat(const char *path, struct darwin_stat *out) __asm__("lstat");
int darwin_lstat(const char *path, struct darwin_stat *out) { return darwin_stat_at(path, out, AT_SYMLINK_NOFOLLOW); }
int darwin_lstat64(const char *path, struct darwin_stat *out) __asm__("lstat$INODE64");
int darwin_lstat64(const char *path, struct darwin_stat *out) { return darwin_stat_at(path, out, AT_SYMLINK_NOFOLLOW); }

/* copyfile(3): the flags the workload's copies use. */
#define COPYFILE_EXCL (1 << 17)
#define COPYFILE_NOFOLLOW_SRC (1 << 18)
#define COPYFILE_RECURSIVE (1 << 15)
#define COPYFILE_UNLINK (1 << 21)

void *copyfile_state_alloc(void) { return calloc(1, 16); }
int copyfile_state_free(void *state) { free(state); return 0; }
int copyfile_state_set(void *state, uint32_t flag, const void *value) { (void)state; (void)flag; (void)value; return 0; }

static int copy_regular(const char *from, const char *to, mode_t mode, uint32_t flags)
{
    int in = open(from, O_RDONLY | O_CLOEXEC);
    if (in < 0)
        return -1;
    int out = open(to, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC | ((flags & COPYFILE_EXCL) ? O_EXCL : 0), mode & 07777);
    if (out < 0) {
        int e = errno; close(in); errno = e;
        return -1;
    }
    char buffer[65536];
    ssize_t n;
    while ((n = read(in, buffer, sizeof buffer)) > 0) {
        for (ssize_t done = 0; done < n;) {
            ssize_t w = write(out, buffer + done, (size_t)(n - done));
            if (w < 0) { int e = errno; close(in); close(out); errno = e; return -1; }
            done += w;
        }
    }
    int e = n < 0 ? errno : 0;
    close(in);
    if (fchmod(out, mode & 07777) != 0 && e == 0)
        e = errno;
    if (close(out) != 0 && e == 0)
        e = errno;
    errno = e;
    return e ? -1 : 0;
}

static int copy_path(const char *from, const char *to, uint32_t flags, int top)
{
    struct stat st;
    if (fstatat(AT_FDCWD, from, &st, (flags & COPYFILE_NOFOLLOW_SRC) || !top ? AT_SYMLINK_NOFOLLOW : 0) != 0)
        return -1;
    if (flags & COPYFILE_UNLINK)
        unlink(to);
    if (S_ISLNK(st.st_mode)) {
        char target[PATH_MAX];
        ssize_t n = readlink(from, target, sizeof target - 1);
        if (n < 0)
            return -1;
        target[n] = 0;
        unlink(to);
        return symlink(target, to);
    }
    if (!S_ISDIR(st.st_mode))
        return copy_regular(from, to, st.st_mode, flags);
    if (!(flags & COPYFILE_RECURSIVE)) {
        errno = EISDIR;
        return -1;
    }
    if (mkdir(to, st.st_mode & 07777) != 0 && errno != EEXIST)
        return -1;
    DIR *dir = opendir(from);
    if (dir == NULL)
        return -1;
    struct dirent *entry;
    int result = 0;
    while (result == 0 && (entry = readdir(dir)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0)
            continue;
        char child_from[PATH_MAX], child_to[PATH_MAX];
        if (snprintf(child_from, sizeof child_from, "%s/%s", from, entry->d_name) >= (int)sizeof child_from
            || snprintf(child_to, sizeof child_to, "%s/%s", to, entry->d_name) >= (int)sizeof child_to) {
            errno = ENAMETOOLONG;
            result = -1;
            break;
        }
        result = copy_path(child_from, child_to, flags & ~COPYFILE_EXCL, 0);
    }
    int e = errno;
    closedir(dir);
    errno = e;
    return result;
}

int copyfile(const char *from, const char *to, void *state, uint32_t flags)
{
    (void)state;
    return copy_path(from, to, flags, 1);
}

int sysctlbyname(const char *name, void *oldp, size_t *oldlenp, void *newp, size_t newlen)
{
    (void)name; (void)oldp; (void)oldlenp; (void)newp; (void)newlen;
    errno = ENOENT;
    return -1;
}
