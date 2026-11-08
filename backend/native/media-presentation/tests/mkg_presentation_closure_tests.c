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

int main(void) {
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
