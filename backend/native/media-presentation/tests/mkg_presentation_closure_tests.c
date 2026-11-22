/* Source-level regression: the loaded closure is re-reported before every frame, but an unchanged image is read
 * and hashed only once per run, and any content change is hashed again. */
#define main mkg_presentation_entrypoint
#include "../mkg_presentation.c"
#undef main

static void check(int condition,const char *detail) {
    if(!condition) { fprintf(stderr,"closure-test-failed: %s\n",detail); exit(1); }
}

static void hash_path(const char *path,uint8_t out[32]) {
    int fd=open(path,O_RDONLY|O_CLOEXEC|O_NOFOLLOW);struct stat st;
    check(fd>=0&&!fstat(fd,&st),"open temporary image");
    image_hash(path,fd,&st,out);close(fd);
}

static void append(const char *path,const char *text) {
    int fd=open(path,O_WRONLY|O_APPEND|O_CLOEXEC);
    check(fd>=0&&write(fd,text,strlen(text))==(ssize_t)strlen(text),"modify temporary image");close(fd);
}

static void test_metadata_fd_and_ffmpeg_version_compat(void) {
    int fd = -1;
    check(try_parse_metadata_fd(NULL, &fd) && fd == 3, "unset MKG_MEDIA_METADATA_FD falls back to 3");
    check(try_parse_metadata_fd("3", &fd) && fd == 3, "valid fd 3");
    check(try_parse_metadata_fd("4", &fd) && fd == 4, "valid fd 4");
    check(try_parse_metadata_fd("2147483647", &fd) && fd == INT_MAX, "valid INT_MAX fd");

    static const char *const invalid_fds[] = {
        "", "0", "-1", "-3", "+3", " 3", "3 ", "3a", "abc", "2147483648", "99999999999999999999"
    };
    for(size_t i=0;i<sizeof invalid_fds/sizeof invalid_fds[0];i++) {
        check(!try_parse_metadata_fd(invalid_fds[i], &fd), "reject invalid MKG_MEDIA_METADATA_FD");
    }

    const char *prev = getenv("MKG_MEDIA_METADATA_FD");
    char *saved = prev ? strdup(prev) : NULL;
    check(!unsetenv("MKG_MEDIA_METADATA_FD") && resolve_metadata_fd() == 3, "resolve unset env returns 3");
    check(!setenv("MKG_MEDIA_METADATA_FD", "7", 1) && resolve_metadata_fd() == 7, "resolve valid env returns 7");
    if(saved) { setenv("MKG_MEDIA_METADATA_FD", saved, 1); free(saved); }
    else unsetenv("MKG_MEDIA_METADATA_FD");

    check(compatible_av_version(AV_VERSION_INT(63, 16, 100), 63, 16), "exact version matches");
    check(compatible_av_version(AV_VERSION_INT(63, 16, 105), 63, 16), "higher micro version allowed");
    check(compatible_av_version(AV_VERSION_INT(63, 16, 0), 63, 16), "lower micro version allowed");
    check(compatible_av_version(AV_VERSION_INT(63, 17, 0), 63, 16), "higher minor version allowed");
    check(!compatible_av_version(AV_VERSION_INT(63, 15, 100), 63, 16), "lower minor version rejected");
    check(!compatible_av_version(AV_VERSION_INT(64, 16, 100), 63, 16), "higher major version rejected");
    check(!compatible_av_version(AV_VERSION_INT(62, 99, 100), 63, 16), "lower major version rejected");
    configure_av();
}

int main(void) {
    test_metadata_fd_and_ffmpeg_version_compat();
    metadata_fd=open("/dev/null",O_WRONLY|O_CLOEXEC);check(metadata_fd>=0,"metadata sink");
    loaded_closure();
    uint64_t first=image_hash_reads;
    check(first>=2,"first closure hashes the executable and its libraries");
    for(int frame=0;frame<8;frame++) loaded_closure();
    check(image_hash_reads==first,"later closures reuse unchanged image hashes");

    char path[]="/tmp/mkg-presentation-closure-XXXXXX";
    int fd=mkstemp(path);check(fd>=0,"temporary image");close(fd);
    append(path,"image-a");
    uint8_t a[32],again[32],b[32];
    hash_path(path,a);uint64_t afterA=image_hash_reads;
    hash_path(path,again);
    check(image_hash_reads==afterA&&!memcmp(a,again,32),"unchanged image is served from the cache");
    append(path,"-b");
    hash_path(path,b);
    check(image_hash_reads==afterA+1&&memcmp(a,b,32),"changed image is read and hashed again");
    unlink(path);
    return 0;
}
