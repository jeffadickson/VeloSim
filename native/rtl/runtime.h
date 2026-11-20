#pragma once
#include <bit>
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>
#if defined(__SSE2__) || defined(_M_X64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
#include <immintrin.h>
#define VSRTL_HAS_SSE2 1
#else
#define VSRTL_HAS_SSE2 0
#endif
#if (defined(__x86_64__) || defined(__i386__)) && (defined(__GNUC__) || defined(__clang__))
#define VSRTL_HAS_AVX2_DISPATCH 1
#else
#define VSRTL_HAS_AVX2_DISPATCH 0
#endif
namespace vsrtl {
using Bits = unsigned __int128;
struct W { Bits v, x, z; int w; };

inline constexpr uint64_t mask64_unchecked(int w) noexcept {
 return w == 64 ? ~uint64_t(0) : (w == 0 ? uint64_t(0) : (uint64_t(1) << w) - 1);
}
inline constexpr Bits mask_unchecked(int w) noexcept {
 return w == 128 ? ~Bits(0) : (w == 0 ? Bits(0) : (Bits(1) << w) - 1);
}
inline constexpr Bits mask(int w) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 return mask_unchecked(w);
}
inline constexpr W lit(uint64_t v, int w) noexcept {
 return W{Bits(v), 0, 0, w};
}
inline constexpr W lit(uint64_t vlo, uint64_t vhi, uint64_t xlo, uint64_t xhi, uint64_t zlo, uint64_t zhi, int w) noexcept {
 return W{(Bits(vhi) << 64) | vlo, (Bits(xhi) << 64) | xlo, (Bits(zhi) << 64) | zlo, w};
}
inline W word(Bits v, Bits x, Bits z, int w) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 if (w <= 64) {
  const uint64_t m = mask64_unchecked(w);
  const uint64_t zv = uint64_t(z) & m;
  const uint64_t xv = uint64_t(x) & ~zv & m;
  const uint64_t vv = uint64_t(v) & ~(xv | zv) & m;
  return W{vv, xv, zv, w};
 }
 const Bits m = mask_unchecked(w);
 return W{v & ~(x | z) & m, x & ~z & m, z & m, w};
}
inline W resize(W a, int w) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 if (w <= 64) {
  const uint64_t m = mask64_unchecked(w);
  const uint64_t zv = uint64_t(a.z) & m;
  const uint64_t xv = uint64_t(a.x) & ~zv & m;
  const uint64_t vv = uint64_t(a.v) & ~(xv | zv) & m;
  return W{vv, xv, zv, w};
 }
 const Bits m = mask_unchecked(w);
 return W{a.v & ~(a.x | a.z) & m, a.x & ~a.z & m, a.z & m, w};
}
inline W unknown(int w) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 return W{0, mask_unchecked(w), 0, w};
}
inline W two_state(W value) {
 if (value.w < 0 || value.w > 128) throw std::invalid_argument("word width");
 if (value.w <= 64) {
  const uint64_t m = mask64_unchecked(value.w);
  return W{uint64_t(value.v) & m, 0, 0, value.w};
 }
 return W{value.v & mask_unchecked(value.w), 0, 0, value.w};
}

