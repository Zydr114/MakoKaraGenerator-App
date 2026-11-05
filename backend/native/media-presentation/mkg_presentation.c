/* M10 DRAFT ONLY. Pinned FFmpeg 9.x helper; not built or executed in this task.
 * Metadata and pixels are emitted from the SAME avcodec_receive_frame AVFrame.
 * No best_effort_timestamp, guessed frame rate, scaler, filter or external IO.
 */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <limits.h>
#include <link.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/buffer.h>
#include <libavutil/imgutils.h>
#include <libavutil/pixdesc.h>
#include <libavutil/sha.h>

#if LIBAVFORMAT_VERSION_MAJOR != 63 || LIBAVCODEC_VERSION_MAJOR != 63 || LIBAVUTIL_VERSION_MAJOR != 61
#error "The controlled presentation capability requires FFmpeg 9 ABI headers."
#endif

#define MAX_FRAMES 54000
#define MAX_SOURCE (8LL*1024*1024*1024)
#define MAX_PIXELS 16777216
#define MAX_SIDE 8192
#define META_BODY 224
static int metadata_fd = 3;
static uint8_t emitted[MAX_FRAMES];
static uint64_t packet_count, frame_count;
static int source_fd = -1;
static int64_t source_size;
static AVRational packet_tb, selected_stream_sar;
static int stream_index, stream_id, family, inspect_only;
static unsigned loaded_count;

typedef struct PacketProof {
    uint64_t id;
    int64_t position, pts, duration;
    int size;
    uint8_t hash[32];
} PacketProof;

