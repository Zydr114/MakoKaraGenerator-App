static void use_binary_pipes(void) {
    int out=_fileno(stdout),error=_fileno(stderr);
    if((out>=0&&_setmode(out,_O_BINARY)<0)||(error>=0&&_setmode(error,_O_BINARY)<0))die("presentation-binary-pipe-unavailable");
}

static HANDLE inherited_metadata_pipe(void) {
    wchar_t text[3*sizeof(uintptr_t)+1];
    DWORD length=GetEnvironmentVariableW(L"MAKO_MEDIA_METADATA_HANDLE",text,_countof(text));
    if(!length||length>=_countof(text))die("presentation-metadata-handle-invalid");
    uintptr_t value=0;
    for(DWORD index=0;index<length;index++) {
        if(text[index]<L'0'||text[index]>L'9')die("presentation-metadata-handle-invalid");
        unsigned digit=(unsigned)(text[index]-L'0');
        if(value>(UINTPTR_MAX-digit)/10)die("presentation-metadata-handle-invalid");
        value=value*10+digit;
    }
    HANDLE pipe=(HANDLE)value;
    if(!pipe||pipe==INVALID_HANDLE_VALUE||pipe==GetStdHandle(STD_INPUT_HANDLE)||
       pipe==GetStdHandle(STD_OUTPUT_HANDLE)||pipe==GetStdHandle(STD_ERROR_HANDLE)||
       GetFileType(pipe)!=FILE_TYPE_PIPE)die("presentation-metadata-handle-invalid");
    return pipe;
}

static HANDLE open_regular_file(const wchar_t *path, LARGE_INTEGER *size) {
    HANDLE file=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    BY_HANDLE_FILE_INFORMATION attributes;
    if(file==INVALID_HANDLE_VALUE)return file;
    if(GetFileType(file)!=FILE_TYPE_DISK||
       !GetFileInformationByHandle(file,&attributes)||
       (attributes.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT))||
       !GetFileSizeEx(file,size)||size->QuadPart<0) {
        CloseHandle(file);
        return INVALID_HANDLE_VALUE;
    }
    return file;
}

static HANDLE open_loaded_image(HMODULE module,wchar_t canonical[32768],LARGE_INTEGER *size) {
    wchar_t path[32768]=L"\\\\?\\GLOBALROOT";
    const DWORD prefix=_countof(L"\\\\?\\GLOBALROOT")-1;
    DWORD length=K32GetMappedFileNameW(GetCurrentProcess(),module,path+prefix,_countof(path)-prefix);
    if(!length||length>=_countof(path)-prefix)die("presentation-loaded-image-unresolved");
    HANDLE file=open_regular_file(path,size);
    if(file==INVALID_HANDLE_VALUE)die("presentation-loaded-image-invalid");
    length=GetFinalPathNameByHandleW(file,canonical,32768,FILE_NAME_NORMALIZED|VOLUME_NAME_DOS);
    if(length<8||length>=32768||wcsncmp(canonical,L"\\\\?\\",4)||
       canonical[5]!=L':'||canonical[6]!=L'\\')die("presentation-loaded-image-unresolved");
    return file;
}

typedef struct LoadedImageIdentity {
    FILE_ID_INFO file_id;
    LARGE_INTEGER size, write_time, change_time;
    int has_times, cacheable;
} LoadedImageIdentity;

typedef struct LoadedImageHash { LoadedImageIdentity identity; uint8_t hash[32]; } LoadedImageHash;
static LoadedImageHash image_hashes[1024];
static unsigned image_hash_count;

static LoadedImageIdentity image_identity(HANDLE file) {
    LoadedImageIdentity identity={0};
    FILE_BASIC_INFO basic;
    if(!GetFileSizeEx(file,&identity.size))die("presentation-loaded-image-invalid");
    identity.has_times=GetFileInformationByHandleEx(file,FileBasicInfo,&basic,sizeof basic)!=0;
    if(identity.has_times) {
        identity.write_time=basic.LastWriteTime;
        identity.change_time=basic.ChangeTime;
    }
    wchar_t filesystem[32];
    identity.cacheable=identity.change_time.QuadPart&&
        GetVolumeInformationByHandleW(file,NULL,0,NULL,NULL,NULL,filesystem,_countof(filesystem))&&
        (!wcscmp(filesystem,L"NTFS")||!wcscmp(filesystem,L"ReFS"))&&
        GetFileInformationByHandleEx(file,FileIdInfo,&identity.file_id,sizeof identity.file_id);
    return identity;
}