namespace simd {
inline bool avx2_available() noexcept {
#if VSRTL_HAS_AVX2_DISPATCH
 static const bool supported = __builtin_cpu_supports("avx2");
 return supported;
#else
 return false;
#endif
}
#if VSRTL_HAS_SSE2
inline __m128i load128(Bits b) noexcept {
 __m128i r;
 std::memcpy(&r, &b, sizeof(r));
 return r;
}
inline Bits store128(__m128i r) noexcept {
 Bits b;
 std::memcpy(&b, &r, sizeof(b));
 return b;
}
inline W bit_not_128(W a, int w) noexcept {
 const __m128i m = load128(mask_unchecked(w));
 const __m128i av = load128(a.v);
 const __m128i au = _mm_and_si128(_mm_or_si128(load128(a.x), load128(a.z)), m);
 const __m128i rv = _mm_andnot_si128(_mm_or_si128(av, au), m);
 return W{store128(rv), store128(au), 0, w};
}
inline W bit_binary_128(W a, W b, int w, int op) noexcept {
 const __m128i m = load128(mask_unchecked(w));
 const __m128i au = _mm_and_si128(_mm_or_si128(load128(a.x), load128(a.z)), m);
 const __m128i bu = _mm_and_si128(_mm_or_si128(load128(b.x), load128(b.z)), m);
 const __m128i av = _mm_andnot_si128(au, _mm_and_si128(load128(a.v), m));
 const __m128i bv = _mm_andnot_si128(bu, _mm_and_si128(load128(b.v), m));
 if (op == 2) {
  const __m128i ru = _mm_or_si128(au, bu);
  const __m128i rv = _mm_andnot_si128(ru, _mm_xor_si128(av, bv));
  return W{store128(rv), store128(ru), 0, w};
 }
 const __m128i ones = _mm_set1_epi32(-1);
 const __m128i az = _mm_xor_si128(_mm_or_si128(av, au), ones);
 const __m128i bz = _mm_xor_si128(_mm_or_si128(bv, bu), ones);
 __m128i rv, ru;
 if (op == 0) {
  rv = _mm_and_si128(av, bv);
  ru = _mm_andnot_si128(_mm_or_si128(_mm_or_si128(az, bz), rv), m);
 } else {
  rv = _mm_or_si128(av, bv);
  ru = _mm_andnot_si128(_mm_or_si128(rv, _mm_and_si128(az, bz)), m);
 }
 return W{store128(rv), store128(ru), 0, w};
}
inline W conditional_merge_128(W a, W b, int w) noexcept {
 const __m128i m = load128(mask_unchecked(w));
 const __m128i av = load128(a.v), ax = load128(a.x), az = load128(a.z);
 const __m128i bv = load128(b.v), bx = load128(b.x), bz = load128(b.z);
 const __m128i diff = _mm_or_si128(_mm_or_si128(_mm_xor_si128(av, bv), _mm_xor_si128(ax, bx)), _mm_xor_si128(az, bz));
 const __m128i same = _mm_andnot_si128(diff, m);
 const __m128i rv = _mm_and_si128(av, same);
 const __m128i rx = _mm_or_si128(_mm_and_si128(ax, same), _mm_and_si128(diff, m));
 const __m128i rz = _mm_and_si128(az, same);
 return W{store128(rv), store128(rx), store128(rz), w};
}
#endif
#if VSRTL_HAS_AVX2_DISPATCH
__attribute__((target("avx2")))
inline void bit_binary_u64_avx2(const uint64_t* av, const uint64_t* ax,
                                const uint64_t* bv, const uint64_t* bx,
                                uint64_t* rv, uint64_t* rx, size_t n, int op, uint64_t m) noexcept {
 const __m256i vm = _mm256_set1_epi64x(static_cast<long long>(m));
 const __m256i ones = _mm256_set1_epi32(-1);
 size_t i = 0;
 for (; i + 4 <= n; i += 4) {
  const __m256i au = _mm256_and_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(ax + i)), vm);
  const __m256i bu = _mm256_and_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(bx + i)), vm);
  const __m256i a = _mm256_andnot_si256(au, _mm256_and_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(av + i)), vm));
  const __m256i b = _mm256_andnot_si256(bu, _mm256_and_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(bv + i)), vm));
  __m256i ov, ou;
  if (op == 2) {
   ou = _mm256_or_si256(au, bu);
   ov = _mm256_andnot_si256(ou, _mm256_xor_si256(a, b));
  } else {
   const __m256i az = _mm256_xor_si256(_mm256_or_si256(a, au), ones);
   const __m256i bz = _mm256_xor_si256(_mm256_or_si256(b, bu), ones);
   if (op == 0) {
    ov = _mm256_and_si256(a, b);
    ou = _mm256_andnot_si256(_mm256_or_si256(_mm256_or_si256(az, bz), ov), vm);
   } else {
    ov = _mm256_or_si256(a, b);
    ou = _mm256_andnot_si256(_mm256_or_si256(ov, _mm256_and_si256(az, bz)), vm);
   }
  }
  _mm256_storeu_si256(reinterpret_cast<__m256i*>(rv + i), ov);
  _mm256_storeu_si256(reinterpret_cast<__m256i*>(rx + i), ou);
 }
 for (; i < n; ++i) {
  const uint64_t au = ax[i] & m, bu = bx[i] & m;
  const uint64_t a = av[i] & ~au & m, b = bv[i] & ~bu & m;
  if (op == 2) {
   const uint64_t ou = au | bu;
   rx[i] = ou;
   rv[i] = (a ^ b) & ~ou;
  } else {
   const uint64_t az = ~(a | au), bz = ~(b | bu);
   if (op == 0) {
    const uint64_t ov = a & b;
    rv[i] = ov;
    rx[i] = ~(az | bz | ov) & m;
   } else {
    const uint64_t ov = a | b;
    rv[i] = ov;
    rx[i] = ~(ov | (az & bz)) & m;
   }
  }
 }
}
__attribute__((target("avx2")))
inline void add_sub_u64_avx2(const uint64_t* av, const uint64_t* au,
                             const uint64_t* bv, const uint64_t* bu,
                             uint64_t* rv, uint64_t* rx, size_t n, bool subtract, uint64_t m) noexcept {
 const __m256i vm = _mm256_set1_epi64x(static_cast<long long>(m));
 const __m256i zero = _mm256_setzero_si256();
 size_t i = 0;
 for (; i + 4 <= n; i += 4) {
  const __m256i a = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(av + i));
  const __m256i b = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(bv + i));
  const __m256i unk = _mm256_or_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(au + i)),
                                      _mm256_loadu_si256(reinterpret_cast<const __m256i*>(bu + i)));
  const __m256i known = _mm256_cmpeq_epi64(unk, zero);
  const __m256i sum = subtract ? _mm256_sub_epi64(a, b) : _mm256_add_epi64(a, b);
  _mm256_storeu_si256(reinterpret_cast<__m256i*>(rv + i), _mm256_and_si256(_mm256_and_si256(sum, vm), known));
  _mm256_storeu_si256(reinterpret_cast<__m256i*>(rx + i), _mm256_andnot_si256(known, vm));
 }
 for (; i < n; ++i) {
  if (au[i] | bu[i]) { rv[i] = 0; rx[i] = m; }
  else { rv[i] = (subtract ? av[i] - bv[i] : av[i] + bv[i]) & m; rx[i] = 0; }
 }
}
#endif
inline void bit_binary_u64_batch(const uint64_t* av, const uint64_t* ax,
                                 const uint64_t* bv, const uint64_t* bx,
                                 uint64_t* rv, uint64_t* rx, size_t n, int op, uint64_t m) noexcept {
#if VSRTL_HAS_AVX2_DISPATCH
 if (n >= 4 && avx2_available()) {
  bit_binary_u64_avx2(av, ax, bv, bx, rv, rx, n, op, m);
  return;
 }
#endif
 for (size_t i = 0; i < n; ++i) {
  const uint64_t au = ax[i] & m, bu = bx[i] & m;
  const uint64_t a = av[i] & ~au & m, b = bv[i] & ~bu & m;
  if (op == 2) {
   const uint64_t ou = au | bu;
   rx[i] = ou;
   rv[i] = (a ^ b) & ~ou;
  } else {
   const uint64_t az = ~(a | au), bz = ~(b | bu);
   if (op == 0) {
    const uint64_t ov = a & b;
    rv[i] = ov;
    rx[i] = ~(az | bz | ov) & m;
   } else {
    const uint64_t ov = a | b;
    rv[i] = ov;
    rx[i] = ~(ov | (az & bz)) & m;
   }
  }
 }
}
inline void add_sub_u64_batch(const uint64_t* av, const uint64_t* au,
                              const uint64_t* bv, const uint64_t* bu,
                              uint64_t* rv, uint64_t* rx, size_t n, bool subtract, uint64_t m) noexcept {
#if VSRTL_HAS_AVX2_DISPATCH
 if (n >= 4 && avx2_available()) {
  add_sub_u64_avx2(av, au, bv, bu, rv, rx, n, subtract, m);
  return;
 }
#endif
 for (size_t i = 0; i < n; ++i) {
  if (au[i] | bu[i]) { rv[i] = 0; rx[i] = m; }
  else { rv[i] = (subtract ? av[i] - bv[i] : av[i] + bv[i]) & m; rx[i] = 0; }
 }
}
} // namespace simd