static void die(const char *code) { (void)write(2, code, strlen(code)); (void)write(2, "\n", 1); _exit(65); }
static void put32(uint8_t *p, uint32_t v) { for(int i=0;i<4;i++) p[i]=(uint8_t)(v>>(8*i)); }
static void put64(uint8_t *p, uint64_t v) { for(int i=0;i<8;i++) p[i]=(uint8_t)(v>>(8*i)); }
static void allwrite(int fd, const void *data, size_t size) {
    const uint8_t *p=data;
    while(size) { ssize_t n=write(fd,p,size); if(n<0&&errno==EINTR)continue; if(n<=0)die("presentation-pipe-write-failed"); p+=n;size-=(size_t)n; }
}
static void record(uint32_t type,const void *body,uint32_t size) {
    if(size>16384)die("presentation-metadata-capacity");
    uint8_t h[16]={'M','K','G','P','R','E','1',0}; put32(h+8,type);put32(h+12,size);
    allwrite(metadata_fd,h,sizeof h); if(size)allwrite(metadata_fd,body,size);
}
static void sha(const uint8_t *p,size_t n,uint8_t out[32]) {
    struct AVSHA *h=av_sha_alloc(); if(!h||av_sha_init(h,256)<0)die("presentation-hash-unavailable");
    av_sha_update(h,p,n);av_sha_final(h,out);av_free(h);
}
static int loaded_file(struct dl_phdr_info *info,size_t size,void *state) {
    (void)size;(void)state;
    if(++loaded_count>1024)die("presentation-loaded-image-capacity");
    const char *name=info->dlpi_name;
    char executable[PATH_MAX];
    if(!name||!name[0]) { ssize_t n=readlink("/proc/self/exe",executable,sizeof executable-1);if(n<=0)die("presentation-self-image-unavailable");executable[n]=0;name=executable; }
    if(!strcmp(name,"linux-vdso.so.1")) return 0; /* kernel pseudo-image; no filesystem content */
    char resolved[PATH_MAX];if(!realpath(name,resolved))die("presentation-loaded-image-unresolved");
    int fd=open(resolved,O_RDONLY|O_CLOEXEC|O_NOFOLLOW);struct stat before,after;
    if(fd<0||fstat(fd,&before)||!S_ISREG(before.st_mode)||before.st_size<0)die("presentation-loaded-image-invalid");
    struct AVSHA *h=av_sha_alloc();if(!h||av_sha_init(h,256)<0)die("presentation-hash-unavailable");
    uint8_t buffer[8192];ssize_t n;
    while((n=read(fd,buffer,sizeof buffer))!=0){if(n<0&&errno==EINTR)continue;if(n<0)die("presentation-loaded-image-read-failed");av_sha_update(h,buffer,(size_t)n);}
    if(fstat(fd,&after)||before.st_ino!=after.st_ino||before.st_dev!=after.st_dev||before.st_size!=after.st_size||before.st_mtim.tv_sec!=after.st_mtim.tv_sec||before.st_mtim.tv_nsec!=after.st_mtim.tv_nsec)die("presentation-loaded-image-changed");
    close(fd);uint8_t body[48+PATH_MAX];memset(body,0,sizeof body);put64(body,(uint64_t)before.st_size);av_sha_final(h,body+8);av_free(h);
    size_t length=strlen(resolved);put32(body+40,(uint32_t)length);memcpy(body+44,resolved,length);record(4,body,(uint32_t)(44+length));return 0;
}
static void loaded_closure(void) { loaded_count=0;if(dl_iterate_phdr(loaded_file,NULL)<0)die("presentation-loaded-image-enumeration-failed");record(5,NULL,0); }
static int read_packet(void *state,uint8_t *buf,int bytes) { (void)state;ssize_t n;do{n=read(source_fd,buf,(size_t)bytes);}while(n<0&&errno==EINTR);return n<0?AVERROR(errno):n==0?AVERROR_EOF:(int)n; }
static int64_t seek_packet(void *state,int64_t offset,int whence) { (void)state;if(whence==AVSEEK_SIZE)return source_size;return lseek(source_fd,offset,whence&~AVSEEK_FORCE); }
static int deny_open(AVFormatContext *s,AVIOContext **pb,const char *url,int flags,AVDictionary **options) { (void)s;(void)pb;(void)url;(void)flags;(void)options;return AVERROR(EPERM); }
static const char *decoder_name(enum AVCodecID id) {
    switch(id) {
        case AV_CODEC_ID_H264:return "h264";case AV_CODEC_ID_HEVC:return "hevc";
        case AV_CODEC_ID_MPEG4:return "mpeg4";case AV_CODEC_ID_VP8:return "vp8";
        case AV_CODEC_ID_VP9:return "vp9";case AV_CODEC_ID_FFV1:return "ffv1";
        case AV_CODEC_ID_PNG:return "png";case AV_CODEC_ID_MJPEG:return "mjpeg";
        default:die("presentation-codec-unsupported");return NULL;
    }
}
static enum AVPixelFormat software_format(AVCodecContext *ctx,const enum AVPixelFormat *formats) {
    (void)ctx;
    for(;*formats!=AV_PIX_FMT_NONE;formats++) {
        const AVPixFmtDescriptor *d=av_pix_fmt_desc_get(*formats);
        if(d&&!(d->flags&AV_PIX_FMT_FLAG_HWACCEL))return *formats;
    }
    return AV_PIX_FMT_NONE;
}
static int flagged_side_data(const AVFrame *f) {
    return av_frame_get_side_data(f,AV_FRAME_DATA_ICC_PROFILE)||av_frame_get_side_data(f,AV_FRAME_DATA_MASTERING_DISPLAY_METADATA)||
        av_frame_get_side_data(f,AV_FRAME_DATA_CONTENT_LIGHT_LEVEL)||av_frame_get_side_data(f,AV_FRAME_DATA_DYNAMIC_HDR_PLUS)||
        av_frame_get_side_data(f,AV_FRAME_DATA_DYNAMIC_HDR_VIVID)||av_frame_get_side_data(f,AV_FRAME_DATA_DOVI_RPU_BUFFER)||
        av_frame_get_side_data(f,AV_FRAME_DATA_DOVI_METADATA)||av_frame_get_side_data(f,AV_FRAME_DATA_DYNAMIC_HDR_SMPTE_2094_APP5)||
        av_frame_get_side_data(f,AV_FRAME_DATA_AMBIENT_VIEWING_ENVIRONMENT)||av_frame_get_side_data(f,AV_FRAME_DATA_RAW_COLOR_PARAMS);
}
static int flagged_packet_side_data(const AVPacketSideData *data,int count) {
    return av_packet_side_data_get(data,count,AV_PKT_DATA_ICC_PROFILE)||av_packet_side_data_get(data,count,AV_PKT_DATA_MASTERING_DISPLAY_METADATA)||
        av_packet_side_data_get(data,count,AV_PKT_DATA_CONTENT_LIGHT_LEVEL)||av_packet_side_data_get(data,count,AV_PKT_DATA_DOVI_CONF)||
        av_packet_side_data_get(data,count,AV_PKT_DATA_DYNAMIC_HDR10_PLUS)||av_packet_side_data_get(data,count,AV_PKT_DATA_DYNAMIC_HDR_SMPTE_2094_APP5)||
        av_packet_side_data_get(data,count,AV_PKT_DATA_AMBIENT_VIEWING_ENVIRONMENT)||av_packet_side_data_get(data,count,AV_PKT_DATA_HEVC_CONF);
}
static int plane_layout(enum AVPixelFormat fmt,int still,int *components,int *bytes,int *sx,int *sy,int *swap16,int *gray,int *palette) {
    *components=1;*bytes=1;*sx=1;*sy=1;*swap16=0;*gray=0;*palette=0;
    switch(fmt) {
        case AV_PIX_FMT_RGB24:*components=3;return 1;
        case AV_PIX_FMT_GBRP:*components=3;*gray=3;return still?1:0;
        case AV_PIX_FMT_RGBA:*components=4;return 2;
        case AV_PIX_FMT_RGB48LE:*components=3;*bytes=2;return still?3:0;
        case AV_PIX_FMT_RGBA64LE:*components=4;*bytes=2;return still?4:0;
        case AV_PIX_FMT_RGB48BE:*components=3;*bytes=2;*swap16=1;return still?3:0;
        case AV_PIX_FMT_RGBA64BE:*components=4;*bytes=2;*swap16=1;return still?4:0;
        case AV_PIX_FMT_GRAY8:*components=3;*gray=1;return still?1:0;
        case AV_PIX_FMT_GRAY16LE:*components=3;*bytes=2;*gray=1;return still?3:0;
        case AV_PIX_FMT_GRAY16BE:*components=3;*bytes=2;*gray=1;*swap16=1;return still?3:0;
        case AV_PIX_FMT_YA8:*components=4;*gray=2;return still?2:0;
        case AV_PIX_FMT_YA16LE:*components=4;*bytes=2;*gray=2;return still?4:0;
        case AV_PIX_FMT_YA16BE:*components=4;*bytes=2;*gray=2;*swap16=1;return still?4:0;
        case AV_PIX_FMT_PAL8:*components=4;*palette=1;return still?2:0;
        case AV_PIX_FMT_YUV420P:case AV_PIX_FMT_YUVJ420P:*sx=2;*sy=2;return 5;
        case AV_PIX_FMT_YUV422P:case AV_PIX_FMT_YUVJ422P:*sx=2;return 6;
        case AV_PIX_FMT_YUV444P:case AV_PIX_FMT_YUVJ444P:return 7;
        case AV_PIX_FMT_YUV420P10LE:*bytes=2;*sx=2;*sy=2;return still?0:8;
        case AV_PIX_FMT_YUV422P10LE:*bytes=2;*sx=2;return still?0:9;
        case AV_PIX_FMT_YUV444P10LE:*bytes=2;return still?0:10;
        default:return 0;
    }
}
static void emit_frame(AVFrame *f,const AVCodecParameters *par) {
    if(frame_count>=MAX_FRAMES||!f->opaque_ref||f->opaque_ref->size!=sizeof(PacketProof))die("presentation-packet-frame-association-missing");
    PacketProof *proof=(PacketProof*)f->opaque_ref->data;
    if(proof->id>=packet_count||emitted[proof->id]||f->pts!=proof->pts)die("presentation-packet-frame-association-mismatch");
    if(f->width<=0||f->height<=0||f->width>MAX_SIDE||f->height>MAX_SIDE||(int64_t)f->width*f->height>MAX_PIXELS)die("presentation-raster-capacity");
    if(f->flags&(AV_FRAME_FLAG_CORRUPT|AV_FRAME_FLAG_DISCARD|AV_FRAME_FLAG_INTERLACED)||flagged_side_data(f)||f->crop_top||f->crop_bottom||f->crop_left||f->crop_right||f->alpha_mode==AVALPHA_MODE_PREMULTIPLIED)die("presentation-frame-metadata-unsupported");
    if(family>2&&frame_count!=0)die("presentation-still-has-multiple-frames");
    if(family<=2&&f->pts==AV_NOPTS_VALUE)die("presentation-original-pts-missing");
    if(f->time_base.num>0&&f->time_base.den>0&&av_cmp_q(f->time_base,packet_tb))die("presentation-frame-timebase-changed");
    int components,bytes,sx,sy,swap16,gray,palette;
    int format=plane_layout(f->format,family>2,&components,&bytes,&sx,&sy,&swap16,&gray,&palette);
    if(!format)die("presentation-native-format-unsupported");
    int planar=format>=5;int count=planar?3:1;
    int widths[3]={f->width,(f->width+sx-1)/sx,(f->width+sx-1)/sx};
    int heights[3]={f->height,(f->height+sy-1)/sy,(f->height+sy-1)/sy};
    int strides[3]={f->width*components*bytes,widths[1]*bytes,widths[2]*bytes};
    if(planar)strides[0]=f->width*bytes;
    uint8_t meta[META_BODY];memset(meta,0,sizeof meta);
    put64(meta,frame_count);put64(meta+8,proof->id);put64(meta+16,(uint64_t)proof->position);put32(meta+24,(uint32_t)proof->size);
    put64(meta+28,(uint64_t)f->pts);put32(meta+36,(uint32_t)packet_tb.num);put32(meta+40,(uint32_t)packet_tb.den);put64(meta+44,(uint64_t)f->duration);
    put32(meta+52,(uint32_t)f->width);put32(meta+56,(uint32_t)f->height);put32(meta+60,(uint32_t)format);
    if((f->color_primaries!=AVCOL_PRI_UNSPECIFIED&&par->color_primaries!=AVCOL_PRI_UNSPECIFIED&&f->color_primaries!=par->color_primaries)||
       (f->color_trc!=AVCOL_TRC_UNSPECIFIED&&par->color_trc!=AVCOL_TRC_UNSPECIFIED&&f->color_trc!=par->color_trc)||
       (f->colorspace!=AVCOL_SPC_UNSPECIFIED&&par->color_space!=AVCOL_SPC_UNSPECIFIED&&f->colorspace!=par->color_space)||
       (f->color_range!=AVCOL_RANGE_UNSPECIFIED&&par->color_range!=AVCOL_RANGE_UNSPECIFIED&&f->color_range!=par->color_range)||
       (f->chroma_location!=AVCHROMA_LOC_UNSPECIFIED&&par->chroma_location!=AVCHROMA_LOC_UNSPECIFIED&&f->chroma_location!=par->chroma_location))die("presentation-color-tags-conflict");
    put32(meta+64,(uint32_t)(f->color_primaries==AVCOL_PRI_UNSPECIFIED?par->color_primaries:f->color_primaries));
    put32(meta+68,(uint32_t)(f->color_trc==AVCOL_TRC_UNSPECIFIED?par->color_trc:f->color_trc));
    put32(meta+72,(uint32_t)(f->colorspace==AVCOL_SPC_UNSPECIFIED?par->color_space:f->colorspace));
    put32(meta+76,(uint32_t)(f->color_range==AVCOL_RANGE_UNSPECIFIED?par->color_range:f->color_range));
    put32(meta+80,(uint32_t)(f->chroma_location==AVCHROMA_LOC_UNSPECIFIED?par->chroma_location:f->chroma_location));put32(meta+84,(uint32_t)f->alpha_mode);
    AVRational sar=f->sample_aspect_ratio.num>0?f->sample_aspect_ratio:selected_stream_sar.num>0?selected_stream_sar:par->sample_aspect_ratio;
    if(f->sample_aspect_ratio.num>0&&f->sample_aspect_ratio.den>0&&par->sample_aspect_ratio.num>0&&par->sample_aspect_ratio.den>0&&
       av_cmp_q(f->sample_aspect_ratio,par->sample_aspect_ratio))die("presentation-sar-conflict");
    if(selected_stream_sar.num>0&&selected_stream_sar.den>0&&sar.num>0&&sar.den>0&&av_cmp_q(selected_stream_sar,sar))die("presentation-stream-sar-conflict");
    if(sar.num<0||sar.den<0||selected_stream_sar.num<0||selected_stream_sar.den<0)die("presentation-sar-invalid");
    if(sar.num==0||sar.den==0)sar=(AVRational){0,1}; /* absence is explicit; managed image/container authority decides any default */
    int n,d;av_reduce(&n,&d,sar.num,sar.den,INT_MAX);put32(meta+88,(uint32_t)n);put32(meta+92,(uint32_t)d);
    const AVPacketSideData *display=av_packet_side_data_get(par->coded_side_data,par->nb_coded_side_data,AV_PKT_DATA_DISPLAYMATRIX);
    AVFrameSideData *frame_display=av_frame_get_side_data(f,AV_FRAME_DATA_DISPLAYMATRIX);
    if(frame_display&&display&&(frame_display->size!=display->size||memcmp(frame_display->data,display->data,display->size)))die("presentation-display-matrix-conflict");
    const uint8_t *matrix=frame_display?frame_display->data:display?display->data:NULL;size_t matrix_size=frame_display?frame_display->size:display?display->size:0;
    if(matrix){if(matrix_size!=36)die("presentation-display-matrix-invalid");for(int i=0;i<9;i++){int32_t value;memcpy(&value,matrix+i*4,4);put32(meta+96+i*4,(uint32_t)value);}put32(meta+132,1);}
    put32(meta+136,(uint32_t)count);
    for(int p=0;p<count;p++){int64_t size=(int64_t)strides[p]*heights[p];if(size<=0||size>INT_MAX)die("presentation-plane-capacity");put32(meta+140+p*8,(uint32_t)strides[p]);put32(meta+144+p*8,(uint32_t)size);}
    put32(meta+164,(uint32_t)stream_id);put32(meta+168,(uint32_t)stream_index);put32(meta+172,(uint32_t)family);memcpy(meta+176,proof->hash,32);
    loaded_closure();record(2,meta,sizeof meta);
    if(!inspect_only) {
        uint8_t row[MAX_SIDE*8];
        for(int p=0;p<count;p++)for(int y=0;y<heights[p];y++) {
            if(!f->data[p])die("presentation-native-plane-missing");
            const uint8_t *input=f->data[p]+(ptrdiff_t)y*f->linesize[p];
            int expected_input=planar?widths[p]*bytes:palette?f->width:gray==3?f->width:gray?f->width*gray*bytes:strides[0];
            if(!f->data[p]||llabs((long long)f->linesize[p])<expected_input)die("presentation-native-stride-invalid");
            if(palette) {
                if(!f->data[1])die("presentation-palette-missing");
                for(int x=0;x<f->width;x++){uint32_t argb;memcpy(&argb,f->data[1]+input[x]*4,4);row[x*4]=(uint8_t)(argb>>16);row[x*4+1]=(uint8_t)(argb>>8);row[x*4+2]=(uint8_t)argb;row[x*4+3]=(uint8_t)(argb>>24);}
            } else if(gray==3) {
                if(!f->data[1]||!f->data[2]||llabs((long long)f->linesize[1])<f->width||llabs((long long)f->linesize[2])<f->width)die("presentation-gbr-stride-invalid");
                const uint8_t *blue=f->data[1]+(ptrdiff_t)y*f->linesize[1],*red=f->data[2]+(ptrdiff_t)y*f->linesize[2];
                for(int x=0;x<f->width;x++){row[3*x]=red[x];row[3*x+1]=input[x];row[3*x+2]=blue[x];}
            } else if(gray) {
                for(int x=0;x<f->width;x++)for(int c=0;c<components;c++)for(int b=0;b<bytes;b++) {
                    int input_component=c==3?1:0;row[x*components*bytes+c*bytes+b]=input[x*gray*bytes+input_component*bytes+(swap16?bytes-1-b:b)];
                }
            } else if(swap16) {for(int x=0;x<strides[p];x+=2){row[x]=input[x+1];row[x+1]=input[x];}}
            else {allwrite(1,input,(size_t)strides[p]);continue;}
            allwrite(1,row,(size_t)strides[p]);
        }
    }
    uint8_t ack;ssize_t nr;do{nr=read(0,&ack,1);}while(nr<0&&errno==EINTR);
    if(nr!=1||ack!=(inspect_only?'S':'N'))die("presentation-frame-ack-invalid");
    emitted[proof->id]=1;frame_count++;
}