static int same_image(const LoadedImageIdentity *before,const LoadedImageIdentity *after) {
    return before->cacheable&&after->cacheable&&
        before->file_id.VolumeSerialNumber==after->file_id.VolumeSerialNumber&&
        !memcmp(&before->file_id.FileId,&after->file_id.FileId,sizeof before->file_id.FileId)&&
        before->size.QuadPart==after->size.QuadPart&&before->write_time.QuadPart==after->write_time.QuadPart&&
        before->change_time.QuadPart==after->change_time.QuadPart;
}

static void image_hash(HANDLE file,const LoadedImageIdentity *before,uint8_t out[32]) {
    for(unsigned index=0;index<image_hash_count;index++) {
        if(same_image(&image_hashes[index].identity,before)){memcpy(out,image_hashes[index].hash,32);return;}
    }
    struct AVSHA *hash=av_sha_alloc();if(!hash||av_sha_init(hash,256)<0)die("presentation-hash-unavailable");
    uint8_t buffer[65536];DWORD bytes;uint64_t total=0;
    do {
        if(!ReadFile(file,buffer,sizeof buffer,&bytes,NULL))die("presentation-loaded-image-read-failed");
        total+=bytes;av_sha_update(hash,buffer,bytes);
    } while(bytes);
    LoadedImageIdentity after=image_identity(file);
    if(total!=(uint64_t)before->size.QuadPart||before->size.QuadPart!=after.size.QuadPart||
       (before->has_times&&(!after.has_times||before->write_time.QuadPart!=after.write_time.QuadPart||
                           before->change_time.QuadPart!=after.change_time.QuadPart))||
       (before->cacheable&&!same_image(before,&after)))die("presentation-loaded-image-changed");
    av_sha_final(hash,out);av_free(hash);
    if(before->cacheable&&image_hash_count<_countof(image_hashes)) {
        LoadedImageHash *entry=&image_hashes[image_hash_count++];
        entry->identity=*before;memcpy(entry->hash,out,32);
    }
}

static void loaded_closure(void) {
    HMODULE modules[1024];DWORD needed;
    if(!K32EnumProcessModules(GetCurrentProcess(),modules,sizeof modules,&needed)||
       !needed||needed>sizeof modules||needed%sizeof *modules)die("presentation-loaded-image-enumeration-failed");
    for(DWORD index=0;index<needed/sizeof *modules;index++) {
        wchar_t canonical[32768];LARGE_INTEGER size;
        HANDLE file=open_loaded_image(modules[index],canonical,&size);
        LoadedImageIdentity before=image_identity(file);
        if(before.size.QuadPart!=size.QuadPart)die("presentation-loaded-image-changed");
        uint8_t body[44+4096+1];
        put64(body,(uint64_t)before.size.QuadPart);image_hash(file,&before,body+8);CloseHandle(file);
        int length=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,canonical+4,-1,(char *)body+44,sizeof body-44,NULL,NULL);
        if(length<=1)die("presentation-loaded-image-unresolved");
        put32(body+40,(uint32_t)(length-1));record(4,body,(uint32_t)(44+length-1));
    }
    record(5,NULL,0);
}

static int read_packet(void *state,uint8_t *buf,int bytes) {
    (void)state;DWORD count;
    return !ReadFile(source_fd,buf,(DWORD)bytes,&count,NULL)?AVERROR(EIO):count?(int)count:AVERROR_EOF;
}

static int64_t seek_packet(void *state,int64_t offset,int whence) {
    (void)state;
    if((whence&~AVSEEK_FORCE)==AVSEEK_SIZE)return source_size;
    DWORD method;
    switch(whence&~AVSEEK_FORCE) {
        case SEEK_SET:method=FILE_BEGIN;break;
        case SEEK_CUR:method=FILE_CURRENT;break;
        case SEEK_END:method=FILE_END;break;
        default:return AVERROR(EINVAL);
    }
    LARGE_INTEGER distance,position;distance.QuadPart=offset;
    return SetFilePointerEx(source_fd,distance,&position,method)?position.QuadPart:AVERROR(EIO);
}

static void open_source(const wchar_t *path) {
    wchar_t extended[32768];
    if(wcslen(path)>=MAX_PATH&&wcsncmp(path,L"\\\\?\\",4)) {
        DWORD length=GetFullPathNameW(path,_countof(extended)-8,extended+8,NULL);
        if(!length||length>=_countof(extended)-8)die("presentation-source-invalid");
        if(!wcsncmp(extended+8,L"\\\\",2)) {
            memcpy(extended+2,L"\\\\?\\UNC\\",8*sizeof *extended);
            path=extended+2;
        } else {
            memcpy(extended+4,L"\\\\?\\",4*sizeof *extended);
            path=extended+4;
        }
    }
    LARGE_INTEGER size;
    source_fd=open_regular_file(path,&size);
    if(source_fd==INVALID_HANDLE_VALUE||size.QuadPart<=0||size.QuadPart>MAX_SOURCE)die("presentation-source-invalid");
    source_size=size.QuadPart;
}
