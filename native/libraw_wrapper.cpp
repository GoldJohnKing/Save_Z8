// native/libraw_wrapper.cpp — Minimal C wrapper around LibRaw for P/Invoke from C#
// Uses wide-character paths on Windows for Unicode path support.

#include <stdlib.h>
#include <string.h>
#include "libraw/libraw.h"

typedef struct {
    LibRaw *lr;
    unsigned short *raw_image;
    int width, height;
    int black_level, white_level;
    int filters;
} lr_handle_t;

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

extern "C" {

#ifdef _WIN32
// Windows: use wide-character path (supports Chinese/Japanese/etc. in filenames)
EXPORT void* lr_open(const wchar_t* filepath, int* error) {
#else
EXPORT void* lr_open(const char* filepath, int* error) {
#endif
    *error = 0;
    lr_handle_t *h = (lr_handle_t*)calloc(1, sizeof(lr_handle_t));
    if (!h) { *error = -1; return NULL; }

    h->lr = new LibRaw();
    if (!h->lr) { free(h); *error = -1; return NULL; }

    int ret;
#ifdef _WIN32
    ret = h->lr->open_file(filepath);
#else
    ret = h->lr->open_file(filepath);
#endif

    if (ret != LIBRAW_SUCCESS) {
        delete h->lr; free(h);
        *error = ret;
        return NULL;
    }

    ret = h->lr->unpack();
    if (ret != LIBRAW_SUCCESS) {
        delete h->lr; free(h);
        *error = ret;
        return NULL;
    }

    h->width = h->lr->imgdata.sizes.raw_width;
    h->height = h->lr->imgdata.sizes.raw_height;
    h->raw_image = h->lr->imgdata.rawdata.raw_image;

    if (!h->raw_image) {
        delete h->lr; free(h);
        *error = -2;
        return NULL;
    }

    h->black_level = h->lr->imgdata.color.black;
    h->white_level = h->lr->imgdata.color.maximum;
    h->filters = h->lr->imgdata.idata.filters;

    return h;
}

EXPORT int lr_get_width(void* handle) {
    return ((lr_handle_t*)handle)->width;
}

EXPORT int lr_get_height(void* handle) {
    return ((lr_handle_t*)handle)->height;
}

EXPORT unsigned short* lr_get_raw_image(void* handle) {
    return ((lr_handle_t*)handle)->raw_image;
}

EXPORT int lr_get_black_level(void* handle) {
    return ((lr_handle_t*)handle)->black_level;
}

EXPORT int lr_get_white_level(void* handle) {
    return ((lr_handle_t*)handle)->white_level;
}

EXPORT int lr_get_cam_mul(void* handle, float* out4) {
    LibRaw *lr = ((lr_handle_t*)handle)->lr;
    for (int i = 0; i < 4; i++) out4[i] = lr->imgdata.color.cam_mul[i];
    return 0;
}

EXPORT int lr_get_cfa_pattern(void* handle, unsigned char* out_pattern) {
    lr_handle_t *h = (lr_handle_t*)handle;
    unsigned int f = (unsigned int)h->filters;
    for (int y = 0; y < 2; y++) {
        for (int x = 0; x < 2; x++) {
            int c = (f >> (((y << 1 & 14) + (x & 1)) << 1)) & 3;
            out_pattern[y * 2 + x] = (unsigned char)c;
        }
    }
    return 0;
}

EXPORT void lr_close(void* handle) {
    if (!handle) return;
    lr_handle_t *h = (lr_handle_t*)handle;
    if (h->lr) delete h->lr;
    free(h);
}

EXPORT const char* lr_version(void) {
    return LIBRAW_VERSION_STR;
}

} // extern "C"
