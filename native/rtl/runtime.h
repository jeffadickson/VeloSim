#pragma once
#include <algorithm>
#include <array>
#include <bit>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iterator>
#include <map>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <utility>
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
// Fixed-capacity, little-endian limbs. All operators are unsigned modulo 2^256;
// HDL width/sign conversion remains explicit in the word operations below.
// Keeping storage bounded avoids heap allocation and ABI-dependent host integers.
struct Bits {
 static constexpr unsigned capacity=256;
 std::array<uint64_t,4> limb{};
 constexpr Bits(uint64_t value=0) noexcept : limb{value,0,0,0}{}
 constexpr Bits(uint64_t l0,uint64_t l1,uint64_t l2,uint64_t l3) noexcept : limb{l0,l1,l2,l3}{}
 template<class T> requires(std::is_integral_v<T>&&std::is_signed_v<T>)
 constexpr Bits(T value) noexcept : limb{uint64_t(value),value<0?UINT64_MAX:0,value<0?UINT64_MAX:0,value<0?UINT64_MAX:0}{}
 constexpr explicit operator bool()const noexcept{return (limb[0]|limb[1]|limb[2]|limb[3])!=0;}
 template<class T> requires(std::is_integral_v<T>&&!std::is_same_v<T,bool>) constexpr explicit operator T()const noexcept{return T(limb[0]);}
 friend constexpr bool operator==(const Bits&,const Bits&)=default;
 friend constexpr bool operator<(Bits a,Bits b) noexcept{for(int i=3;i>=0;--i)if(a.limb[i]!=b.limb[i])return a.limb[i]<b.limb[i];return false;}
 friend constexpr bool operator>(Bits a,Bits b) noexcept{return b<a;}
 friend constexpr bool operator<=(Bits a,Bits b) noexcept{return !(b<a);}
 friend constexpr bool operator>=(Bits a,Bits b) noexcept{return !(a<b);}
 friend constexpr Bits operator~(Bits a) noexcept{return Bits(~a.limb[0],~a.limb[1],~a.limb[2],~a.limb[3]);}
 friend constexpr Bits operator&(Bits a,Bits b) noexcept{return Bits(a.limb[0]&b.limb[0],a.limb[1]&b.limb[1],a.limb[2]&b.limb[2],a.limb[3]&b.limb[3]);}
 friend constexpr Bits operator|(Bits a,Bits b) noexcept{return Bits(a.limb[0]|b.limb[0],a.limb[1]|b.limb[1],a.limb[2]|b.limb[2],a.limb[3]|b.limb[3]);}
 friend constexpr Bits operator^(Bits a,Bits b) noexcept{return Bits(a.limb[0]^b.limb[0],a.limb[1]^b.limb[1],a.limb[2]^b.limb[2],a.limb[3]^b.limb[3]);}
 friend constexpr Bits operator+(Bits a,Bits b) noexcept{unsigned __int128 carry=0;for(int i=0;i<4;++i){carry+=static_cast<unsigned __int128>(a.limb[i])+b.limb[i];a.limb[i]=uint64_t(carry);carry>>=64;}return a;}
 friend constexpr Bits operator-(Bits a,Bits b) noexcept{return a+(~b)+Bits(1);}
 friend constexpr Bits operator<<(Bits a,uint64_t n) noexcept{Bits out;if(n>=capacity)return out;unsigned words=unsigned(n/64),shift=unsigned(n%64);for(unsigned i=words;i<4;++i){out.limb[i]=a.limb[i-words]<<shift;if(shift&&i>words)out.limb[i]|=a.limb[i-words-1]>>(64-shift);}return out;}
 friend constexpr Bits operator>>(Bits a,uint64_t n) noexcept{Bits out;if(n>=capacity)return out;unsigned words=unsigned(n/64),shift=unsigned(n%64);for(unsigned i=0;i+words<4;++i){out.limb[i]=a.limb[i+words]>>shift;if(shift&&i+words+1<4)out.limb[i]|=a.limb[i+words+1]<<(64-shift);}return out;}
 friend constexpr Bits operator*(Bits a,Bits b) noexcept{Bits out;for(int i=0;i<4;++i){unsigned __int128 carry=0;for(int j=0;i+j<4;++j){carry+=static_cast<unsigned __int128>(a.limb[i])*b.limb[j]+out.limb[i+j];out.limb[i+j]=uint64_t(carry);carry>>=64;}}return out;}
 // Each remainder is bounded by the scanned numerator prefix, so shifting it
 // before the next bit cannot overflow capacity. Small words use native limbs.
 static std::pair<Bits,Bits> divide(Bits numerator,Bits denominator){
  if(!denominator)throw std::domain_error("zero limb divisor");
  if(!(numerator.limb[2]|numerator.limb[3]|denominator.limb[2]|denominator.limb[3])){unsigned __int128 a=(static_cast<unsigned __int128>(numerator.limb[1])<<64)|numerator.limb[0],b=(static_cast<unsigned __int128>(denominator.limb[1])<<64)|denominator.limb[0];auto q=a/b,r=a%b;Bits quotient{uint64_t(q),uint64_t(q>>64),0,0},remainder{uint64_t(r),uint64_t(r>>64),0,0};return {quotient,remainder};}
  Bits quotient,remainder;for(int bit=255;bit>=0;--bit){remainder=remainder<<1;remainder.limb[0]|=(numerator.limb[bit/64]>>(bit%64))&1;if(remainder>=denominator){remainder=remainder-denominator;quotient.limb[bit/64]|=uint64_t(1)<<(bit%64);}}return {quotient,remainder};
 }
 friend Bits operator/(Bits a,Bits b){return divide(a,b).first;}friend Bits operator%(Bits a,Bits b){return divide(a,b).second;}
 constexpr Bits& operator|=(Bits b) noexcept{return *this=*this|b;}
 constexpr Bits& operator&=(Bits b) noexcept{return *this=*this&b;}
 constexpr Bits& operator<<=(uint64_t n) noexcept{return *this=*this<<n;}
 constexpr Bits& operator>>=(uint64_t n) noexcept{return *this=*this>>n;}
 Bits& operator/=(Bits b){return *this=*this/b;}
};
struct W {Bits v,x,z;int w;};

inline constexpr uint64_t mask64_unchecked(int w) noexcept {
 return w == 64 ? ~uint64_t(0) : (w == 0 ? uint64_t(0) : (uint64_t(1) << w) - 1);
}
inline constexpr Bits mask_unchecked(int w) noexcept {
 if (w <= 64) return Bits(mask64_unchecked(w), 0, 0, 0);
 if (w <= 128) return Bits(~uint64_t(0), mask64_unchecked(w - 64), 0, 0);
 if (w <= 192) return Bits(~uint64_t(0), ~uint64_t(0), mask64_unchecked(w - 128), 0);
 return Bits(~uint64_t(0), ~uint64_t(0), ~uint64_t(0), mask64_unchecked(w - 192));
}
inline constexpr Bits mask(int w) {
 if (w < 0 || w > 256) throw std::invalid_argument("word width");
 return mask_unchecked(w);
}
inline constexpr W lit(uint64_t v, int w) noexcept {
 return W{Bits(v, 0, 0, 0), Bits(), Bits(), w};
}
inline constexpr W lit(uint64_t vlo, uint64_t vhi, uint64_t xlo, uint64_t xhi, uint64_t zlo, uint64_t zhi, int w) noexcept {
 return W{Bits(vlo, vhi, 0, 0), Bits(xlo, xhi, 0, 0), Bits(zlo, zhi, 0, 0), w};
}
inline constexpr W lit256(uint64_t l0, uint64_t l1, uint64_t l2, uint64_t l3, int w) noexcept {
 return W{Bits(l0, l1, l2, l3), Bits(), Bits(), w};
}
inline bool has_xz(const W& a) noexcept {
 if (a.w <= 64) [[likely]] return (a.x.limb[0] | a.z.limb[0]) != 0;
 return ((a.x.limb[0] | a.x.limb[1] | a.x.limb[2] | a.x.limb[3] |
          a.z.limb[0] | a.z.limb[1] | a.z.limb[2] | a.z.limb[3]) != 0);
}
inline bool has_v(const W& a) noexcept {
 if (a.w <= 64) [[likely]] return a.v.limb[0] != 0;
 return (a.v.limb[0] | a.v.limb[1] | a.v.limb[2] | a.v.limb[3]) != 0;
}