inline W memory_index(W index, int lower) {
 if (lower < 0) throw std::invalid_argument("negative memory lower bound");
 if ((index.x | index.z) || index.v < Bits(lower)) return unknown(1);
 return word(index.v - Bits(lower), 0, 0, index.w);
}
inline bool edge_event(W before, W after, bool positive) {
 if (before.w != 1 || after.w != 1) throw std::invalid_argument("event requires scalar values");
 const int a = before.x ? 2 : before.z ? 3 : before.v ? 1 : 0;
 const int b = after.x ? 2 : after.z ? 3 : after.v ? 1 : 0;
 return positive ? ((a == 0 && b != 0) || (a >= 2 && b == 1))
                 : ((a == 1 && b != 1) || (a >= 2 && b == 0));
}
inline W select_bit(W a, W index) noexcept {
 if ((index.x | index.z) || index.v >= uint64_t(a.w)) return W{0, 1, 0, 1};
 const unsigned n = unsigned(index.v);
 if (a.w <= 64) {
  return W{(uint64_t(a.v) >> n) & 1u, (uint64_t(a.x) >> n) & 1u, (uint64_t(a.z) >> n) & 1u, 1};
 }
 return W{(a.v >> n) & 1u, (a.x >> n) & 1u, (a.z >> n) & 1u, 1};
}
inline W select_part(W a, int left, int right) {
 const int w = left - right + 1;
 if (right < 0 || left < right || w < 1 || w > 128) throw std::invalid_argument("part select range");
 if (right >= a.w) return W{0, mask_unchecked(w), 0, w};
 const int avail = a.w - right;
 const int valid = avail < w ? avail : w;
 if (a.w <= 64 && w <= 64) {
  const uint64_t vm = mask64_unchecked(valid);
  const uint64_t oob = mask64_unchecked(w) & ~vm;
  return W{(uint64_t(a.v) >> right) & vm, ((uint64_t(a.x) >> right) & vm) | oob, (uint64_t(a.z) >> right) & vm, w};
 }
 const Bits vm = mask_unchecked(valid);
 const Bits oob = mask_unchecked(w) & ~vm;
 return W{(a.v >> right) & vm, ((a.x >> right) & vm) | oob, (a.z >> right) & vm, w};
}
inline W select_indexed(W a, W base, int width, bool descending) {
 if (width < 1 || width > 128) throw std::invalid_argument("indexed select width must be1..128");
 if (base.x | base.z) return W{0, mask_unchecked(width), 0, width};
 if (base.v > 2147483647ULL) throw std::out_of_range("known indexed base exceeds qualified range0..INT32_MAX");
 const int64_t lo = descending ? int64_t(base.v) - (width - 1) : int64_t(base.v);
 const int64_t src_lo = lo < 0 ? 0 : lo;
 const int64_t src_hi = (lo + width) < a.w ? (lo + width) : a.w;
 if (src_lo >= src_hi) return W{0, mask_unchecked(width), 0, width};
 const int count = int(src_hi - src_lo);
 const int dst_lo = int(src_lo - lo);
 const int s_lo = int(src_lo);
 if (a.w <= 64 && width <= 64) {
  const uint64_t cm = mask64_unchecked(count);
  const uint64_t dst_mask = cm << dst_lo;
  return W{((uint64_t(a.v) >> s_lo) & cm) << dst_lo,
           (((uint64_t(a.x) >> s_lo) & cm) << dst_lo) | (mask64_unchecked(width) & ~dst_mask),
           ((uint64_t(a.z) >> s_lo) & cm) << dst_lo,
           width};
 }
 const Bits cm = mask_unchecked(count);
 const Bits dst_mask = cm << dst_lo;
 return W{((a.v >> s_lo) & cm) << dst_lo,
          (((a.x >> s_lo) & cm) << dst_lo) | (mask_unchecked(width) & ~dst_mask),
          ((a.z >> s_lo) & cm) << dst_lo,
          width};
}

