// zstd (Meta, BSD; its single-file decoder, zstddeclib_impl.h) for Delirium Sandbox's .blend import: Blender 3+
// writes "compressed" .blend files as zstd frames. dz_decompress: the whole stream at once; 0 on success.
#define ZSTD_STATIC_LINKING_ONLY
#include "zstddeclib_impl.h"
#include <stdlib.h>
#include <stdint.h>

int dz_decompress(const uint8_t* src, int64_t len, uint8_t** out, int64_t* outLen)
{
    ZSTD_DStream* ds = ZSTD_createDStream();
    if (!ds) return 1;
    ZSTD_initDStream(ds);
    size_t cap = (size_t)len * 6 + (1 << 20), n = 0;
    uint8_t* buf = (uint8_t*)malloc(cap);
    ZSTD_inBuffer in = { src, (size_t)len, 0 };
    while (in.pos < in.size)
    {
        if (cap - n < (1 << 20))
        {
            cap *= 2;
            uint8_t* nb = (uint8_t*)realloc(buf, cap);
            if (!nb) { free(buf); ZSTD_freeDStream(ds); return 2; }
            buf = nb;
        }
        ZSTD_outBuffer o = { buf + n, cap - n, 0 };
        size_t r = ZSTD_decompressStream(ds, &o, &in);
        if (ZSTD_isError(r)) { free(buf); ZSTD_freeDStream(ds); return 3; }
        n += o.pos;
        if (r == 0 && in.pos >= in.size) break;
    }
    ZSTD_freeDStream(ds);
    *out = buf;
    *outLen = (int64_t)n;
    return 0;
}

void dz_free(uint8_t* p) { free(p); }
