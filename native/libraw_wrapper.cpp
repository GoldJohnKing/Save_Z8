// native/libraw_wrapper.c — Minimal C wrapper around LibRaw for P/Invoke from C#
// Compile: cl /LD /O2 libraw_wrapper.c /I <libraw_include> /link <libraw_lib>
// Or add to VS project as a native C project targeting the same platform.

#include <stdlib.h>
#include <string.h>
// Include LibRaw headers (adjust path to your LibRaw installation)
#include "libraw/libraw.h"

typedef struct {
    LibRaw *lr;
    unsigned short *raw_image;   // pointer into LibRaw's internal buffer
    int width, height;
    int black_level, white_level;
    int colors, filters;
} lr_handle_t;

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT
#endif

EXPORT void* lr_open(const char* filepath, int* error) {
    *error = 0;
    lr_handle_t *h = (lr_handle_t*)calloc(1, sizeof(lr_handle_t));
    if (!h) { *error = -1; return NULL; }

    h->lr = new LibRaw();
    if (!h->lr) { free(h); *error = -1; return NULL; }

    int ret = h->lr->open_file(filepath);
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

    // Get dimensions
    h->width = h->lr->imgdata.sizes.raw_width;
    h->height = h->lr->imgdata.sizes.raw_height;

    // Get raw image data (mosaic, u16, pre-black-subtraction)
    h->raw_image = h->lr->imgdata.rawdata.raw_image;
    if (!h->raw_image) {
        delete h->lr; free(h);
        *error = -2; // no raw image
        return NULL;
    }

    // Get metadata
    h->black_level = h->lr->imgdata.color.black;
    h->white_level = h->lr->imgdata.color.maximum;
    h->colors = h->lr->imgdata.idata.colors;
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

EXPORT int lr_get_filters(void* handle) {
    return ((lr_handle_t*)handle)->filters;
}

EXPORT int lr_get_cfa_pattern(void* handle, unsigned char* out_pattern) {
    lr_handle_t *h = (lr_handle_t*)handle;
    // filters is a 32-bit packed CFA description
    // For RGGB: filters = 0x94949494 (or similar based on LibRaw encoding)
    // We decode it to the standard 2x2 pattern: 0=R, 1=G, 2=B
    unsigned int f = (unsigned int)h->filters;
    // LibRaw's FC() macro: (filters >> (((y << 1 & 14) + (x & 1)) << 1) & 3)
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