inline W parse_span(const char* data, size_t size) {
 if (size == 0 || size > 128) throw std::runtime_error("invalid word");
 if (size <= 64) {
  uint64_t v = 0, x = 0, z = 0;
  for (size_t i = 0; i < size; ++i) {
   const char c = data[i];
   v <<= 1; x <<= 1; z <<= 1;
   if (c == '1') v |= 1;
   else if (c == 'x') x |= 1;
   else if (c == 'z') z |= 1;
   else if (c != '0') throw std::runtime_error("invalid digit");
  }
  return W{v, x, z, int(size)};
 }
 Bits v = 0, x = 0, z = 0;
 for (size_t i = 0; i < size; ++i) {
  const char c = data[i];
  v <<= 1; x <<= 1; z <<= 1;
  if (c == '1') v |= 1;
  else if (c == 'x') x |= 1;
  else if (c == 'z') z |= 1;
  else if (c != '0') throw std::runtime_error("invalid digit");
 }
 return W{v, x, z, int(size)};
}
inline W parse(const std::string& text) {
 return parse_span(text.data(), text.size());
}
inline void append_text(std::string& out, W a) {
 const size_t offset = out.size();
 out.resize(offset + size_t(a.w));
 char* dst = out.data() + offset;
 if (a.w <= 64) {
  const uint64_t v = uint64_t(a.v), x = uint64_t(a.x), z = uint64_t(a.z);
  for (int i = a.w - 1; i >= 0; --i) {
   const uint64_t b = uint64_t(1) << i;
   *dst++ = (z & b) ? 'z' : (x & b) ? 'x' : (v & b) ? '1' : '0';
  }
  return;
 }
 for (int i = a.w - 1; i >= 0; --i) {
  const Bits b = Bits(1) << i;
  *dst++ = (a.z & b) ? 'z' : (a.x & b) ? 'x' : (a.v & b) ? '1' : '0';
 }
}
inline std::string text(W a) {
 std::string s;
 append_text(s, a);
 return s;
}
inline int truth(W a) noexcept { return a.v ? 1 : (a.x | a.z) ? 2 : 0; }
inline bool yes(W a) noexcept { return a.v != 0; }
inline W logical_not(W a) noexcept {
 const int t = truth(a);
 return t == 2 ? W{0, 1, 0, 1} : W{uint64_t(!t), 0, 0, 1};
}
inline W logical_and(W a, W b) noexcept {
 const int l = truth(a), r = truth(b);
 return l == 0 || r == 0 ? W{0, 0, 0, 1} : l == 1 && r == 1 ? W{1, 0, 0, 1} : W{0, 1, 0, 1};
}
inline W logical_or(W a, W b) noexcept {
 const int l = truth(a), r = truth(b);
 return l == 1 || r == 1 ? W{1, 0, 0, 1} : l == 0 && r == 0 ? W{0, 0, 0, 1} : W{0, 1, 0, 1};
}
inline W bit_not(W a, int w) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 if (w <= 64) {
  const uint64_t m = mask64_unchecked(w);
  const uint64_t u = (uint64_t(a.x) | uint64_t(a.z)) & m;
  const uint64_t v = ~(uint64_t(a.v) | u) & m;
  return W{v, u, 0, w};
 }
