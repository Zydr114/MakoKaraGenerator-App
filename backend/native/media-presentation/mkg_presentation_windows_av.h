#define PRESENTATION_AVUTIL_SYMBOLS(BIND) \
    BIND(av_buffer_allocz) BIND(av_buffer_unref) BIND(av_dict_count) BIND(av_dict_free) \
    BIND(av_dict_set) BIND(av_frame_alloc) BIND(av_frame_free) BIND(av_frame_get_side_data) \
    BIND(av_frame_new_side_data) BIND(av_frame_remove_side_data) BIND(av_frame_unref) \
    BIND(av_free) BIND(av_freep) BIND(av_log_set_level) BIND(av_malloc) BIND(av_pix_fmt_desc_get) \
    BIND(av_reduce) BIND(av_sha_alloc) BIND(av_sha_final) BIND(av_sha_init) BIND(av_sha_update) BIND(avutil_version)
#define PRESENTATION_AVCODEC_SYMBOLS(BIND) \
    BIND(av_new_packet) BIND(av_packet_alloc) BIND(av_packet_free) BIND(av_packet_side_data_free) \
    BIND(av_packet_side_data_get) BIND(av_packet_side_data_new) BIND(av_packet_unref) \
    BIND(avcodec_alloc_context3) BIND(avcodec_find_decoder_by_name) BIND(avcodec_free_context) \
    BIND(avcodec_open2) BIND(avcodec_parameters_to_context) BIND(avcodec_receive_frame) \
    BIND(avcodec_send_packet) BIND(avcodec_version)
#define PRESENTATION_AVFORMAT_SYMBOLS(BIND) \
    BIND(av_find_input_format) BIND(av_read_frame) BIND(avformat_alloc_context) \
    BIND(avformat_close_input) BIND(avformat_open_input) BIND(avformat_version) \
    BIND(avio_alloc_context) BIND(avio_context_free)

#define DECLARE_SYMBOL(name) static __typeof__(name) *loaded_##name;
PRESENTATION_AVUTIL_SYMBOLS(DECLARE_SYMBOL)
PRESENTATION_AVCODEC_SYMBOLS(DECLARE_SYMBOL)
PRESENTATION_AVFORMAT_SYMBOLS(DECLARE_SYMBOL)
#undef DECLARE_SYMBOL

static HMODULE load_local_image(wchar_t path[], size_t directory_length, const wchar_t *name) {
    wcscpy(path + directory_length, name);
    HMODULE image=LoadLibraryExW(path,NULL,LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!image)die("presentation-controlled-library-load-failed");
    return image;
}

static void load_controlled_av(void) {
    if(!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32))die("presentation-loader-search-unavailable");
    if(!LoadLibraryExW(L"apphelp.dll",NULL,LOAD_LIBRARY_SEARCH_SYSTEM32))die("presentation-loader-search-unavailable");
    wchar_t path[32768];LARGE_INTEGER size;
    CloseHandle(open_loaded_image(GetModuleHandleW(NULL),path,&size));
    wchar_t *separator=wcsrchr(path,L'\\');
    if(!separator||_countof(path)-(separator+1-path)<32)die("presentation-self-image-unavailable");
    size_t directory_length=(size_t)(separator+1-path);
    HMODULE image=load_local_image(path,directory_length,L"avutil-61.dll");
#define LOAD_SYMBOL(name) \
    loaded_##name=(__typeof__(name) *)GetProcAddress(image,#name); \
    if(!loaded_##name)die("presentation-controlled-library-symbol-missing");
    PRESENTATION_AVUTIL_SYMBOLS(LOAD_SYMBOL)
    image=load_local_image(path,directory_length,L"avcodec-63.dll");
    PRESENTATION_AVCODEC_SYMBOLS(LOAD_SYMBOL)
    image=load_local_image(path,directory_length,L"avformat-63.dll");
    PRESENTATION_AVFORMAT_SYMBOLS(LOAD_SYMBOL)
#undef LOAD_SYMBOL
}

#define av_buffer_allocz loaded_av_buffer_allocz
#define av_buffer_unref loaded_av_buffer_unref
#define av_dict_count loaded_av_dict_count
#define av_dict_free loaded_av_dict_free
#define av_dict_set loaded_av_dict_set
#define av_find_input_format loaded_av_find_input_format
#define av_frame_alloc loaded_av_frame_alloc
#define av_frame_free loaded_av_frame_free
#define av_frame_get_side_data loaded_av_frame_get_side_data
#define av_frame_new_side_data loaded_av_frame_new_side_data
#define av_frame_remove_side_data loaded_av_frame_remove_side_data
#define av_frame_unref loaded_av_frame_unref
#define av_free loaded_av_free
#define av_freep loaded_av_freep
#define av_log_set_level loaded_av_log_set_level
#define av_malloc loaded_av_malloc
#define av_new_packet loaded_av_new_packet
#define av_packet_alloc loaded_av_packet_alloc
#define av_packet_free loaded_av_packet_free
#define av_packet_side_data_free loaded_av_packet_side_data_free
#define av_packet_side_data_get loaded_av_packet_side_data_get
#define av_packet_side_data_new loaded_av_packet_side_data_new
#define av_packet_unref loaded_av_packet_unref
#define av_pix_fmt_desc_get loaded_av_pix_fmt_desc_get
#define av_read_frame loaded_av_read_frame
#define av_reduce loaded_av_reduce
#define av_sha_alloc loaded_av_sha_alloc
#define av_sha_final loaded_av_sha_final
#define av_sha_init loaded_av_sha_init
#define av_sha_update loaded_av_sha_update
#define avcodec_alloc_context3 loaded_avcodec_alloc_context3
#define avcodec_find_decoder_by_name loaded_avcodec_find_decoder_by_name
#define avcodec_free_context loaded_avcodec_free_context
#define avcodec_open2 loaded_avcodec_open2
#define avcodec_parameters_to_context loaded_avcodec_parameters_to_context
#define avcodec_receive_frame loaded_avcodec_receive_frame
#define avcodec_send_packet loaded_avcodec_send_packet
#define avcodec_version loaded_avcodec_version
#define avformat_alloc_context loaded_avformat_alloc_context
#define avformat_close_input loaded_avformat_close_input
#define avformat_open_input loaded_avformat_open_input
#define avformat_version loaded_avformat_version
#define avio_alloc_context loaded_avio_alloc_context
#define avio_context_free loaded_avio_context_free
#define avutil_version loaded_avutil_version
