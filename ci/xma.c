// delirium_xma_decode: one GTA IV wave of XMA2 (the Xbox's audio: whole 2048-byte packets) to 16-bit samples, with
// FFmpeg's XMA2 decoder (built for the iPhone by ci/ffmpeg-ios.sh and linked into libdelirium_ios.a; the Rust side,
// native/sound, declares it). The decoder takes what a .wav of XMA2 gives it: the XMA2WAVEFORMATEX's 34 bytes after
// the WAVEFORMATEX as extradata, 2048-byte blocks.
#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#include <stdint.h>
#include <string.h>

static void put16(uint8_t *p, unsigned v) { p[0] = v & 255; p[1] = (v >> 8) & 255; }
static void put32(uint8_t *p, unsigned v) { put16(p, v & 0xffff); put16(p + 2, v >> 16); }

int delirium_xma_decode(const uint8_t *data, int size, int channels, int rate, int16_t *out, int max)
{
    const AVCodec *codec = avcodec_find_decoder(AV_CODEC_ID_XMA2);
    if (!codec) return -2;
    AVCodecContext *c = avcodec_alloc_context3(codec);
    if (!c) return -3;
    c->sample_rate = rate;
    av_channel_layout_default(&c->ch_layout, channels);
    c->block_align = 2048;
    c->extradata = av_mallocz(34 + AV_INPUT_BUFFER_PADDING_SIZE);
    if (!c->extradata) { avcodec_free_context(&c); return -3; }
    c->extradata_size = 34;
    uint8_t *x = c->extradata;
    put16(x + 0, 1);            // NumStreams
    put32(x + 2, 0);            // ChannelMask
    put32(x + 6, 0);            // SamplesEncoded
    put32(x + 10, (unsigned)size); // BytesPerBlock
    x[30] = 0;                  // LoopCount
    x[31] = 4;                  // EncoderVersion
    put16(x + 32, (unsigned)(size / 2048 > 0 ? size / 2048 : 1)); // BlockCount
    if (avcodec_open2(c, codec, NULL) < 0) { avcodec_free_context(&c); return -4; }
    AVPacket *pkt = av_packet_alloc();
    AVFrame *fr = av_frame_alloc();
    uint8_t *buf = av_mallocz(2048 + AV_INPUT_BUFFER_PADDING_SIZE);
    int n = 0, err = 0;
    for (int at = 0; at <= size && !err; at += 2048) {
        int last = at >= size;
        if (!last) {
            int len = size - at < 2048 ? size - at : 2048;
            memset(buf, 0, 2048 + AV_INPUT_BUFFER_PADDING_SIZE);
            memcpy(buf, data + at, (size_t)len);
            pkt->data = buf;
            pkt->size = 2048;
            if (avcodec_send_packet(c, pkt) < 0) { err = -5; break; }
        } else {
            avcodec_send_packet(c, NULL); // the rest out
        }
        for (;;) {
            int r = avcodec_receive_frame(c, fr);
            if (r == AVERROR(EAGAIN) || r == AVERROR_EOF) break;
            if (r < 0) { err = -6; break; }
            int ch = fr->ch_layout.nb_channels > 0 ? fr->ch_layout.nb_channels : 1;
            for (int i = 0; i < fr->nb_samples && n < max; i++) {
                float v;
                if (fr->format == AV_SAMPLE_FMT_FLTP) v = ((const float *)fr->extended_data[0])[i];
                else if (fr->format == AV_SAMPLE_FMT_FLT) v = ((const float *)fr->extended_data[0])[i * ch];
                else if (fr->format == AV_SAMPLE_FMT_S16P) v = ((const int16_t *)fr->extended_data[0])[i] / 32768.0f;
                else v = ((const int16_t *)fr->extended_data[0])[i * ch] / 32768.0f;
                v *= 32767.0f;
                out[n++] = (int16_t)(v > 32767.0f ? 32767 : v < -32768.0f ? -32768 : v);
            }
            av_frame_unref(fr);
        }
    }
    av_free(buf);
    av_frame_free(&fr);
    av_packet_free(&pkt);
    avcodec_free_context(&c);
    return err ? err : n;
}