#if VSRTL_HAS_SSE2
 return simd::bit_not_128(a, w);
#else
 a = resize(a, w);
 const Bits u = a.x | a.z;
 return word(~(a.v | u), u, 0, w);
#endif
}
inline W bit_binary(W a, W b, int w, int op) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 if (w <= 64) {
  const uint64_t m = mask64_unchecked(w);
  const uint64_t au = (uint64_t(a.x) | uint64_t(a.z)) & m;
  const uint64_t bu = (uint64_t(b.x) | uint64_t(b.z)) & m;
  const uint64_t av = uint64_t(a.v) & ~au & m;
  const uint64_t bv = uint64_t(b.v) & ~bu & m;
  if (op == 2) {
   const uint64_t u = au | bu;
   return W{(av ^ bv) & ~u, u, 0, w};
  }
  const uint64_t az = ~(av | au), bz = ~(bv | bu);
  if (op == 0) {
   const uint64_t v = av & bv;
   return W{v, ~(az | bz | v) & m, 0, w};
  }
  const uint64_t v = av | bv;
  return W{v, ~(v | (az & bz)) & m, 0, w};
 }
#if VSRTL_HAS_SSE2
 return simd::bit_binary_128(a, b, w, op);
#else
 a = resize(a, w); b = resize(b, w);
 const Bits au = a.x | a.z, bu = b.x | b.z;
 const Bits az = ~(a.v | au), bz = ~(b.v | bu);
 Bits v, u;
 if (op == 0) { v = a.v & b.v; u = ~(az | bz | v); }
 else if (op == 1) { v = a.v | b.v; u = ~(v | (az & bz)); }
 else { u = au | bu; v = (a.v ^ b.v) & ~u; }
 return word(v, u, 0, w);
