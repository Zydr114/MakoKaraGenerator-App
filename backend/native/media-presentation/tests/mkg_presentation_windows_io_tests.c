#define wmain mkg_presentation_entrypoint
#include "../mkg_presentation.c"
#undef wmain

static void check(int condition,const char *detail) {
    if(!condition) { fprintf(stderr,"windows-io-test-failed: %s\n",detail);exit(1); }
}

static void write_file(const wchar_t *path,uint8_t value) {
    HANDLE file=CreateFileW(path,GENERIC_WRITE,0,NULL,TRUNCATE_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);
    DWORD written;
    check(file!=INVALID_HANDLE_VALUE&&WriteFile(file,&value,1,&written,NULL)&&written==1,"write temporary image");
    CloseHandle(file);
}

static void hash_uncached(const wchar_t *path,uint8_t out[32]) {
    LARGE_INTEGER size;
    HANDLE file=open_regular_file(path,&size);
    check(file!=INVALID_HANDLE_VALUE,"open temporary image");
    LoadedImageIdentity before={.size=size};
    image_hash(file,&before,out);
    CloseHandle(file);
}

int wmain(void) {
    configure_av();
    wchar_t directory[MAX_PATH],path[MAX_PATH];
    DWORD length=GetTempPathW(_countof(directory),directory);
    check(length>0&&length<_countof(directory)&&GetTempFileNameW(directory,L"mkg",0,path)!=0,"temporary image path");
    uint8_t first[32],again[32],changed[32];
    write_file(path,1);
    hash_uncached(path,first);hash_uncached(path,again);
    check(!memcmp(first,again,32)&&image_hash_count==0,"stable read without metadata never populates cache");
    write_file(path,2);
    hash_uncached(path,changed);
    check(memcmp(first,changed,32)!=0&&image_hash_count==0,"changed bytes are hashed without metadata");

    open_source(path);
    const int64_t offset=5LL*1024*1024*1024;
    check(seek_packet(NULL,0,AVSEEK_SIZE)==1,"size query");
    check(seek_packet(NULL,offset,SEEK_SET)==offset,"absolute seek beyond 32-bit range");
    check(seek_packet(NULL,-4,SEEK_CUR|AVSEEK_FORCE)==offset-4,"relative seek beyond 32-bit range");
    check(seek_packet(NULL,-1,SEEK_END)==0,"seek from end");
    uint8_t value;
    check(read_packet(NULL,&value,1)==1&&value==2&&read_packet(NULL,&value,1)==AVERROR_EOF,"binary byte and EOF");
    CloseHandle(source_fd);
    check(DeleteFileW(path)!=0,"delete temporary image");
    return 0;
}
