#pragma once
#include <slang/numeric/SVInt.h>
#include <algorithm>
#include <cstdint>
#include <stdexcept>
#include <vector>

// Experimental, opt-in SVInt adapter. Never exported with the model SDK.
// Keep upstream SVInt unchanged so it remains a differential reference.
#if (defined(__x86_64__) || defined(__i386__)) && (defined(__GNUC__) || defined(__clang__))
#include <immintrin.h>
#define VELOSIM_AVX128_TARGET __attribute__((target("avx"), noinline))
#define VELOSIM_HAS_AVX128 1
#else
#define VELOSIM_HAS_AVX128 0
#endif

namespace vsnumeric {
enum class BitOperation { and_, or_, xor_ };
enum class Backend { scalar, automatic, avx128 };

/// CPU/OS-aware dispatch; the rest of the executable retains its baseline ISA.
inline bool avx128Available() {
#if VELOSIM_HAS_AVX128
    return __builtin_cpu_supports("avx");
#else
    return false;
#endif
}

namespace detail {
// Value and unknown planes use SVInt's published SVIntStorage representation.
// Produce canonical X (unknown=1,value=0); bitwise operations cannot produce Z.
inline void scalarWord(uint64_t av, uint64_t au, uint64_t bv, uint64_t bu,
                       BitOperation operation, uint64_t& value, uint64_t& unknown) {
    if (operation == BitOperation::xor_) {
        unknown = au | bu;
        value = (av ^ bv) & ~unknown;
    } else {
        const auto aOne = av & ~au, bOne = bv & ~bu;
        const auto aZero = ~(av | au), bZero = ~(bv | bu);
        const auto one = operation == BitOperation::and_ ? aOne & bOne : aOne | bOne;
        const auto zero = operation == BitOperation::and_ ? aZero | bZero : aZero & bZero;
        value = one;
        unknown = ~(one | zero);
    }
}

#if VELOSIM_HAS_AVX128
// AVX VEX-encoded 128-bit integer operations on pairs of uint64_t words.
// All loads are unaligned, bounded within a single plane, and precede stores.
VELOSIM_AVX128_TARGET inline size_t pairedWords(const uint64_t* av, const uint64_t* au,
                                               const uint64_t* bv, const uint64_t* bu,
                                               size_t words, BitOperation operation,
                                               uint64_t* values, uint64_t* unknowns) {
    const auto ones = _mm_set1_epi32(-1);
    size_t i = 0;
    for (; i + 1 < words; i += 2) {
        const auto a = _mm_loadu_si128(reinterpret_cast<const __m128i*>(av + i));
        const auto b = _mm_loadu_si128(reinterpret_cast<const __m128i*>(bv + i));
        const auto ax = au ? _mm_loadu_si128(reinterpret_cast<const __m128i*>(au + i)) : _mm_setzero_si128();
        const auto bx = bu ? _mm_loadu_si128(reinterpret_cast<const __m128i*>(bu + i)) : _mm_setzero_si128();
        __m128i value, unknown;
        if (operation == BitOperation::xor_) {
            unknown = _mm_or_si128(ax, bx);
            value = _mm_andnot_si128(unknown, _mm_xor_si128(a, b));
        } else {
            const auto aOne = _mm_andnot_si128(ax, a), bOne = _mm_andnot_si128(bx, b);
            const auto aZero = _mm_xor_si128(_mm_or_si128(a, ax), ones);
            const auto bZero = _mm_xor_si128(_mm_or_si128(b, bx), ones);
            value = operation == BitOperation::and_ ? _mm_and_si128(aOne, bOne) : _mm_or_si128(aOne, bOne);
            const auto zero = operation == BitOperation::and_ ? _mm_or_si128(aZero, bZero) : _mm_and_si128(aZero, bZero);
            unknown = _mm_xor_si128(_mm_or_si128(value, zero), ones);
        }
        _mm_storeu_si128(reinterpret_cast<__m128i*>(values + i), value);
        _mm_storeu_si128(reinterpret_cast<__m128i*>(unknowns + i), unknown);
    }
    return i;
}
#endif
}

/// Compare compatible equal-width operands with explicit scalar or AVX dispatch.
/// Mixed widths/sign flags are deliberately rejected instead of guessing coercion.
/// Result construction copies public SVIntStorage; no mutation of library internals.
inline slang::SVInt bitwise(const slang::SVInt& a, const slang::SVInt& b,
                            BitOperation operation, Backend backend = Backend::automatic) {
    if (a.getBitWidth() != b.getBitWidth() || a.isSigned() != b.isSigned())
        throw std::invalid_argument("SIMD prototype requires equal width and signedness");
    if (operation != BitOperation::and_ && operation != BitOperation::or_ && operation != BitOperation::xor_)
        throw std::invalid_argument("SIMD bitwise operation");
    if (backend != Backend::scalar && backend != Backend::automatic && backend != Backend::avx128)
        throw std::invalid_argument("SIMD backend");
    if (backend == Backend::avx128 && !avx128Available())
        throw std::runtime_error("AVX128 unavailable on CPU/OS or compiler target");
    const size_t words = (size_t(a.getBitWidth()) + 63) / 64;
    std::vector<uint64_t> output(words * 2);
    const auto av = a.getRawPtr(), bv = b.getRawPtr();
    const auto au = a.hasUnknown() ? av + words : nullptr;
    const auto bu = b.hasUnknown() ? bv + words : nullptr;
    size_t i = 0;
#if VELOSIM_HAS_AVX128
    if (backend != Backend::scalar && avx128Available())
        i = detail::pairedWords(av, au, bv, bu, words, operation, output.data(), output.data() + words);
#endif
    for (; i < words; ++i)
        detail::scalarWord(av[i], au ? au[i] : 0, bv[i], bu ? bu[i] : 0,
                           operation, output[i], output[i + words]);
    // Both planes must clear unused top bits, including odd-word SIMD tails.
    const unsigned tail = a.getBitWidth() % 64;
    if (tail) {
        const uint64_t mask = (uint64_t(1) << tail) - 1;
        output[words - 1] &= mask;
        output[2 * words - 1] &= mask;
    }
    const bool unknown = std::any_of(output.begin() + words, output.end(), [](auto word) { return word != 0; });
    if (words == 1 && !unknown) return slang::SVInt(a.getBitWidth(), output[0], a.isSigned());
    return slang::SVInt(slang::SVIntStorage(output.data(), a.getBitWidth(), a.isSigned(), unknown));
}
}

#undef VELOSIM_HAS_AVX128
#if defined(VELOSIM_AVX128_TARGET)
#undef VELOSIM_AVX128_TARGET
#endif