#endif
}
inline bool exact(W a, W b) noexcept {
 if (a.w == b.w) return a.v == b.v && a.x == b.x && a.z == b.z;
 const int w = a.w > b.w ? a.w : b.w;
 a = resize(a, w); b = resize(b, w);
 return a.v == b.v && a.x == b.x && a.z == b.z;
}
inline W equal(W a, W b) noexcept {
 if (a.w <= 64 && b.w <= 64) {
  const uint64_t u = uint64_t(a.x) | uint64_t(a.z) | uint64_t(b.x) | uint64_t(b.z);
  if ((uint64_t(a.v) ^ uint64_t(b.v)) & ~u) return W{0, 0, 0, 1};
  return u ? W{0, 1, 0, 1} : W{1, 0, 0, 1};
 }
 const Bits u = a.x | a.z | b.x | b.z;
 if ((a.v ^ b.v) & ~u) return W{0, 0, 0, 1};
 return u ? W{0, 1, 0, 1} : W{1, 0, 0, 1};
}
inline W compare(W a, W b, int op) noexcept {
 if (a.x | a.z | b.x | b.z) return W{0, 1, 0, 1};
 if (a.w <= 64 && b.w <= 64) {
  const uint64_t av = uint64_t(a.v), bv = uint64_t(b.v);
  const bool v = op == 0 ? av < bv : op == 1 ? av <= bv : op == 2 ? av > bv : av >= bv;
  return W{uint64_t(v), 0, 0, 1};
 }
 const bool v = op == 0 ? a.v < b.v : op == 1 ? a.v <= b.v : op == 2 ? a.v > b.v : a.v >= b.v;
 return W{uint64_t(v), 0, 0, 1};
}
inline W shift(W a, W amount, int w, bool right) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 if (amount.x | amount.z) return W{0, mask_unchecked(w), 0, w};
 if (amount.v >= uint64_t(w)) return W{0, 0, 0, w};
 const unsigned n = unsigned(amount.v);
 if (w <= 64) {
  const uint64_t m = mask64_unchecked(w);
  const uint64_t av = uint64_t(a.v) & m, ax = uint64_t(a.x) & m, az = uint64_t(a.z) & m;
  return right ? W{av >> n, ax >> n, az >> n, w}
               : W{(av << n) & m, (ax << n) & m, (az << n) & m, w};
 }
 const Bits m = mask_unchecked(w);
 const Bits av = a.v & m, ax = a.x & m, az = a.z & m;
 return right ? W{av >> n, ax >> n, az >> n, w}
              : W{(av << n) & m, (ax << n) & m, (az << n) & m, w};
}
inline W reduce(W a, int op) noexcept {
 W r;
 if (a.w <= 64) {
  const uint64_t u = uint64_t(a.x) | uint64_t(a.z);
  const uint64_t av = uint64_t(a.v);
  if (op < 2) r = (~(av | u) & mask64_unchecked(a.w)) ? W{0, 0, 0, 1} : u ? W{0, 1, 0, 1} : W{1, 0, 0, 1};
  else if (op < 4) r = av ? W{1, 0, 0, 1} : u ? W{0, 1, 0, 1} : W{0, 0, 0, 1};
  else r = u ? W{0, 1, 0, 1} : W{uint64_t(std::popcount(av) & 1), 0, 0, 1};
 } else {
  const Bits u = a.x | a.z;
  if (op < 2) r = (~(a.v | u) & mask_unchecked(a.w)) ? W{0, 0, 0, 1} : u ? W{0, 1, 0, 1} : W{1, 0, 0, 1};
  else if (op < 4) r = a.v ? W{1, 0, 0, 1} : u ? W{0, 1, 0, 1} : W{0, 0, 0, 1};
  else r = u ? W{0, 1, 0, 1} : W{uint64_t((std::popcount(uint64_t(a.v)) + std::popcount(uint64_t(a.v >> 64))) & 1), 0, 0, 1};
 }
 return (op & 1) ? logical_not(r) : r;
}
inline W product(W a, W b, int w, int op) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 if (w <= 64) {
  const uint64_t m = mask64_unchecked(w);
  if (((uint64_t(a.x) | uint64_t(a.z) | uint64_t(b.x) | uint64_t(b.z)) & m)) return W{0, m, 0, w};
  const uint64_t av = uint64_t(a.v) & m, bv = uint64_t(b.v) & m;
  if (op != 0 && bv == 0) return W{0, m, 0, w};
  const uint64_t r = op == 0 ? av * bv : op == 1 ? av / bv : av % bv;
  return W{r & m, 0, 0, w};
 }
 a = resize(a, w); b = resize(b, w);
 if ((a.x | a.z | b.x | b.z) || (op != 0 && b.v == 0)) return W{0, mask_unchecked(w), 0, w};
 return W{(op == 0 ? a.v * b.v : op == 1 ? a.v / b.v : a.v % b.v) & mask_unchecked(w), 0, 0, w};
}
inline W conditional(W condition, W a, W b, int w) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 const int t = truth(condition);
 if (t == 1) return resize(a, w);
 if (t == 0) return resize(b, w);
 a = resize(a, w); b = resize(b, w);
 if (w <= 64) {
  const uint64_t m = mask64_unchecked(w);
  const uint64_t diff = (uint64_t(a.v) ^ uint64_t(b.v)) | (uint64_t(a.x) ^ uint64_t(b.x)) | (uint64_t(a.z) ^ uint64_t(b.z));
  const uint64_t same = (~diff) & m;
  return W{uint64_t(a.v) & same, (uint64_t(a.x) & same) | (diff & m), uint64_t(a.z) & same, w};
 }
#if VSRTL_HAS_SSE2
 return simd::conditional_merge_128(a, b, w);