// One shared budget covers startup or an entire evaluation, including nested loops.
inline void execution_step(size_t& remaining){if(!remaining)throw std::runtime_error("RTL execution budget exceeded");--remaining;}
inline unsigned repeat_count(W count,bool is_signed){if(has_xz(count)||(is_signed&&bool(count.v&(Bits(1)<<(count.w-1)))))return 0;if(count.v>20000)throw std::runtime_error("runtime repeat count exceeds execution budget");return unsigned(count.v);}
inline W word(const Bits& v,const Bits& x,const Bits& z,int w){
 if(w<0||w>256)throw std::invalid_argument("word width");
 if(w<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(w);
  const uint64_t zv=z.limb[0]&m;
  const uint64_t xv=x.limb[0]&~zv&m;
  const uint64_t vv=v.limb[0]&~(xv|zv)&m;
  return W{Bits(vv),Bits(xv),Bits(zv),w};
 }
 auto m=mask_unchecked(w);
 return {v&~(x|z)&m,x&~z&m,z&m,w};
}
inline W resize(const W& a,int w){
 if(w<0||w>256)throw std::invalid_argument("word width");
 if(w<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(w);
  const uint64_t zv=a.z.limb[0]&m;
  const uint64_t xv=a.x.limb[0]&~zv&m;
  const uint64_t vv=a.v.limb[0]&~(xv|zv)&m;
  return W{Bits(vv),Bits(xv),Bits(zv),w};
 }
 auto m=mask_unchecked(w);
 return {a.v&~(a.x|a.z)&m,a.x&~a.z&m,a.z&m,w};
}
inline W unknown(int w){
 if(w<0||w>256)throw std::invalid_argument("word width");
 return {Bits(),mask_unchecked(w),Bits(),w};
}
inline W two_state(const W& value){
 if(value.w<0||value.w>256)throw std::invalid_argument("word width");
 if(value.w<=64)[[likely]] return W{Bits(value.v.limb[0]&mask64_unchecked(value.w)),Bits(),Bits(),value.w};
 return {value.v&mask_unchecked(value.w),Bits(),Bits(),value.w};
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
#if VSRTL_HAS_AVX2_DISPATCH
__attribute__((target("avx2")))
inline void bit_binary_u64_avx2(const uint64_t* av, const uint64_t* ax,
                                const uint64_t* bv, const uint64_t* bx,
                                uint64_t* rv, uint64_t* rx, size_t n, int op, uint64_t m) noexcept {
 const __m256i vm = _mm256_set1_epi64x(static_cast<long long>(m));
 const __m256i ones = _mm256_set1_epi64x(-1LL);
 size_t i = 0;
 for (; i + 4 <= n; i += 4) {
  const __m256i au = _mm256_and_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(ax + i)), vm);
  const __m256i bu = _mm256_and_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(bx + i)), vm);
  const __m256i a = _mm256_andnot_si256(au, _mm256_and_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(av + i)), vm));
  const __m256i b = _mm256_andnot_si256(bu, _mm256_and_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(bv + i)), vm));
  if (op == 2) {
   const __m256i ou = _mm256_or_si256(au, bu);
   const __m256i ov = _mm256_andnot_si256(ou, _mm256_xor_si256(a, b));
   _mm256_storeu_si256(reinterpret_cast<__m256i*>(rv + i), ov);
   _mm256_storeu_si256(reinterpret_cast<__m256i*>(rx + i), ou);
  } else {
   const __m256i az = _mm256_xor_si256(_mm256_or_si256(a, au), ones);
   const __m256i bz = _mm256_xor_si256(_mm256_or_si256(b, bu), ones);
   __m256i ov, ou;
   if (op == 0) {
    ov = _mm256_and_si256(a, b);
    ou = _mm256_andnot_si256(_mm256_or_si256(_mm256_or_si256(az, bz), ov), vm);
   } else {
    ov = _mm256_or_si256(a, b);
    ou = _mm256_andnot_si256(_mm256_or_si256(ov, _mm256_and_si256(az, bz)), vm);
   }
   _mm256_storeu_si256(reinterpret_cast<__m256i*>(rv + i), ov);
   _mm256_storeu_si256(reinterpret_cast<__m256i*>(rx + i), ou);
  }
 }
 for (; i < n; ++i) {
  const uint64_t au = ax[i] & m, bu = bx[i] & m;
  const uint64_t a = av[i] & ~au & m, b = bv[i] & ~bu & m;
  if (op == 2) {
   const uint64_t ou = au | bu;
   rx[i] = ou; rv[i] = (a ^ b) & ~ou;
  } else {
   const uint64_t az = ~(a | au), bz = ~(b | bu);
   if (op == 0) { const uint64_t ov = a & b; rv[i] = ov; rx[i] = ~(az | bz | ov) & m; }
   else { const uint64_t ov = a | b; rv[i] = ov; rx[i] = ~(ov | (az & bz)) & m; }
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
  const __m256i u = _mm256_or_si256(_mm256_loadu_si256(reinterpret_cast<const __m256i*>(au + i)),
                                    _mm256_loadu_si256(reinterpret_cast<const __m256i*>(bu + i)));
  const __m256i known = _mm256_cmpeq_epi64(u, zero);
  const __m256i va = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(av + i));
  const __m256i vb = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(bv + i));
  const __m256i sum = subtract ? _mm256_sub_epi64(va, vb) : _mm256_add_epi64(va, vb);
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
   rx[i] = ou; rv[i] = (a ^ b) & ~ou;
  } else {
   const uint64_t az = ~(a | au), bz = ~(b | bu);
   if (op == 0) { const uint64_t ov = a & b; rv[i] = ov; rx[i] = ~(az | bz | ov) & m; }
   else { const uint64_t ov = a | b; rv[i] = ov; rx[i] = ~(ov | (az & bz)) & m; }
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

// One element policy is shared by generated HDL adapters and owning primitive
// containers. Storage remains in STL containers; HDL default/coercion is domain
// policy and must be identical on both paths.
class ElementType {
 int width_;bool two_state_;
public:
 ElementType(int width,bool two_state):width_(width),two_state_(two_state){
  if(width<1||width>256)throw std::invalid_argument("container element width");
 }
 int width()const{return width_;}
 bool is_two_state()const{return two_state_;}
 W initial()const{return two_state_?word(0,0,0,width_):unknown(width_);}
 W convert(W value)const{value=resize(value,width_);return two_state_?two_state(value):value;}
};
inline constexpr size_t container_capacity=256;
inline W memory_index(W index,int lower){
 if(lower<0)throw std::invalid_argument("negative memory lower bound");
 if(has_xz(index)||index.v<Bits(lower))return unknown(1);
 return word(index.v-Bits(lower),0,0,index.w);
}
// Flatten fixed unpacked coordinates only after checking every dimension.
// Bounds preserve declaration direction for future traversal operations; cell
// identity uses ascending physical offsets independently of that direction.
inline W array_index(const std::vector<W>& indices,const std::vector<std::pair<int,int>>& dimensions){
 if(indices.empty()||indices.size()!=dimensions.size()||indices.size()>4)throw std::invalid_argument("array index rank");
 uint64_t offset=0,product=1;
 for(size_t i=0;i<indices.size();++i){auto [left,right]=dimensions[i];if(left<0||right<0)throw std::invalid_argument("array dimension bounds");auto lower=std::min(left,right),upper=std::max(left,right);uint64_t size=uint64_t(upper)-lower+1;product*=size;if(product>256)throw std::invalid_argument("array dimension product");auto value=indices[i];if(has_xz(value)||value.v<Bits(lower)||value.v>Bits(upper))return unknown(64);offset=offset*size+uint64_t(value.v)-lower;}
 return word(offset,0,0,64);
}
inline bool edge_event(const W& before,const W& after,bool positive){
 if(before.w!=1||after.w!=1)throw std::invalid_argument("event requires scalar values");
 const int a=before.x.limb[0]?2:before.z.limb[0]?3:before.v.limb[0]?1:0;
 const int b=after.x.limb[0]?2:after.z.limb[0]?3:after.v.limb[0]?1:0;
 return positive?((a==0&&b!=0)||(a>=2&&b==1)):((a==1&&b!=1)||(a>=2&&b==0));
}
inline W select_bit(const W& a,const W& index) noexcept{
 if(has_xz(index)||index.v>=uint64_t(a.w))return W{Bits(),Bits(1),Bits(),1};
 const unsigned n=unsigned(index.v.limb[0]);
 if(a.w<=64)[[likely]] return W{Bits((a.v.limb[0]>>n)&1u),Bits((a.x.limb[0]>>n)&1u),Bits((a.z.limb[0]>>n)&1u),1};
 return word(a.v>>n,a.x>>n,a.z>>n,1);
}
inline W select_part(const W& a,int left,int right){
 const int w=left-right+1;
 if(right<0||left<right||w<1||w>256)throw std::invalid_argument("part select range");
 if(right>=a.w)return W{Bits(),mask_unchecked(w),Bits(),w};
 const int avail=a.w-right;
 const int valid=avail<w?avail:w;
 if(a.w<=64&&w<=64)[[likely]]{
  const uint64_t vm=mask64_unchecked(valid);
  const uint64_t oob=mask64_unchecked(w)&~vm;
  return W{Bits((a.v.limb[0]>>right)&vm),Bits(((a.x.limb[0]>>right)&vm)|oob),Bits((a.z.limb[0]>>right)&vm),w};
 }
 const Bits vm=mask_unchecked(valid);
 const Bits oob=mask_unchecked(w)&~vm;
 return W{(a.v>>right)&vm,((a.x>>right)&vm)|oob,(a.z>>right)&vm,w};
}
inline W select_indexed(const W& a,const W& base,int width,bool descending){
 if(width<1||width>256)throw std::invalid_argument("indexed select width must be1..256");
 if(has_xz(base))return W{Bits(),mask_unchecked(width),Bits(),width};
 if(base.v>2147483647ULL)throw std::out_of_range("known indexed base exceeds qualified range0..INT32_MAX");
 const int64_t lo=descending?int64_t(base.v.limb[0])-(width-1):int64_t(base.v.limb[0]);
 const int64_t src_lo=lo<0?0:lo;
 const int64_t src_hi=(lo+width)<a.w?(lo+width):a.w;
 if(src_lo>=src_hi)return W{Bits(),mask_unchecked(width),Bits(),width};
 const int count=int(src_hi-src_lo);
 const int dst_lo=int(src_lo-lo);
 const int s_lo=int(src_lo);
 if(a.w<=64&&width<=64)[[likely]]{
  const uint64_t cm=mask64_unchecked(count);
  const uint64_t dst_mask=cm<<dst_lo;
  return W{Bits(((a.v.limb[0]>>s_lo)&cm)<<dst_lo),
           Bits((((a.x.limb[0]>>s_lo)&cm)<<dst_lo)|(mask64_unchecked(width)&~dst_mask)),
           Bits(((a.z.limb[0]>>s_lo)&cm)<<dst_lo),
           width};
 }
 const Bits cm=mask_unchecked(count);
 const Bits dst_mask=cm<<dst_lo;
 return W{((a.v>>s_lo)&cm)<<dst_lo,
          (((a.x>>s_lo)&cm)<<dst_lo)|(mask_unchecked(width)&~dst_mask),
          ((a.z>>s_lo)&cm)<<dst_lo,
          width};
}

inline W parse_span(const char* data,size_t size){
 if(size==0||size>256)throw std::runtime_error("invalid word");
 if(size<=64)[[likely]]{
  uint64_t v=0,x=0,z=0;
  for(size_t i=0;i<size;++i){
   const char c=data[i];v<<=1;x<<=1;z<<=1;
   if(c=='1')v|=1;else if(c=='x')x|=1;else if(c=='z')z|=1;else if(c!='0')throw std::runtime_error("invalid digit");
  }
  return W{Bits(v),Bits(x),Bits(z),int(size)};
 }
 Bits v=0,x=0,z=0;
 for(size_t i=0;i<size;++i){
  const char c=data[i];v<<=1;x<<=1;z<<=1;
  if(c=='1')v|=1;else if(c=='x')x|=1;else if(c=='z')z|=1;else if(c!='0')throw std::runtime_error("invalid digit");
 }
 return W{v,x,z,int(size)};
}
inline W parse(const std::string& text){return parse_span(text.data(),text.size());}
inline void append_text(std::string& out,const W& a){
 const size_t offset=out.size();
 out.resize(offset+size_t(a.w));
 char* dst=out.data()+offset;
 if(a.w<=64)[[likely]]{
  const uint64_t v=a.v.limb[0],x=a.x.limb[0],z=a.z.limb[0];
  for(int i=a.w-1;i>=0;--i){const uint64_t b=uint64_t(1)<<i;*dst++=(z&b)?'z':(x&b)?'x':(v&b)?'1':'0';}
  return;
 }
 for(int i=a.w-1;i>=0;--i){const Bits b=Bits(1)<<i;*dst++=(a.z&b)?'z':(a.x&b)?'x':(a.v&b)?'1':'0';}
}
inline std::string text(const W& a){std::string s;append_text(s,a);return s;}
inline int truth(const W& a) noexcept{
 if(a.w<=64)[[likely]] return a.v.limb[0]?1:(a.x.limb[0]|a.z.limb[0])?2:0;
 return has_v(a)?1:has_xz(a)?2:0;
}
inline bool yes(const W& a) noexcept{return has_v(a);}
inline W logical_not(const W& a) noexcept{int t=truth(a);return t==2?W{Bits(),Bits(1),Bits(),1}:W{Bits(uint64_t(!t)),Bits(),Bits(),1};}
inline W logical_and(const W& a,const W& b) noexcept{int l=truth(a),r=truth(b);return l==0||r==0?W{Bits(),Bits(),Bits(),1}:l==1&&r==1?W{Bits(1),Bits(),Bits(),1}:W{Bits(),Bits(1),Bits(),1};}
inline W logical_or(const W& a,const W& b) noexcept{int l=truth(a),r=truth(b);return l==1||r==1?W{Bits(1),Bits(),Bits(),1}:l==0&&r==0?W{Bits(),Bits(),Bits(),1}:W{Bits(),Bits(1),Bits(),1};}
inline W bit_not(const W& a,int w){
 if(w<0||w>256)throw std::invalid_argument("word width");
 if(w<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(w);
  const uint64_t u=(a.x.limb[0]|a.z.limb[0])&m;
  const uint64_t v=~(a.v.limb[0]|u)&m;
  return W{Bits(v),Bits(u),Bits(),w};
 }
 W ar=resize(a,w);auto u=ar.x|ar.z;return word(~(ar.v|u),u,0,w);
}
inline W bit_binary(const W& a,const W& b,int w,int op){
 if(w<0||w>256)throw std::invalid_argument("word width");
 if(w<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(w);
  const uint64_t au=(a.x.limb[0]|a.z.limb[0])&m;
  const uint64_t bu=(b.x.limb[0]|b.z.limb[0])&m;
  const uint64_t av=a.v.limb[0]&~au&m;
  const uint64_t bv=b.v.limb[0]&~bu&m;
  if(op==2){const uint64_t u=au|bu;return W{Bits((av^bv)&~u),Bits(u),Bits(),w};}
  const uint64_t az=~(av|au),bz=~(bv|bu);
  if(op==0){const uint64_t v=av&bv;return W{Bits(v),Bits(~(az|bz|v)&m),Bits(),w};}
  const uint64_t v=av|bv;return W{Bits(v),Bits(~(v|(az&bz))&m),Bits(),w};
 }
 W ar=resize(a,w),br=resize(b,w);auto au=ar.x|ar.z,bu=br.x|br.z;
 auto az=~(ar.v|au),bz=~(br.v|bu);Bits v,u;
 if(op==0){v=ar.v&br.v;u=~(az|bz|v);}
 else if(op==1){v=ar.v|br.v;u=~(v|(az&bz));}
 else{u=au|bu;v=(ar.v^br.v)&~u;}
 return word(v,u,0,w);
}
inline bool exact(const W& a,const W& b){
 if(a.w<=64&&b.w<=64)[[likely]] return a.v.limb[0]==b.v.limb[0]&&a.x.limb[0]==b.x.limb[0]&&a.z.limb[0]==b.z.limb[0];
 int w=a.w>b.w?a.w:b.w;W ar=resize(a,w),br=resize(b,w);return ar.v==br.v&&ar.x==br.x&&ar.z==br.z;
}
inline W equal(const W& a,const W& b){
 if(a.w<=64&&b.w<=64)[[likely]]{
  const uint64_t u=a.x.limb[0]|a.z.limb[0]|b.x.limb[0]|b.z.limb[0];
  if((a.v.limb[0]^b.v.limb[0])&~u)return W{Bits(),Bits(),Bits(),1};
  return u?W{Bits(),Bits(1),Bits(),1}:W{Bits(1),Bits(),Bits(),1};
 }
 int w=a.w>b.w?a.w:b.w;W ar=resize(a,w),br=resize(b,w);auto u=ar.x|ar.z|br.x|br.z;if((ar.v^br.v)&~u)return {0,0,0,1};return u?unknown(1):W{1,0,0,1};
}
inline W compare(const W& a,const W& b,int op) noexcept{
 if(has_xz(a)||has_xz(b))return W{Bits(),Bits(1),Bits(),1};
 if(a.w<=64&&b.w<=64)[[likely]]{
  const uint64_t av=a.v.limb[0],bv=b.v.limb[0];
  const bool v=op==0?av<bv:op==1?av<=bv:op==2?av>bv:av>=bv;
  return W{Bits(uint64_t(v)),Bits(),Bits(),1};
 }
 bool v=op==0?a.v<b.v:op==1?a.v<=b.v:op==2?a.v>b.v:a.v>=b.v;return W{uint64_t(v),0,0,1};
}
inline W shift(const W& a,const W& amount,int w,bool right){
 if(w<0||w>256)throw std::invalid_argument("word width");
 if(has_xz(amount))return W{Bits(),mask_unchecked(w),Bits(),w};
 if(amount.v>=uint64_t(w))return W{Bits(),Bits(),Bits(),w};
 const unsigned n=unsigned(amount.v.limb[0]);
 if(w<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(w);
  const uint64_t av=a.v.limb[0]&m,ax=a.x.limb[0]&m,az=a.z.limb[0]&m;
  return right?W{Bits(av>>n),Bits(ax>>n),Bits(az>>n),w}
              :W{Bits((av<<n)&m),Bits((ax<<n)&m),Bits((az<<n)&m),w};
 }
 W ar=resize(a,w);
 return right?word(ar.v>>n,ar.x>>n,ar.z>>n,w):word(ar.v<<n,ar.x<<n,ar.z<<n,w);
}
inline W reduce(const W& a,int op){
 W r;
 if(a.w<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(a.w);
  const uint64_t u=a.x.limb[0]|a.z.limb[0];
  if(op<2)r=(~(a.v.limb[0]|u)&m)?W{Bits(),Bits(),Bits(),1}:u?W{Bits(),Bits(1),Bits(),1}:W{Bits(1),Bits(),Bits(),1};
  else if(op<4)r=a.v.limb[0]?W{Bits(1),Bits(),Bits(),1}:u?W{Bits(),Bits(1),Bits(),1}:W{Bits(),Bits(),Bits(),1};
  else r=u?W{Bits(),Bits(1),Bits(),1}:W{Bits(uint64_t(std::popcount(a.v.limb[0])&1)),Bits(),Bits(),1};
  return op%2?logical_not(r):r;
 }
 auto u=a.x|a.z;
 if(op<2)r=(~(a.v|u)&mask_unchecked(a.w))?W{0,0,0,1}:u?unknown(1):W{1,0,0,1};
 else if(op<4)r=a.v?W{1,0,0,1}:u?unknown(1):W{0,0,0,1};
 else r=u?unknown(1):W{uint64_t((std::popcount(a.v.limb[0])+std::popcount(a.v.limb[1])+std::popcount(a.v.limb[2])+std::popcount(a.v.limb[3]))&1),0,0,1};
 return op%2?logical_not(r):r;
}
inline W product(const W& a,const W& b,int w,int op){
 if(w<0||w>256)throw std::invalid_argument("word width");
 if(w<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(w);
  const uint64_t ax=(a.x.limb[0]|a.z.limb[0])&m;
  const uint64_t bx=(b.x.limb[0]|b.z.limb[0])&m;
  const uint64_t av=a.v.limb[0]&m,bv=b.v.limb[0]&m;
  if(ax|bx||(op!=0&&bv==0))return W{Bits(),Bits(m),Bits(),w};
  const uint64_t r=op==0?av*bv:op==1?av/bv:av%bv;
  return W{Bits(r&m),Bits(),Bits(),w};
 }
 W ar=resize(a,w),br=resize(b,w);if(has_xz(ar)||has_xz(br)||(op!=0&&br.v==0))return unknown(w);
 return word(op==0?ar.v*br.v:op==1?ar.v/br.v:ar.v%br.v,0,0,w);
}
inline W conditional(const W& condition,const W& a,const W& b,int w){
 if(w<0||w>256)throw std::invalid_argument("word width");
 const int t=truth(condition);
 if(t==1)return a.w==w?a:resize(a,w);
 if(t==0)return b.w==w?b:resize(b,w);
 W ar=resize(a,w),br=resize(b,w);
 if(w<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(w);
  const uint64_t diff=(ar.v.limb[0]^br.v.limb[0])|(ar.x.limb[0]^br.x.limb[0])|(ar.z.limb[0]^br.z.limb[0]);
  const uint64_t same=(~diff)&m;
  return W{Bits(ar.v.limb[0]&same),Bits((ar.x.limb[0]&same)|(diff&m)),Bits(ar.z.limb[0]&same),w};
 }
 auto same=~((ar.v^br.v)|(ar.x^br.x)|(ar.z^br.z));return word(ar.v&same,(ar.x&same)|~same,ar.z&same,w);
}
inline W arithmetic(const W& a,const W& b,int w,bool subtract){
 if(w<0||w>256)throw std::invalid_argument("word width");
 if(has_xz(a)||has_xz(b))return W{Bits(),mask_unchecked(w),Bits(),w};
 if(w<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(w);
  return W{Bits((subtract?a.v.limb[0]-b.v.limb[0]:a.v.limb[0]+b.v.limb[0])&m),Bits(),Bits(),w};
 }
 return word(subtract?a.v-b.v:a.v+b.v,0,0,w);
}
inline W concat(const W& a,const W& b){
 const int total=a.w+b.w;
 if(total>256)throw std::runtime_error("concat width");
 if(total<=64)[[likely]]{
  return W{Bits((a.v.limb[0]<<b.w)|b.v.limb[0]),
           Bits((a.x.limb[0]<<b.w)|b.x.limb[0]),
           Bits((a.z.limb[0]<<b.w)|b.z.limb[0]),
           total};
 }
 return word((a.v<<b.w)|b.v,(a.x<<b.w)|b.x,(a.z<<b.w)|b.z,total);
}
inline W read(const std::vector<W>& m,const W& index,int w){return has_xz(index)||index.v>=m.size()?W{Bits(),mask_unchecked(w),Bits(),w}:m[size_t(index.v.limb[0])];}
inline void write(std::vector<W>& m,const W& index,const W& value,int w){if(!has_xz(index)&&index.v<m.size())m[size_t(index.v.limb[0])]=value.w==w?value:resize(value,w);}

struct MemWrite { uint16_t mem_id; uint32_t index; W value; };
template <size_t InlineCap = 16>
struct MemLog {
 MemWrite inline_buf[InlineCap];
 std::vector<MemWrite> overflow;
 size_t count = 0;
 inline void push(uint16_t mem_id, size_t mem_size, const W& index, const W& value, int w) {
  if (has_xz(index) || index.v >= mem_size) return;
  const MemWrite entry{mem_id, uint32_t(index.v.limb[0]), value.w == w ? value : resize(value, w)};
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

// Dynamic storage starts empty. Allocation samples its count once and publishes
// only after all validation/allocation succeeds; model snapshots own the vector.
inline void dynamic_allocate(std::vector<W>& memory,W count,int width,bool is_two_state,bool preserve,bool is_signed){
 ElementType element(width,is_two_state);
 if(count.w<1||count.w>256)throw std::invalid_argument("dynamic array count width");
 if(has_xz(count)||(is_signed&&bool(count.v&(Bits(1)<<(count.w-1)))))throw std::invalid_argument("dynamic array count must be known and nonnegative");
 if(count.v>container_capacity)throw std::length_error("dynamic array capacity exceeds256");
 std::vector<W> next(size_t(count.v),element.initial());
 if(preserve)for(size_t i=0;i<std::min(next.size(),memory.size());++i)next[i]=memory[i];
 memory.swap(next);
}
inline W dynamic_read(const std::vector<W>& memory,const W& index,int width,bool is_two_state){
 if(has_xz(index)||index.v>=memory.size())return ElementType(width,is_two_state).initial();
 return memory[size_t(index.v)];
}
// HDL queues share owning vector snapshots with fixed/dynamic memories. The
// 256-cell ceiling is a resource guard, not SystemVerilog [$:N] truncation.
template<class Cells> inline void queue_push(Cells& memory,W value,int width,bool is_two_state,bool front){
 if(memory.size()>=container_capacity)throw std::length_error("queue capacity exceeds256");
 value=ElementType(width,is_two_state).convert(value);
 if(front)memory.insert(memory.begin(),value);else memory.push_back(value);
}
template<class Cells> inline W queue_pop(Cells& memory,int width,bool is_two_state,bool front){
 if(memory.empty())return ElementType(width,is_two_state).initial();
 W value=front?memory.front():memory.back();
 if(front)memory.erase(memory.begin());else memory.pop_back();return value;
}
inline void queue_delete(std::vector<W>& memory,W index){
 if(!has_xz(index)&&index.v<memory.size())memory.erase(memory.begin()+size_t(index.v));
}
inline void queue_write(std::vector<W>& memory,W index,W value,int width,bool is_two_state){
 if(has_xz(index)||index.v>memory.size())return;
 // Queue writes exactly one past the last cell append; farther writes do not.
 if(index.v==memory.size())queue_push(memory,value,width,is_two_state,false);
 else memory[size_t(index.v)]=ElementType(width,is_two_state).convert(value);
}
// Standard-library ordered storage owns its values and copies with snapshots.
// Flipping the sign bit maps a signed key's ordering onto unsigned Bits ordering.
using AssociativeStorage=std::map<Bits,W>;
inline Bits assoc_key(W key,int key_width,bool key_signed){return resize(key,key_width).v^(key_signed?(Bits(1)<<(key_width-1)):Bits(0));}
inline AssociativeStorage::const_iterator assoc_find(const AssociativeStorage& memory,W key,int key_width,bool key_signed){
 key=resize(key,key_width);return has_xz(key)?memory.end():memory.find(assoc_key(key,key_width,key_signed));
}
inline W assoc_read(const AssociativeStorage& memory,W key,int key_width,bool key_signed,int width,bool is_two_state){
 auto i=assoc_find(memory,key,key_width,key_signed);return i!=memory.end()?i->second:ElementType(width,is_two_state).initial();
}
inline W assoc_exists(const AssociativeStorage& memory,W key,int key_width,bool key_signed){return word(assoc_find(memory,key,key_width,key_signed)!=memory.end(),0,0,32);}
inline void assoc_write(AssociativeStorage& memory,W key,W value,int key_width,bool key_signed,int width,bool is_two_state){
 key=resize(key,key_width);if(has_xz(key))return;auto converted=ElementType(width,is_two_state).convert(value);
 auto encoded=assoc_key(key,key_width,key_signed);auto i=memory.find(encoded);
 if(i!=memory.end()){i->second=converted;return;}
 if(memory.size()>=container_capacity)throw std::length_error("associative array capacity exceeds256");
 memory.emplace(encoded,converted);
}
inline void assoc_delete(AssociativeStorage& memory,W key,int key_width,bool key_signed){
 key=resize(key,key_width);if(!has_xz(key))memory.erase(assoc_key(key,key_width,key_signed));
}
// Failed traversal preserves the caller's index. Ordered bounds also handle an
// absent starting key without requiring insertion or a custom search algorithm.
inline W assoc_traverse(const AssociativeStorage& memory,W& key,int key_width,bool key_signed,int operation){
 if(operation<0||operation>3)throw std::invalid_argument("associative traversal operation");
 if(memory.empty())return word(0,0,0,32);
 auto it=memory.cbegin();
 if(operation==1)it=std::prev(memory.cend());
 else if(operation>=2){
  auto current=resize(key,key_width);if(has_xz(current))return word(0,0,0,32);
  auto encoded=assoc_key(current,key_width,key_signed);
  if(operation==2){it=memory.upper_bound(encoded);if(it==memory.end())return word(0,0,0,32);}
  else{it=memory.lower_bound(encoded);if(it==memory.begin())return word(0,0,0,32);--it;}
 }
 key=word(it->first^(key_signed?(Bits(1)<<(key_width-1)):Bits(0)),0,0,key_width);return word(1,0,0,32);
}
}
namespace vsrtl {
inline W deposit_indexed(W before,const W& base,int width,bool descending,const W& value){
 if(width<1||width>256)throw std::invalid_argument("selected write width must be1..256");
 if(has_xz(base))return before;
 if(base.v>2147483647ULL)throw std::out_of_range("known selected write base exceeds qualified range0..INT32_MAX");
 W val=value.w==width?value:resize(value,width);
 const int64_t lo=descending?int64_t(base.v.limb[0])-(width-1):int64_t(base.v.limb[0]);
 const int64_t dst_lo=lo<0?0:lo;
 const int64_t dst_hi=(lo+width)<before.w?(lo+width):before.w;
 if(dst_lo>=dst_hi)return before;
 const int count=int(dst_hi-dst_lo);
 const int src_lo=int(dst_lo-lo);
 const int d_lo=int(dst_lo);
 if(before.w<=64&&width<=64)[[likely]]{
  const uint64_t cm=mask64_unchecked(count);
  const uint64_t dmask=cm<<d_lo;
  before.v.limb[0]=(before.v.limb[0]&~dmask)|(((val.v.limb[0]>>src_lo)&cm)<<d_lo);
  before.x.limb[0]=(before.x.limb[0]&~dmask)|(((val.x.limb[0]>>src_lo)&cm)<<d_lo);
  before.z.limb[0]=(before.z.limb[0]&~dmask)|(((val.z.limb[0]>>src_lo)&cm)<<d_lo);
  return before;
 }
 const Bits cm=mask_unchecked(count);
 const Bits dmask=cm<<d_lo;
 before.v=(before.v&~dmask)|(((val.v>>src_lo)&cm)<<d_lo);
 before.x=(before.x&~dmask)|(((val.x>>src_lo)&cm)<<d_lo);
 before.z=(before.z&~dmask)|(((val.z>>src_lo)&cm)<<d_lo);
 return before;
}
}
namespace vsrtl {
inline W signed_resize(const W& a,int width){
 if(width==a.w)return a;
 if(width<a.w)return resize(a,width);
 if(width<=64)[[likely]]{
  const uint64_t fill=mask64_unchecked(width)^mask64_unchecked(a.w);
  const uint64_t sign=uint64_t(1)<<(a.w-1);
  const uint64_t av=a.v.limb[0],ax=a.x.limb[0],az=a.z.limb[0];
  return W{Bits(av|((av&sign)?fill:0ULL)),
           Bits(ax|((ax&sign)?fill:0ULL)),
           Bits(az|((az&sign)?fill:0ULL)),
           width};
 }
 auto fill=mask_unchecked(width)^mask_unchecked(a.w),sign=Bits(1)<<(a.w-1);
 return word(a.v|((a.v&sign)?fill:Bits(0)),a.x|((a.x&sign)?fill:Bits(0)),a.z|((a.z&sign)?fill:Bits(0)),width);
}
inline bool negative(const W& a) noexcept{
 if(a.w<=64)[[likely]] return (a.v.limb[0]&(uint64_t(1)<<(a.w-1)))!=0;
 return bool(a.v&(Bits(1)<<(a.w-1)));
}
inline W signed_compare(const W& a,const W& b,int op) noexcept{
 if(has_xz(a)||has_xz(b))return W{Bits(),Bits(1),Bits(),1};
 const bool an=negative(a),bn=negative(b);
 if(an==bn)return compare(a,b,op);
 return W{Bits(uint64_t(op<2?an:!an)),Bits(),Bits(),1};
}
inline W signed_shift(const W& a,const W& amount,int width){
 W ar=signed_resize(a,width);if(has_xz(amount))return unknown(width);
 if(width<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(width);
  const uint64_t sign=uint64_t(1)<<(width-1);
  const uint64_t av=ar.v.limb[0],ax=ar.x.limb[0],az=ar.z.limb[0];
  const uint64_t fillV=(av&sign)?m:0ULL,fillX=(ax&sign)?m:0ULL,fillZ=(az&sign)?m:0ULL;
  if(amount.v>=uint64_t(width))return W{Bits(fillV),Bits(fillX),Bits(fillZ),width};
  const unsigned n=unsigned(amount.v.limb[0]);
  const uint64_t fill=m^mask64_unchecked(width-int(n));
  return W{Bits((av>>n)|(fillV&fill)),Bits((ax>>n)|(fillX&fill)),Bits((az>>n)|(fillZ&fill)),width};
 }
 auto sign=Bits(1)<<(width-1);Bits fillV=(ar.v&sign)?mask_unchecked(width):Bits(0),fillX=(ar.x&sign)?mask_unchecked(width):Bits(0),fillZ=(ar.z&sign)?mask_unchecked(width):Bits(0);
 if(amount.v>=uint64_t(width))return word(fillV,fillX,fillZ,width);
 unsigned n=unsigned(amount.v.limb[0]);auto fill=mask_unchecked(width)^mask_unchecked(width-int(n));
 return word((ar.v>>n)|(fillV&fill),(ar.x>>n)|(fillX&fill),(ar.z>>n)|(fillZ&fill),width);
}
inline W signed_product(const W& a,const W& b,int width,int op){
 if(op==0&&width<=64&&a.w==width&&b.w==width)[[likely]]{
  const uint64_t m=mask64_unchecked(width);
  if((a.x.limb[0]|a.z.limb[0]|b.x.limb[0]|b.z.limb[0])&m)return W{Bits(),Bits(m),Bits(),width};
  return W{Bits((a.v.limb[0]*b.v.limb[0])&m),Bits(),Bits(),width};
 }
 W ar=signed_resize(a,width),br=signed_resize(b,width);
 if(op==0)return product(ar,br,width,op);
 if(has_xz(ar)||has_xz(br)||!has_v(br))return unknown(width);
 if(width<=64)[[likely]]{
  const uint64_t m=mask64_unchecked(width);
  const bool an=negative(ar),bn=negative(br);
  const uint64_t av=an?((~ar.v.limb[0]+1)&m):ar.v.limb[0];
  const uint64_t bv=bn?((~br.v.limb[0]+1)&m):br.v.limb[0];
  const uint64_t result=op==1?av/bv:av%bv;
  const bool neg=op==1?(an!=bn):an;
  return W{Bits((neg?(~result+1):result)&m),Bits(),Bits(),width};
 }
 bool an=negative(ar),bn=negative(br);Bits av=an?(~ar.v+1)&mask_unchecked(width):ar.v,bv=bn?(~br.v+1)&mask_unchecked(width):br.v;
 Bits result=op==1?av/bv:av%bv;bool neg=op==1?(an!=bn):an;
 return word(neg?~result+1:result,0,0,width);
}
}

namespace vsrtl::w64 {
// Register-resident 4-state word for 1..64-bit fast-path codegen.
// Storage in Model::s/mem remains ABI-compatible 256-bit W with upper limbs zeroed.
struct W64 { uint64_t v, x, z; int w; };

inline constexpr W64 lit(uint64_t v, int w) noexcept { return W64{v, 0, 0, w}; }
inline constexpr W64 lit(uint64_t v, uint64_t x, uint64_t z, int w) noexcept { return W64{v, x, z, w}; }
inline constexpr W64 unknown(int w) noexcept { return W64{0, mask64_unchecked(w), 0, w}; }
inline W64 to(const W& a) noexcept { return W64{a.v.limb[0], a.x.limb[0], a.z.limb[0], a.w}; }
inline W from(W64 a) noexcept { return W{Bits(a.v), Bits(a.x), Bits(a.z), a.w}; }
inline void store(W& dst, W64 a) noexcept {
 dst.v.limb[0] = a.v;
 dst.x.limb[0] = a.x;
 dst.z.limb[0] = a.z;
}
inline constexpr W64 word(uint64_t v, uint64_t x, uint64_t z, int w) noexcept {
 const uint64_t m = mask64_unchecked(w);
 const uint64_t zv = z & m;
 const uint64_t xv = x & ~zv & m;
 const uint64_t vv = v & ~(xv | zv) & m;
 return W64{vv, xv, zv, w};
}
inline constexpr W64 resize(W64 a, int w) noexcept {
 if (a.w == w) return a;
 const uint64_t m = mask64_unchecked(w);
 const uint64_t zv = a.z & m;
 const uint64_t xv = a.x & ~zv & m;
 const uint64_t vv = a.v & ~(xv | zv) & m;
 return W64{vv, xv, zv, w};
}
inline constexpr W64 two_state(W64 a) noexcept {
 const uint64_t m = mask64_unchecked(a.w);
 return W64{a.v & m, 0, 0, a.w};
}
inline constexpr W64 sresize(W64 a, int w) noexcept {
 if (w == a.w) return a;
 if (w < a.w) return resize(a, w);
 const uint64_t fill = mask64_unchecked(w) ^ mask64_unchecked(a.w);
 const uint64_t sign = uint64_t(1) << (a.w - 1);
 return W64{a.v | ((a.v & sign) ? fill : 0ULL),
            a.x | ((a.x & sign) ? fill : 0ULL),
            a.z | ((a.z & sign) ? fill : 0ULL),
            w};
}
inline constexpr bool edge(W64 before, W64 after, bool positive) noexcept {
 const int a = before.x ? 2 : before.z ? 3 : before.v ? 1 : 0;
 const int b = after.x ? 2 : after.z ? 3 : after.v ? 1 : 0;
 return positive ? ((a == 0 && b != 0) || (a >= 2 && b == 1))
                 : ((a == 1 && b != 1) || (a >= 2 && b == 0));
}
inline constexpr int truth(W64 a) noexcept { return a.v ? 1 : (a.x | a.z) ? 2 : 0; }
inline constexpr bool yes(W64 a) noexcept { return a.v != 0; }
inline constexpr W64 lnot(W64 a) noexcept {
 const int t = truth(a);
 return t == 2 ? W64{0, 1, 0, 1} : W64{uint64_t(!t), 0, 0, 1};
}
inline constexpr W64 land(W64 a, W64 b) noexcept {
 const int l = truth(a), r = truth(b);
 return l == 0 || r == 0 ? W64{0, 0, 0, 1} : l == 1 && r == 1 ? W64{1, 0, 0, 1} : W64{0, 1, 0, 1};
}
inline constexpr W64 lor(W64 a, W64 b) noexcept {
 const int l = truth(a), r = truth(b);
 return l == 1 || r == 1 ? W64{1, 0, 0, 1} : l == 0 && r == 0 ? W64{0, 0, 0, 1} : W64{0, 1, 0, 1};
}
inline constexpr W64 bnot(W64 a, int w) noexcept {
 const uint64_t m = mask64_unchecked(w);
 const uint64_t u = (a.x | a.z) & m;
 return W64{~(a.v | u) & m, u, 0, w};
}
inline constexpr W64 bbin(W64 a, W64 b, int w, int op) noexcept {
 const uint64_t m = mask64_unchecked(w);
 const uint64_t au = (a.x | a.z) & m, bu = (b.x | b.z) & m;
 const uint64_t av = a.v & ~au & m, bv = b.v & ~bu & m;
 if (op == 2) { const uint64_t u = au | bu; return W64{(av ^ bv) & ~u, u, 0, w}; }
 const uint64_t az = ~(av | au), bz = ~(bv | bu);
 if (op == 0) { const uint64_t v = av & bv; return W64{v, ~(az | bz | v) & m, 0, w}; }
 const uint64_t v = av | bv;
 return W64{v, ~(v | (az & bz)) & m, 0, w};
}
inline constexpr bool exact(W64 a, W64 b) noexcept {
 return a.v == b.v && a.x == b.x && a.z == b.z;
}
inline constexpr W64 eq(W64 a, W64 b) noexcept {
 const uint64_t u = a.x | a.z | b.x | b.z;
 if ((a.v ^ b.v) & ~u) return W64{0, 0, 0, 1};
 return u ? W64{0, 1, 0, 1} : W64{1, 0, 0, 1};
}
inline constexpr W64 cmp(W64 a, W64 b, int op) noexcept {
 if (a.x | a.z | b.x | b.z) return W64{0, 1, 0, 1};
 const bool v = op == 0 ? a.v < b.v : op == 1 ? a.v <= b.v : op == 2 ? a.v > b.v : a.v >= b.v;
 return W64{uint64_t(v), 0, 0, 1};
}
inline constexpr bool neg(W64 a) noexcept { return (a.v & (uint64_t(1) << (a.w - 1))) != 0; }
inline constexpr W64 scmp(W64 a, W64 b, int op) noexcept {
 if (a.x | a.z | b.x | b.z) return W64{0, 1, 0, 1};
 const bool an = neg(a), bn = neg(b);
 if (an == bn) return cmp(a, b, op);
 return W64{uint64_t(op < 2 ? an : !an), 0, 0, 1};
}
inline constexpr W64 shift(W64 a, W64 amount, int w, bool right) noexcept {
 if (amount.x | amount.z) return unknown(w);
 if (amount.v >= uint64_t(w)) return W64{0, 0, 0, w};
 const unsigned n = unsigned(amount.v);
 const uint64_t m = mask64_unchecked(w);
 const uint64_t av = a.v & m, ax = a.x & m, az = a.z & m;
 return right ? W64{av >> n, ax >> n, az >> n, w}
              : W64{(av << n) & m, (ax << n) & m, (az << n) & m, w};
}
inline constexpr W64 sshift(W64 a, W64 amount, int w) noexcept {
 a = sresize(a, w);
 if (amount.x | amount.z) return unknown(w);
 const uint64_t m = mask64_unchecked(w);
 const uint64_t sign = uint64_t(1) << (w - 1);
 const uint64_t fv = (a.v & sign) ? m : 0ULL, fx = (a.x & sign) ? m : 0ULL, fz = (a.z & sign) ? m : 0ULL;
 if (amount.v >= uint64_t(w)) return W64{fv, fx, fz, w};
 const unsigned n = unsigned(amount.v);
 const uint64_t fill = m ^ mask64_unchecked(w - int(n));
 return W64{(a.v >> n) | (fv & fill), (a.x >> n) | (fx & fill), (a.z >> n) | (fz & fill), w};
}
inline constexpr W64 red(W64 a, int op) noexcept {
 const uint64_t m = mask64_unchecked(a.w);
 const uint64_t u = a.x | a.z;
 W64 r;
 if (op < 2) r = (~(a.v | u) & m) ? W64{0, 0, 0, 1} : u ? W64{0, 1, 0, 1} : W64{1, 0, 0, 1};
 else if (op < 4) r = a.v ? W64{1, 0, 0, 1} : u ? W64{0, 1, 0, 1} : W64{0, 0, 0, 1};
 else r = u ? W64{0, 1, 0, 1} : W64{uint64_t(std::popcount(a.v) & 1), 0, 0, 1};
 return (op & 1) ? lnot(r) : r;
}
inline constexpr W64 product(W64 a, W64 b, int w, int op) noexcept {
 const uint64_t m = mask64_unchecked(w);
 const uint64_t ax = (a.x | a.z) & m, bx = (b.x | b.z) & m;
 const uint64_t av = a.v & m, bv = b.v & m;
 if ((ax | bx) || (op != 0 && bv == 0)) return W64{0, m, 0, w};
 const uint64_t r = op == 0 ? av * bv : op == 1 ? av / bv : av % bv;
 return W64{r & m, 0, 0, w};
}
inline constexpr W64 sproduct(W64 a, W64 b, int w, int op) noexcept {
 if (a.w != w) a = sresize(a, w);
 if (b.w != w) b = sresize(b, w);
 const uint64_t m = mask64_unchecked(w);
 if (op == 0) {
  if ((a.x | a.z | b.x | b.z) & m) return W64{0, m, 0, w};
  return W64{(a.v * b.v) & m, 0, 0, w};
 }
 if ((a.x | a.z | b.x | b.z) || !b.v) return W64{0, m, 0, w};
 const bool an = neg(a), bn = neg(b);
 const uint64_t av = an ? ((~a.v + 1) & m) : a.v;
 const uint64_t bv = bn ? ((~b.v + 1) & m) : b.v;
 const uint64_t result = op == 1 ? av / bv : av % bv;
 const bool rneg = op == 1 ? (an != bn) : an;
 return W64{(rneg ? (~result + 1) : result) & m, 0, 0, w};
}
inline constexpr W64 cond(W64 c, W64 a, W64 b, int w) noexcept {
 const int t = truth(c);
 if (t == 1) return resize(a, w);
 if (t == 0) return resize(b, w);
 a = resize(a, w); b = resize(b, w);
 const uint64_t m = mask64_unchecked(w);
 const uint64_t diff = (a.v ^ b.v) | (a.x ^ b.x) | (a.z ^ b.z);
 const uint64_t same = (~diff) & m;
 return W64{a.v & same, (a.x & same) | (diff & m), a.z & same, w};
}
inline constexpr W64 arith(W64 a, W64 b, int w, bool subtract) noexcept {
 const uint64_t m = mask64_unchecked(w);
 if (a.x | a.z | b.x | b.z) return W64{0, m, 0, w};
 return W64{(subtract ? a.v - b.v : a.v + b.v) & m, 0, 0, w};
}
inline constexpr W64 concat(W64 a, W64 b) noexcept {
 return W64{(a.v << b.w) | b.v, (a.x << b.w) | b.x, (a.z << b.w) | b.z, a.w + b.w};
}
inline constexpr W64 selbit(W64 a, W64 index) noexcept {
 if ((index.x | index.z) || index.v >= uint64_t(a.w)) return W64{0, 1, 0, 1};
 const unsigned n = unsigned(index.v);
 return W64{(a.v >> n) & 1u, (a.x >> n) & 1u, (a.z >> n) & 1u, 1};
}
inline constexpr W64 selpart(W64 a, int left, int right) noexcept {
 const int w = left - right + 1;
 if (right >= a.w) return unknown(w);
 const int avail = a.w - right;
 const int valid = avail < w ? avail : w;
 const uint64_t vm = mask64_unchecked(valid);
 const uint64_t oob = mask64_unchecked(w) & ~vm;
 return W64{(a.v >> right) & vm, ((a.x >> right) & vm) | oob, (a.z >> right) & vm, w};
}
inline W64 selidx(W64 a, W64 base, int width, bool descending) {
 if (base.x | base.z) return unknown(width);
 if (base.v > 2147483647ULL) throw std::out_of_range("known indexed base exceeds qualified range0..INT32_MAX");
 const int64_t lo = descending ? int64_t(base.v) - (width - 1) : int64_t(base.v);
 const int64_t src_lo = lo < 0 ? 0 : lo;
 const int64_t src_hi = (lo + width) < a.w ? (lo + width) : a.w;
 if (src_lo >= src_hi) return unknown(width);
 const int count = int(src_hi - src_lo);
 const int dst_lo = int(src_lo - lo);
 const int s_lo = int(src_lo);
 const uint64_t cm = mask64_unchecked(count);
 const uint64_t dst_mask = cm << dst_lo;
 return W64{((a.v >> s_lo) & cm) << dst_lo,
            (((a.x >> s_lo) & cm) << dst_lo) | (mask64_unchecked(width) & ~dst_mask),
            ((a.z >> s_lo) & cm) << dst_lo,
            width};
}
inline W64 depidx(W64 before, W64 base, int width, bool descending, W64 value) {
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
 const uint64_t cm = mask64_unchecked(count);
 const uint64_t dmask = cm << d_lo;
 before.v = (before.v & ~dmask) | (((value.v >> src_lo) & cm) << d_lo);
 before.x = (before.x & ~dmask) | (((value.x >> src_lo) & cm) << d_lo);
 before.z = (before.z & ~dmask) | (((value.z >> src_lo) & cm) << d_lo);
 return before;
}
inline constexpr W64 memidx(W64 index, int lower) noexcept {
 if ((index.x | index.z) || index.v < uint64_t(lower)) return W64{0, 1, 0, 1};
 return word(index.v - uint64_t(lower), 0, 0, index.w);
}
inline W64 read(const std::vector<W>& m, W64 index, int w) noexcept {
 if ((index.x | index.z) || index.v >= m.size()) return unknown(w);
 return to(m[size_t(index.v)]);
}
struct MemWrite64 { uint16_t mem_id; uint32_t index; W64 value; };
template <size_t InlineCap = 16>
struct MemLog64 {
 MemWrite64 inline_buf[InlineCap];
 std::vector<MemWrite64> overflow;
 size_t count = 0;
 inline void push(uint16_t mem_id, size_t mem_size, W64 index, W64 value, int w) {
  if ((index.x | index.z) || index.v >= mem_size) return;
  const MemWrite64 entry{mem_id, uint32_t(index.v), resize(value, w)};
  if (count < InlineCap) inline_buf[count++] = entry;
  else overflow.push_back(entry);
 }
 inline void commit(std::vector<std::vector<W>>& mem) noexcept {
  for (size_t i = 0; i < count; ++i) {
   const auto& e = inline_buf[i];
   store(mem[e.mem_id][e.index], e.value);
  }
  for (const auto& e : overflow) store(mem[e.mem_id][e.index], e.value);
  count = 0;
  if (!overflow.empty()) overflow.clear();
 }
};
} // namespace vsrtl::w64

// Register-resident 64-bit two-state runtime for opt-in --two-state models.
// All values are masked uint64_t words (X/Z eliminated). Storage in Model::s
// remains ABI v2 compatible with two_state=true on all signals (x=z=0).
namespace vsrtl::w2 {
inline constexpr int64_t sext(uint64_t a, int w) noexcept {
 const uint64_t m = mask64_unchecked(w);
 a &= m;
 if (w < 64 && (a & (uint64_t(1) << (w - 1)))) a |= ~m;
 return int64_t(a);
}
inline uint64_t to(const W& a) noexcept { return a.v.limb[0]; }
inline W from(uint64_t v, int w) noexcept {
 return W{Bits(v & mask64_unchecked(w)), Bits(), Bits(), w};
}
inline void store(W& dst, uint64_t v) noexcept { dst.v.limb[0] = v; }
inline constexpr uint64_t lit(uint64_t v, int w) noexcept { return v & mask64_unchecked(w); }
inline constexpr uint64_t resize(uint64_t a, int w) noexcept { return a & mask64_unchecked(w); }
inline constexpr uint64_t sresize(uint64_t a, int aw, int w) noexcept {
 return uint64_t(sext(a, aw)) & mask64_unchecked(w);
}
inline constexpr bool edge(uint64_t prev, uint64_t curr, bool pos) noexcept {
 return pos ? (((prev & 1ULL) == 0) && ((curr & 1ULL) != 0))
            : (((prev & 1ULL) != 0) && ((curr & 1ULL) == 0));
}
inline constexpr bool yes(uint64_t a) noexcept { return a != 0; }
inline constexpr uint64_t lnot(uint64_t a) noexcept { return a == 0 ? 1ULL : 0ULL; }
inline constexpr uint64_t land(uint64_t a, uint64_t b) noexcept { return (a != 0 && b != 0) ? 1ULL : 0ULL; }
inline constexpr uint64_t lor(uint64_t a, uint64_t b) noexcept { return (a != 0 || b != 0) ? 1ULL : 0ULL; }
inline constexpr uint64_t bnot(uint64_t a, int w) noexcept { return (~a) & mask64_unchecked(w); }
inline constexpr uint64_t bbin(uint64_t a, uint64_t b, int w, int op) noexcept {
 return (op == 0 ? (a & b) : op == 1 ? (a | b) : (a ^ b)) & mask64_unchecked(w);
}
inline constexpr uint64_t eq(uint64_t a, uint64_t b) noexcept { return a == b ? 1ULL : 0ULL; }
inline constexpr uint64_t cmp(uint64_t a, uint64_t b, int op) noexcept {
 return (op == 0 ? a < b : op == 1 ? a <= b : op == 2 ? a > b : a >= b) ? 1ULL : 0ULL;
}
inline constexpr uint64_t scmp(uint64_t a, uint64_t b, int w, int op) noexcept {
 const int64_t sa = sext(a, w), sb = sext(b, w);
 return (op == 0 ? sa < sb : op == 1 ? sa <= sb : op == 2 ? sa > sb : sa >= sb) ? 1ULL : 0ULL;
}
inline constexpr uint64_t arith(uint64_t a, uint64_t b, int w, bool sub) noexcept {
 return (sub ? (a - b) : (a + b)) & mask64_unchecked(w);
}
inline constexpr uint64_t shift(uint64_t a, uint64_t b, int w, bool right) noexcept {
 if (b >= 64) return 0ULL;
 const uint64_t m = mask64_unchecked(w);
 return (right ? ((a & m) >> b) : (a << b)) & m;
}
inline constexpr uint64_t sshift(uint64_t a, uint64_t b, int w) noexcept {
 const uint64_t m = mask64_unchecked(w);
 const int64_t sa = sext(a, w);
 if (b >= 64) return sa < 0 ? m : 0ULL;
 return uint64_t(sa >> b) & m;
}
inline constexpr uint64_t red(uint64_t a, int w, int op) noexcept {
 const uint64_t m = mask64_unchecked(w);
 const uint64_t v = a & m;
 if (op == 0 || op == 1) return ((v == m) ^ (op == 1)) ? 1ULL : 0ULL;
 if (op == 2 || op == 3) return ((v != 0) ^ (op == 3)) ? 1ULL : 0ULL;
 const bool odd = ((std::popcount(v) & 1) != 0);
 return (odd ^ (op == 5)) ? 1ULL : 0ULL;
}
inline constexpr uint64_t product(uint64_t a, uint64_t b, int w, int op) noexcept {
 const uint64_t m = mask64_unchecked(w);
 const uint64_t av = a & m, bv = b & m;
 if (op == 0) return (av * bv) & m;
 if (bv == 0) return 0ULL;
 return (op == 1 ? (av / bv) : (av % bv)) & m;
}
inline constexpr uint64_t sproduct(uint64_t a, uint64_t b, int w, int op) noexcept {
 const uint64_t m = mask64_unchecked(w);
 const int64_t sa = sext(a, w), sb = sext(b, w);
 if (op == 0) return (uint64_t(sa) * uint64_t(sb)) & m;
 if (sb == 0) return 0ULL;
 if (sa == INT64_MIN && sb == -1)
  return op == 1 ? (uint64_t(sa) & m) : 0ULL;
 return uint64_t(op == 1 ? (sa / sb) : (sa % sb)) & m;
}
inline constexpr uint64_t cond(uint64_t c, uint64_t t, uint64_t f, int w) noexcept {
 return (c != 0 ? t : f) & mask64_unchecked(w);
}
inline constexpr uint64_t concat(uint64_t a, uint64_t b, int bw) noexcept {
 return (bw >= 64 ? 0ULL : (a << bw)) | (b & mask64_unchecked(bw));
}
inline constexpr uint64_t selbit(uint64_t a, int aw, uint64_t idx) noexcept {
 return idx < uint64_t(aw) ? ((a >> idx) & 1ULL) : 0ULL;
}
inline constexpr uint64_t selpart(uint64_t a, int aw, int left, int right) noexcept {
 const int n = left - right + 1;
 if (right >= aw) return 0ULL;
 return ((a & mask64_unchecked(aw)) >> right) & mask64_unchecked(n);
}
inline uint64_t selidx(uint64_t a, int aw, uint64_t base, int width, bool descending) {
 if (base > 2147483647ULL) throw std::out_of_range("known indexed select base exceeds qualified range0..INT32_MAX");
 const int64_t lo = descending ? int64_t(base) - (width - 1) : int64_t(base);
 const int64_t src_lo = lo < 0 ? 0 : lo;
 const int64_t src_hi = (lo + width) < aw ? (lo + width) : aw;
 if (src_lo >= src_hi) return 0ULL;
 const int count = int(src_hi - src_lo);
 const int dst_lo = int(src_lo - lo);
 const int s_lo = int(src_lo);
 const uint64_t cm = mask64_unchecked(count);
 return (((a & mask64_unchecked(aw)) >> s_lo) & cm) << dst_lo;
}
inline uint64_t depidx(uint64_t before, int tw, uint64_t base, int width, bool descending, uint64_t value) {
 if (base > 2147483647ULL) throw std::out_of_range("known selected write base exceeds qualified range0..INT32_MAX");
 value &= mask64_unchecked(width);
 const int64_t lo = descending ? int64_t(base) - (width - 1) : int64_t(base);
 const int64_t dst_lo = lo < 0 ? 0 : lo;
 const int64_t dst_hi = (lo + width) < tw ? (lo + width) : tw;
 if (dst_lo >= dst_hi) return before;
 const int count = int(dst_hi - dst_lo);
 const int src_lo = int(dst_lo - lo);
 const int d_lo = int(dst_lo);
 const uint64_t cm = mask64_unchecked(count);
 const uint64_t dmask = cm << d_lo;
 return (before & ~dmask) | (((value >> src_lo) & cm) << d_lo);
}
inline constexpr uint64_t memidx(uint64_t index, int lower) noexcept {
 return index >= uint64_t(lower) ? (index - uint64_t(lower)) : ~uint64_t(0);
}
inline uint64_t read_flat(const uint64_t* m, size_t depth, uint64_t index) noexcept {
 return index < depth ? m[index] : 0ULL;
}
struct MemWrite2 { uint16_t mem_id; uint32_t index; uint64_t value; };
template <size_t InlineCap = 16>
struct MemLog2 {
 MemWrite2 inline_buf[InlineCap];
 std::vector<MemWrite2> overflow;
 size_t count = 0;
 inline void push(uint16_t mem_id, size_t mem_size, uint64_t index, uint64_t value, int w) {
  if (index >= mem_size) return;
  const MemWrite2 entry{mem_id, uint32_t(index), value & mask64_unchecked(w)};
  if (count < InlineCap) inline_buf[count++] = entry;
  else overflow.push_back(entry);
 }
 inline void commit(uint64_t* const* mem_ptrs) noexcept {
  for (size_t i = 0; i < count; ++i) {
   const auto& e = inline_buf[i];
   mem_ptrs[e.mem_id][e.index] = e.value;
  }
  for (const auto& e : overflow) mem_ptrs[e.mem_id][e.index] = e.value;
  count = 0;
  if (!overflow.empty()) overflow.clear();
 }
};
inline void check_assert(uint64_t cond, const char* msg) {
 if (!cond) throw std::runtime_error(msg && *msg ? std::string("RTL assertion failed: ") + msg : "RTL assertion failed");
}
} // namespace vsrtl::w2

namespace vsrtl {

inline void check_assert(const W& cond, const char* msg) {
 if (!yes(cond)) throw std::runtime_error(msg && *msg ? std::string("RTL assertion failed: ") + msg : "RTL assertion failed");
}

namespace w64 {
inline void check_assert(W64 cond, const char* msg) {
 if (!yes(cond)) throw std::runtime_error(msg && *msg ? std::string("RTL assertion failed: ") + msg : "RTL assertion failed");
}
} // namespace w64

// IEEE 1364 $readmemh / $readmemb parser supporting @hex_addr, // and /* */ comments, '_' separators, and 4-state x/z.
inline W parse_mem_token(const std::string& token, int width, bool hex) {
 Bits v = 0, x = 0, z = 0;
 const int shift = hex ? 4 : 1;
 const uint64_t nib = hex ? 0xFULL : 1ULL;
 bool any = false;
 for (char raw : token) {
  if (raw == '_') continue;
  unsigned char c = static_cast<unsigned char>(raw);
  if (c >= 'A' && c <= 'Z') c = static_cast<unsigned char>(c + 32);
  v <<= shift; x <<= shift; z <<= shift;
  if (c == 'x') x |= nib;
  else if (c == 'z') z |= nib;
  else if (hex) {
   int d = (c >= '0' && c <= '9') ? (c - '0') : (c >= 'a' && c <= 'f') ? (c - 'a' + 10) : -1;
   if (d < 0) throw std::runtime_error("invalid hex digit in $readmemh token: " + token);
   v |= uint64_t(d);
  } else {
   if (c != '0' && c != '1') throw std::runtime_error("invalid binary digit in $readmemb token: " + token);
   if (c == '1') v |= 1ULL;
  }
  any = true;
 }
 if (!any) throw std::runtime_error("empty token in memory file");
 return word(v, x, z, width);
}

inline size_t load_mem_file(std::vector<W>& mem, int width, const std::string& path, bool hex,
                            size_t start_addr = 0, size_t end_addr = SIZE_MAX, bool two_state_mem = false) {
 std::ifstream in(path);
 if (!in) throw std::runtime_error("failed to open memory file: " + path);
 std::string content((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
 const size_t limit = end_addr < mem.size() ? end_addr : (mem.empty() ? 0 : mem.size() - 1);
 size_t addr = start_addr, loaded = 0, i = 0, n = content.size();
 auto skip_ws_comments = [&]() {
  while (i < n) {
   unsigned char c = static_cast<unsigned char>(content[i]);
   if (c <= ' ') { ++i; continue; }
   if (c == '/' && i + 1 < n && content[i + 1] == '/') {
    i += 2; while (i < n && content[i] != '\n') ++i; continue;
   }
   if (c == '/' && i + 1 < n && content[i + 1] == '*') {
    i += 2; while (i + 1 < n && !(content[i] == '*' && content[i + 1] == '/')) ++i;
    if (i + 1 >= n) throw std::runtime_error("unterminated block comment in memory file");
    i += 2; continue;
   }
   break;
  }
 };
 while (true) {
  skip_ws_comments();
  if (i >= n) break;
  size_t tok_start = i;
  while (i < n && static_cast<unsigned char>(content[i]) > ' ' &&
         !(content[i] == '/' && i + 1 < n && (content[i + 1] == '/' || content[i + 1] == '*'))) ++i;
  std::string tok = content.substr(tok_start, i - tok_start);
  if (tok.empty()) continue;
  if (tok[0] == '@') {
   if (tok.size() == 1) throw std::runtime_error("empty @address directive in memory file");
   size_t parsed = 0;
   for (size_t k = 1; k < tok.size(); ++k) {
    char c = tok[k];
    if (c == '_') continue;
    if (c >= 'A' && c <= 'Z') c = char(c + 32);
    int d = (c >= '0' && c <= '9') ? (c - '0') : (c >= 'a' && c <= 'f') ? (c - 'a' + 10) : -1;
    if (d < 0) throw std::runtime_error("invalid @address in memory file: " + tok);
    parsed = (parsed << 4) | size_t(d);
   }
   addr = parsed;
   continue;
  }
  if (mem.empty() || addr > limit || addr >= mem.size()) break;
  W val = parse_mem_token(tok, width, hex);
  mem[addr++] = two_state_mem ? two_state(val) : val;
  ++loaded;
 }
 return loaded;
}

inline size_t load_mem_file_flat(uint64_t* mem, size_t depth, int width, const std::string& path, bool hex,
                                 size_t start_addr = 0, size_t end_addr = SIZE_MAX) {
 std::vector<W> tmp(depth, word(0, 0, 0, width));
 for (size_t i = 0; i < depth; ++i) tmp[i].v.limb[0] = mem[i];
 size_t loaded = load_mem_file(tmp, width, path, hex, start_addr, end_addr, true);
 for (size_t i = 0; i < depth; ++i) mem[i] = tmp[i].v.limb[0];
 return loaded;
}

// IEEE 1364 $display / $write formatter supporting %d, %u, %x/%h, %b, %0d/%0x/%0b, and %%.
inline std::string format_display(const std::string& fmt, const std::vector<W>& args, bool newline) {
 std::string out;
 out.reserve(fmt.size() + 32);
 size_t arg_idx = 0;
 auto fmt_hex = [](const W& w, bool trim) {
  std::string s;
  for (int low = ((w.w - 1) / 4) * 4; low >= 0; low -= 4) {
   auto m = mask(std::min(4, w.w - low));
   auto xv = (w.x >> low) & m, zv = (w.z >> low) & m, vv = (w.v >> low) & m;
   s += xv ? (xv == m ? 'x' : 'X') : zv ? (zv == m ? 'z' : 'Z') : "0123456789abcdef"[unsigned(vv)];
  }
  if (trim) { size_t p = 0; while (p + 1 < s.size() && s[p] == '0') ++p; s.erase(0, p); }
  return s;
 };
 auto fmt_dec = [](const W& w) {
  if (w.x) return std::string(w.x == mask(w.w) ? "x" : "X");
  if (w.z) return std::string(w.z == mask(w.w) ? "z" : "Z");
  Bits v = w.v & mask(w.w);
  std::string s;
  do { s += char('0' + (v % 10)); v /= 10; } while (v);
  std::reverse(s.begin(), s.end());
  return s;
 };
 for (size_t i = 0; i < fmt.size(); ++i) {
  if (fmt[i] != '%' || i + 1 >= fmt.size()) { out += fmt[i]; continue; }
  if (fmt[i + 1] == '%') { out += '%'; ++i; continue; }
  bool zero_trim = false;
  if (fmt[i + 1] == '0' && i + 2 < fmt.size()) { zero_trim = true; ++i; }
  char spec = fmt[++i];
  if (arg_idx >= args.size()) { out += '%'; out += spec; continue; }
  const W& a = args[arg_idx++];
  if (spec == 'h' || spec == 'H' || spec == 'x' || spec == 'X') out += fmt_hex(a, zero_trim);
  else if (spec == 'b' || spec == 'B') {
   std::string s = text(a);
   if (zero_trim) { size_t p = 0; while (p + 1 < s.size() && s[p] == '0') ++p; s.erase(0, p); }
   out += s;
  } else if (spec == 'd' || spec == 'D' || spec == 'u' || spec == 'U') out += fmt_dec(a);
  else { out += '%'; out += spec; }
 }
 if (newline) out += '\n';
 return out;
}

} // namespace vsrtl
