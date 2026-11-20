/* Source-level regression draft. It compiles the actual helper guard functions
 * into this translation unit without invoking the helper entrypoint. */
#ifdef _WIN32
#define wmain mkg_presentation_entrypoint
#else
#define main mkg_presentation_entrypoint
#endif
#include "../mkg_presentation.c"
#undef main
#undef wmain

static void check(int condition,const char *detail) {
    if(!condition) { fprintf(stderr,"side-data-test-failed: %s\n",detail); exit(1); }
}

#ifdef _WIN32
int wmain(void) {
    configure_av();
#else
int main(void) {
#endif
    static const enum AVFrameSideDataType frame_types[] = {
        AV_FRAME_DATA_ICC_PROFILE,AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
        AV_FRAME_DATA_CONTENT_LIGHT_LEVEL,AV_FRAME_DATA_DYNAMIC_HDR_PLUS,
        AV_FRAME_DATA_DYNAMIC_HDR_VIVID,AV_FRAME_DATA_DOVI_RPU_BUFFER,
        AV_FRAME_DATA_DOVI_METADATA,AV_FRAME_DATA_DYNAMIC_HDR_SMPTE_2094_APP5,
        AV_FRAME_DATA_AMBIENT_VIEWING_ENVIRONMENT,AV_FRAME_DATA_RAW_COLOR_PARAMS
    };
    static const enum AVPacketSideDataType packet_types[] = {
        AV_PKT_DATA_ICC_PROFILE,AV_PKT_DATA_MASTERING_DISPLAY_METADATA,
        AV_PKT_DATA_CONTENT_LIGHT_LEVEL,AV_PKT_DATA_DOVI_CONF,
        AV_PKT_DATA_DYNAMIC_HDR10_PLUS,AV_PKT_DATA_DYNAMIC_HDR_SMPTE_2094_APP5,
        AV_PKT_DATA_AMBIENT_VIEWING_ENVIRONMENT,AV_PKT_DATA_HEVC_CONF
    };
    AVFrame *frame=av_frame_alloc();check(frame!=NULL,"frame allocation");
    frame->color_primaries=AVCOL_PRI_BT709;frame->color_trc=AVCOL_TRC_IEC61966_2_1;
    frame->colorspace=AVCOL_SPC_RGB;frame->color_range=AVCOL_RANGE_JPEG;
    check(!flagged_side_data(frame),"plain SDR frame");
    for(size_t i=0;i<sizeof(frame_types)/sizeof(frame_types[0]);i++) {
        check(av_frame_new_side_data(frame,frame_types[i],1)!=NULL,"side-data allocation");
        check(flagged_side_data(frame),"explicit unsupported frame interpretation with SDR tags");
        av_frame_remove_side_data(frame,frame_types[i]);
        check(!flagged_side_data(frame),"frame removal restores clean SDR");
    }
    av_frame_free(&frame);
    AVPacketSideData *side_data=NULL;int count=0;
    check(!flagged_packet_side_data(side_data,count),"empty packet side-data");
    for(size_t i=0;i<sizeof(packet_types)/sizeof(packet_types[0]);i++) {
        check(av_packet_side_data_new(&side_data,&count,packet_types[i],1,0)!=NULL,"packet side-data allocation");
        check(flagged_packet_side_data(side_data,count),"explicit unsupported coded or packet interpretation");
        av_packet_side_data_free(&side_data,&count);
        check(!flagged_packet_side_data(side_data,count),"packet removal restores empty side-data");
    }
    return 0;
}