#else
 const Bits same = ~((a.v ^ b.v) | (a.x ^ b.x) | (a.z ^ b.z));
 return word(a.v & same, (a.x & same) | ~same, a.z & same, w);
#endif
}
inline W arithmetic(W a, W b, int w, bool subtract) {
 if (w < 0 || w > 128) throw std::invalid_argument("word width");
 if (a.x | a.z | b.x | b.z) return W{0, mask_unchecked(w), 0, w};
 if (w <= 64) {
  const uint64_t m = mask64_unchecked(w);
  return W{(subtract ? uint64_t(a.v) - uint64_t(b.v) : uint64_t(a.v) + uint64_t(b.v)) & m, 0, 0, w};
 }
 return W{(subtract ? a.v - b.v : a.v + b.v) & mask_unchecked(w), 0, 0, w};
}
inline W concat(W a, W b) {
 const int total = a.w + b.w;
 if (total > 128) throw std::runtime_error("concat width");
 if (total <= 64) {
  return W{(uint64_t(a.v) << b.w) | uint64_t(b.v),
           (uint64_t(a.x) << b.w) | uint64_t(b.x),
           (uint64_t(a.z) << b.w) | uint64_t(b.z),
           total};
 }
 return W{(a.v << b.w) | b.v, (a.x << b.w) | b.x, (a.z << b.w) | b.z, total};
}
inline W read(const std::vector<W>& m, W index, int w) {
 return (index.x | index.z) || index.v >= m.size() ? W{0, mask_unchecked(w), 0, w} : m[size_t(index.v)];
}
inline void write(std::vector<W>& m, W index, W value, int w) {
 if (!(index.x | index.z) && index.v < m.size()) m[size_t(index.v)] = resize(value, w);
}

struct MemWrite { uint16_t mem_id; uint32_t index; W value; };
template <size_t InlineCap = 16>
struct MemLog {
 MemWrite inline_buf[InlineCap];
 std::vector<MemWrite> overflow;
 size_t count = 0;
 inline void push(uint16_t mem_id, size_t mem_size, W index, W value, int w) {
  if ((index.x | index.z) || index.v >= mem_size) return;
  const MemWrite entry{mem_id, uint32_t(index.v), resize(value, w)};
  if (count < InlineCap) inline_buf[count++] = entry;
  else overflow.push_back(entry);
 }
 inline void commit(std::vector<std::vector<W>>& mem) noexcept {
  for (size_t i = 0; i < count; ++i) {
   const auto& e = inline_buf[i];
   mem[e.mem_id][e.index] = e.value;
  }
  for (const auto& e : overflow) mem[e.mem_id][e.index] = e.value;
  count = 0;
  if (!overflow.empty()) overflow.clear();
 }
};
} // namespace vsrtl

namespace vsrtl {
inline W deposit_indexed(W before, W base, int width, bool descending, W value) {
 if (width < 1 || width > 128) throw std::invalid_argument("selected write width must be1..128");
 if (base.x | base.z) return before;
 if (base.v > 2147483647ULL) throw std::out_of_range("known selected write base exceeds qualified range0..INT32_MAX");
 value = resize(value, width);
 const int64_t lo = descending ? int64_t(base.v) - (width - 1) : int64_t(base.v);
 const int64_t dst_lo = lo < 0 ? 0 : lo;
 const int64_t dst_hi = (lo + width) < before.w ? (lo + width) : before.w;
 if (dst_lo >= dst_hi) return before;
 const int count = int(dst_hi - dst_lo);
 const int src_lo = int(dst_lo - lo);
 const int d_lo = int(dst_lo);
 if (before.w <= 64 && width <= 64) {
  const uint64_t cm = mask64_unchecked(count);
  const uint64_t dmask = cm << d_lo;
  before.v = (uint64_t(before.v) & ~dmask) | (((uint64_t(value.v) >> src_lo) & cm) << d_lo);
  before.x = (uint64_t(before.x) & ~dmask) | (((uint64_t(value.x) >> src_lo) & cm) << d_lo);
  before.z = (uint64_t(before.z) & ~dmask) | (((uint64_t(value.z) >> src_lo) & cm) << d_lo);
  return before;
 }
 const Bits cm = mask_unchecked(count);
 const Bits dmask = cm << d_lo;
 before.v = (before.v & ~dmask) | (((value.v >> src_lo) & cm) << d_lo);
 before.x = (before.x & ~dmask) | (((value.x >> src_lo) & cm) << d_lo);
 before.z = (before.z & ~dmask) | (((value.z >> src_lo) & cm) << d_lo);
 return before;
}
} // namespace vsrtl