int main(int argc,char **argv) {
    if(argc!=6)die("presentation-internal-arguments-invalid");
    family=atoi(argv[1]);stream_index=atoi(argv[2]);inspect_only=!strcmp(argv[3],"inspect");
    if(family<1||family>4||stream_index<0||strcmp(argv[3],inspect_only?"inspect":"decode"))die("presentation-internal-mode-invalid");
    source_fd=open(argv[4],O_RDONLY|O_CLOEXEC|O_NOFOLLOW);struct stat st;
    if(source_fd<0||fstat(source_fd,&st)||!S_ISREG(st.st_mode)||st.st_size<=0||st.st_size>MAX_SOURCE)die("presentation-source-invalid");
    source_size=st.st_size;
    if(strcmp(argv[5],"mkg-libav-same-frame-v1"))die("presentation-capability-revision-invalid");
    av_log_set_level(AV_LOG_ERROR);
    if(avformat_version()!=LIBAVFORMAT_VERSION_INT||avcodec_version()!=LIBAVCODEC_VERSION_INT||avutil_version()!=LIBAVUTIL_VERSION_INT)die("presentation-tool-abi-mismatch");
    loaded_closure();
    uint8_t *avio_buffer=av_malloc(32768);if(!avio_buffer)die("presentation-native-allocation-failed");
    AVIOContext *io=avio_alloc_context(avio_buffer,32768,0,NULL,read_packet,NULL,seek_packet);if(!io)die("presentation-io-allocation-failed");
    AVFormatContext *fmt=avformat_alloc_context();if(!fmt)die("presentation-format-allocation-failed");fmt->pb=io;fmt->flags|=AVFMT_FLAG_CUSTOM_IO|AVFMT_FLAG_NOFILLIN|AVFMT_FLAG_NOPARSE;fmt->io_open=deny_open;fmt->max_streams=128;fmt->max_probe_packets=64;
    const char *demuxer=family==1?"mov":family==2?"matroska":family==3?"png_pipe":"jpeg_pipe";
    const AVInputFormat *input=av_find_input_format(demuxer);AVDictionary *options=NULL;
    if(family==1){av_dict_set(&options,"enable_drefs","0",0);av_dict_set(&options,"use_absolute_path","0",0);av_dict_set(&options,"advanced_editlist","0",0);}
    if(!input||avformat_open_input(&fmt,NULL,input,&options)<0||av_dict_count(options))die("presentation-demux-open-failed");
    av_dict_free(&options);
    if((unsigned)stream_index>=fmt->nb_streams)die("presentation-stream-missing");
    AVStream *stream=fmt->streams[stream_index];
    AVCodecParameters *par=stream->codecpar;
    stream_id=stream->id;selected_stream_sar=stream->sample_aspect_ratio;
    if(par->codec_type!=AVMEDIA_TYPE_VIDEO)die("presentation-selected-stream-not-video");
    if((family==3&&par->codec_id!=AV_CODEC_ID_PNG)||(family==4&&par->codec_id!=AV_CODEC_ID_MJPEG)||(family<3&&(par->codec_id==AV_CODEC_ID_PNG||par->codec_id==AV_CODEC_ID_MJPEG)))die("presentation-family-codec-mismatch");
    if(flagged_packet_side_data(par->coded_side_data,par->nb_coded_side_data))die("presentation-color-side-data-unsupported");
    if(av_packet_side_data_get(par->coded_side_data,par->nb_coded_side_data,AV_PKT_DATA_FRAME_CROPPING))die("presentation-container-cropping-unsupported");
    packet_tb=stream->time_base;if(packet_tb.num<=0||packet_tb.den<=0)die("presentation-stream-timebase-invalid");
    int n,d;av_reduce(&n,&d,packet_tb.num,packet_tb.den,INT_MAX);packet_tb=(AVRational){n,d};
    const AVCodec *codec=avcodec_find_decoder_by_name(decoder_name(par->codec_id));AVCodecContext *ctx=avcodec_alloc_context3(codec);
    if(!codec||!ctx||avcodec_parameters_to_context(ctx,par)<0)die("presentation-decoder-unavailable");
    ctx->pkt_timebase=packet_tb;ctx->thread_count=1;ctx->thread_type=0;ctx->skip_frame=AVDISCARD_NONE;ctx->skip_loop_filter=AVDISCARD_NONE;ctx->skip_idct=AVDISCARD_NONE;ctx->max_pixels=MAX_PIXELS;ctx->get_format=software_format;ctx->flags|=AV_CODEC_FLAG_COPY_OPAQUE|AV_CODEC_FLAG_BITEXACT;ctx->err_recognition=AV_EF_CRCCHECK|AV_EF_EXPLODE;ctx->apply_cropping=1;
    if(avcodec_open2(ctx,codec,NULL)<0)die("presentation-decoder-open-failed");
    loaded_closure();
    AVPacket *packet=av_packet_alloc();AVFrame *frame=av_frame_alloc();if(!packet||!frame)die("presentation-native-allocation-failed");
    int finished=0,sent_flush=0;
    while(!finished) {
        int received=avcodec_receive_frame(ctx,frame);
        if(received==0){emit_frame(frame,par);av_frame_unref(frame);if(inspect_only)break;continue;}
        if(received==AVERROR_EOF){if(!sent_flush)die("presentation-early-decoder-eof");finished=1;break;}
        if(received!=AVERROR(EAGAIN)||sent_flush)die("presentation-decoder-drain-failed");
        int read_result;
        if(family>2) {
            if(packet_count){read_result=AVERROR_EOF;}
            else {
                if(source_size>64LL*1024*1024||source_size>INT_MAX||av_new_packet(packet,(int)source_size)<0||lseek(source_fd,0,SEEK_SET)<0)die("presentation-still-compressed-capacity");
                int at=0;while(at<packet->size){ssize_t n=read(source_fd,packet->data+at,(size_t)(packet->size-at));if(n<0&&errno==EINTR)continue;if(n<=0)die("presentation-still-source-truncated");at+=(int)n;}
                packet->pts=0;packet->dts=0;packet->pos=0;packet->stream_index=stream_index;read_result=0;
            }
        } else do { read_result=av_read_frame(fmt,packet);if(read_result<0)break;if(packet->stream_index!=stream_index)av_packet_unref(packet);else break; }while(1);
        if(read_result==AVERROR_EOF){if(avcodec_send_packet(ctx,NULL)<0)die("presentation-flush-send-failed");sent_flush=1;continue;}
        if(read_result<0||packet_count>=MAX_FRAMES||packet->flags&(AV_PKT_FLAG_CORRUPT|AV_PKT_FLAG_DISCARD)||packet->pos<0||packet->size<=0)die("presentation-packet-invalid");
        if(flagged_packet_side_data(packet->side_data,packet->side_data_elems))die("presentation-color-side-data-unsupported");
        AVBufferRef *proof=av_buffer_allocz(sizeof(PacketProof));if(!proof)die("presentation-native-allocation-failed");
        PacketProof *value=(PacketProof*)proof->data;value->id=packet_count;value->position=packet->pos;value->size=packet->size;value->pts=packet->pts;value->duration=packet->duration;sha(packet->data,(size_t)packet->size,value->hash);
        av_buffer_unref(&packet->opaque_ref);packet->opaque_ref=proof;
        if(avcodec_send_packet(ctx,packet)<0)die("presentation-packet-submit-failed");
        packet_count++;av_packet_unref(packet);
    }
    if(!inspect_only){if(!finished||packet_count!=frame_count)die("presentation-packet-frame-count-mismatch");for(uint64_t i=0;i<packet_count;i++)if(!emitted[i])die("presentation-packet-not-presented");}
    loaded_closure();uint8_t complete[24]={0};put64(complete,frame_count);put64(complete+8,packet_count);put32(complete+16,(uint32_t)!inspect_only);record(3,complete,sizeof complete);
    av_frame_free(&frame);av_packet_free(&packet);avcodec_free_context(&ctx);avformat_close_input(&fmt);av_freep(&io->buffer);avio_context_free(&io);close(source_fd);return 0;
}
