#include <immintrin.h>
#include <bit>
#include <cstdint>
#include <cstdio>

// Validate every binary16 bit pattern against the IEEE binary16->binary32
// expansion, including signed zero, subnormals, infinity and NaN payloads.
static uint32_t Expand(uint16_t half) {
    uint32_t sign = uint32_t(half & 0x8000) << 16;
    uint32_t exponent = (half >> 10) & 31;
    uint32_t fraction = half & 1023;
    if (exponent == 31) return sign | 0x7f800000 | (fraction << 13) | (fraction ? 0x400000 : 0);
    if (!exponent) {
        if (!fraction) return sign;
        int shift = 0;
        while (!(fraction & 1024)) { fraction <<= 1; ++shift; }
        return sign | uint32_t(113-shift) << 23 | ((fraction & 1023) << 13);
    }
    return sign | ((exponent+112) << 23) | (fraction << 13);
}
int main() {
    for (uint32_t base = 0; base < 65536; base += 8) {
        alignas(16) uint16_t input[8];
        alignas(32) float output[8];
        for (uint32_t i = 0; i < 8; ++i) input[i] = base+i;
        _mm256_store_ps(output, _mm256_cvtph_ps(_mm_load_si128(reinterpret_cast<const __m128i*>(input))));
        for (uint32_t i = 0; i < 8; ++i) if (std::bit_cast<uint32_t>(output[i]) != Expand(input[i])) {
            std::fprintf(stderr, "half mismatch at 0x%04x\n", input[i]); return 1;
        }
    }
    std::puts("{\"binary16Patterns\":65536,\"binary32BitMatches\":65536,\"passed\":true}");
}