namespace vsrtl {
inline W signed_resize(W a, int width) {
 if (width <= a.w) return resize(a, width);
 if (width <= 64) {
  const uint64_t fill = mask64_unchecked(width) ^ mask64_unchecked(a.w);
  const uint64_t sign = uint64_t(1) << (a.w - 1);
  const uint64_t av = uint64_t(a.v), ax = uint64_t(a.x), az = uint64_t(a.z);
  return W{av | ((av & sign) ? fill : 0ULL),
           ax | ((ax & sign) ? fill : 0ULL),
           az | ((az & sign) ? fill : 0ULL),
           width};
 }
 const Bits fill = mask_unchecked(width) ^ mask_unchecked(a.w);
 const Bits sign = Bits(1) << (a.w - 1);
 return word(a.v | ((a.v & sign) ? fill : Bits(0)),
             a.x | ((a.x & sign) ? fill : Bits(0)),
             a.z | ((a.z & sign) ? fill : Bits(0)),
             width);
}
inline bool negative(W a) noexcept {
 if (a.w <= 64) return (uint64_t(a.v) & (uint64_t(1) << (a.w - 1))) != 0;
 return (a.v & (Bits(1) << (a.w - 1))) != 0;
}
inline W signed_compare(W a, W b, int op) noexcept {
 if (a.x | a.z | b.x | b.z) return W{0, 1, 0, 1};
 const bool an = negative(a), bn = negative(b);
 if (an == bn) return compare(a, b, op);
 return W{uint64_t(op < 2 ? an : !an), 0, 0, 1};
}
inline W signed_shift(W a, W amount, int width) {
 a = signed_resize(a, width);
 if (amount.x | amount.z) return W{0, mask_unchecked(width), 0, width};
 if (width <= 64) {
  const uint64_t m = mask64_unchecked(width);
  const uint64_t sign = uint64_t(1) << (width - 1);
  const uint64_t av = uint64_t(a.v), ax = uint64_t(a.x), az = uint64_t(a.z);
  const uint64_t fillV = (av & sign) ? m : 0ULL;
  const uint64_t fillX = (ax & sign) ? m : 0ULL;
  const uint64_t fillZ = (az & sign) ? m : 0ULL;
  if (amount.v >= uint64_t(width)) return W{fillV, fillX, fillZ, width};
  const unsigned n = unsigned(amount.v);
  const uint64_t fill = m ^ mask64_unchecked(width - int(n));
  return W{(av >> n) | (fillV & fill), (ax >> n) | (fillX & fill), (az >> n) | (fillZ & fill), width};
 }
 const Bits m = mask_unchecked(width);
 const Bits sign = Bits(1) << (width - 1);
 const Bits fillV = (a.v & sign) ? m : Bits(0);
 const Bits fillX = (a.x & sign) ? m : Bits(0);
 const Bits fillZ = (a.z & sign) ? m : Bits(0);
 if (amount.v >= uint64_t(width)) return W{fillV, fillX, fillZ, width};
 const unsigned n = unsigned(amount.v);
 const Bits fill = m ^ mask_unchecked(width - int(n));
 return W{(a.v >> n) | (fillV & fill), (a.x >> n) | (fillX & fill), (a.z >> n) | (fillZ & fill), width};
}
inline W signed_product(W a, W b, int width, int op) {
 a = signed_resize(a, width); b = signed_resize(b, width);
 if (op == 0) return product(a, b, width, op);
 if ((a.x | a.z | b.x | b.z) || !b.v) return W{0, mask_unchecked(width), 0, width};
 if (width <= 64) {
  const uint64_t m = mask64_unchecked(width);
  const bool an = negative(a), bn = negative(b);
  const uint64_t av = an ? ((~uint64_t(a.v) + 1) & m) : uint64_t(a.v);
  const uint64_t bv = bn ? ((~uint64_t(b.v) + 1) & m) : uint64_t(b.v);
  const uint64_t result = op == 1 ? av / bv : av % bv;
  const bool neg = op == 1 ? (an != bn) : an;
  return W{(neg ? (~result + 1) : result) & m, 0, 0, width};
 }
 const Bits m = mask_unchecked(width);
 const bool an = negative(a), bn = negative(b);
 const Bits av = an ? ((~a.v + 1) & m) : a.v;
 const Bits bv = bn ? ((~b.v + 1) & m) : b.v;
 const Bits result = op == 1 ? av / bv : av % bv;
 const bool neg = op == 1 ? (an != bn) : an;
 return W{(neg ? (~result + 1) : result) & m, 0, 0, width};
}
} // namespace vsrtl
