#pragma once
#include <cstdint>
#include <string>
#include <stdexcept>
#include <vector>
#include <bit>
#include <utility>
#include <array>
#include <type_traits>
namespace vsrtl {
// Fixed-capacity, little-endian limbs. All operators are unsigned modulo 2^256;
// HDL width/sign conversion remains explicit in the word operations below.
// Keeping storage bounded avoids heap allocation and ABI-dependent host integers.
struct Bits {
 static constexpr unsigned capacity=256;
 std::array<uint64_t,4> limb{};
 Bits(uint64_t value=0):limb{value,0,0,0}{}
 template<class T> requires(std::is_integral_v<T>&&std::is_signed_v<T>) Bits(T value):limb{uint64_t(value),value<0?UINT64_MAX:0,value<0?UINT64_MAX:0,value<0?UINT64_MAX:0}{}
 explicit operator bool()const{return limb[0]||limb[1]||limb[2]||limb[3];}
 template<class T> requires(std::is_integral_v<T>&&!std::is_same_v<T,bool>) explicit operator T()const{return T(limb[0]);}
 friend bool operator==(const Bits&,const Bits&)=default;
 friend bool operator<(Bits a,Bits b){for(int i=3;i>=0;--i)if(a.limb[i]!=b.limb[i])return a.limb[i]<b.limb[i];return false;}
 friend bool operator>(Bits a,Bits b){return b<a;}friend bool operator<=(Bits a,Bits b){return !(b<a);}friend bool operator>=(Bits a,Bits b){return !(a<b);}
 friend Bits operator~(Bits a){for(auto& v:a.limb)v=~v;return a;}
 friend Bits operator&(Bits a,Bits b){for(int i=0;i<4;++i)a.limb[i]&=b.limb[i];return a;}
 friend Bits operator|(Bits a,Bits b){for(int i=0;i<4;++i)a.limb[i]|=b.limb[i];return a;}
 friend Bits operator^(Bits a,Bits b){for(int i=0;i<4;++i)a.limb[i]^=b.limb[i];return a;}
 friend Bits operator+(Bits a,Bits b){unsigned __int128 carry=0;for(int i=0;i<4;++i){carry+=static_cast<unsigned __int128>(a.limb[i])+b.limb[i];a.limb[i]=uint64_t(carry);carry>>=64;}return a;}
 friend Bits operator-(Bits a,Bits b){return a+(~b)+Bits(1);}
 friend Bits operator<<(Bits a,uint64_t n){Bits out;if(n>=capacity)return out;unsigned words=unsigned(n/64),shift=unsigned(n%64);for(unsigned i=words;i<4;++i){out.limb[i]=a.limb[i-words]<<shift;if(shift&&i>words)out.limb[i]|=a.limb[i-words-1]>>(64-shift);}return out;}
 friend Bits operator>>(Bits a,uint64_t n){Bits out;if(n>=capacity)return out;unsigned words=unsigned(n/64),shift=unsigned(n%64);for(unsigned i=0;i+words<4;++i){out.limb[i]=a.limb[i+words]>>shift;if(shift&&i+words+1<4)out.limb[i]|=a.limb[i+words+1]<<(64-shift);}return out;}
 friend Bits operator*(Bits a,Bits b){Bits out;for(int i=0;i<4;++i){unsigned __int128 carry=0;for(int j=0;i+j<4;++j){carry+=static_cast<unsigned __int128>(a.limb[i])*b.limb[j]+out.limb[i+j];out.limb[i+j]=uint64_t(carry);carry>>=64;}}return out;}
 // Each remainder is bounded by the scanned numerator prefix, so shifting it
 // before the next bit cannot overflow capacity. Small words use native limbs.
 static std::pair<Bits,Bits> divide(Bits numerator,Bits denominator){
  if(!denominator)throw std::domain_error("zero limb divisor");
  if(!(numerator.limb[2]|numerator.limb[3]|denominator.limb[2]|denominator.limb[3])){unsigned __int128 a=(static_cast<unsigned __int128>(numerator.limb[1])<<64)|numerator.limb[0],b=(static_cast<unsigned __int128>(denominator.limb[1])<<64)|denominator.limb[0];auto q=a/b,r=a%b;Bits quotient{uint64_t(q)},remainder{uint64_t(r)};quotient.limb[1]=uint64_t(q>>64);remainder.limb[1]=uint64_t(r>>64);return {quotient,remainder};}
  Bits quotient,remainder;for(int bit=255;bit>=0;--bit){remainder=remainder<<1;remainder.limb[0]|=(numerator.limb[bit/64]>>(bit%64))&1;if(remainder>=denominator){remainder=remainder-denominator;quotient.limb[bit/64]|=uint64_t(1)<<(bit%64);}}return {quotient,remainder};
 }
 friend Bits operator/(Bits a,Bits b){return divide(a,b).first;}friend Bits operator%(Bits a,Bits b){return divide(a,b).second;}
 Bits& operator|=(Bits b){return *this=*this|b;}Bits& operator&=(Bits b){return *this=*this&b;}
 Bits& operator<<=(uint64_t n){return *this=*this<<n;}Bits& operator>>=(uint64_t n){return *this=*this>>n;}Bits& operator/=(Bits b){return *this=*this/b;}
};
struct W {Bits v,x,z;int w;};
// One shared budget covers startup or an entire evaluation, including nested loops.
inline void execution_step(size_t& remaining){if(!remaining)throw std::runtime_error("RTL execution budget exceeded");--remaining;}
inline unsigned repeat_count(W count,bool is_signed){if(count.x||count.z||(is_signed&&bool(count.v&(Bits(1)<<(count.w-1)))))return 0;if(count.v>20000)throw std::runtime_error("runtime repeat count exceeds execution budget");return unsigned(count.v);}
inline Bits mask(int w){if(w<0||w>256)throw std::invalid_argument("word width");return w==256?~Bits(0):(Bits(1)<<w)-1;}
inline W word(Bits v,Bits x,Bits z,int w){auto m=mask(w);return {v&~(x|z)&m,x&~z&m,z&m,w};}
inline W resize(W a,int w){return word(a.v,a.x,a.z,w);}
inline W unknown(int w){return {0,mask(w),0,w};}
inline W two_state(W value){return word(value.v,0,0,value.w);}
inline W memory_index(W index,int lower){
 if(lower<0)throw std::invalid_argument("negative memory lower bound");
 if(index.x||index.z||index.v<Bits(lower))return unknown(1);
 return word(index.v-Bits(lower),0,0,index.w);
}
// Flatten fixed unpacked coordinates only after checking every dimension.
// Bounds preserve declaration direction for future traversal operations; cell
// identity uses ascending physical offsets independently of that direction.
inline W array_index(const std::vector<W>& indices,const std::vector<std::pair<int,int>>& dimensions){
 if(indices.empty()||indices.size()!=dimensions.size()||indices.size()>4)throw std::invalid_argument("array index rank");
 uint64_t offset=0,product=1;
 for(size_t i=0;i<indices.size();++i){auto [left,right]=dimensions[i];if(left<0||right<0)throw std::invalid_argument("array dimension bounds");auto lower=std::min(left,right),upper=std::max(left,right);uint64_t size=uint64_t(upper)-lower+1;product*=size;if(product>256)throw std::invalid_argument("array dimension product");auto value=indices[i];if(value.x||value.z||value.v<Bits(lower)||value.v>Bits(upper))return unknown(64);offset=offset*size+uint64_t(value.v)-lower;}
 return word(offset,0,0,64);
}
inline bool edge_event(W before,W after,bool positive){
 if(before.w!=1||after.w!=1)throw std::invalid_argument("event requires scalar values");
 int a=before.x?2:before.z?3:before.v?1:0,b=after.x?2:after.z?3:after.v?1:0;
 return positive?((a==0&&b!=0)||(a>=2&&b==1)):((a==1&&b!=1)||(a>=2&&b==0));
}
inline W select_bit(W a,W index){if(index.x||index.z||index.v>=uint64_t(a.w))return unknown(1);auto n=unsigned(index.v);return word(a.v>>n,a.x>>n,a.z>>n,1);}
inline W select_part(W a,int left,int right){int w=left-right+1;W out=unknown(w);for(int i=0;i<w;i++){auto index=uint64_t(right)+unsigned(i);if(index>=uint64_t(a.w))continue;auto dst=Bits(1)<<i,src=Bits(1)<<unsigned(index);out.x&=~dst;if(a.v&src)out.v|=dst;if(a.x&src)out.x|=dst;if(a.z&src)out.z|=dst;}return out;}
inline W select_indexed(W a,W base,int width,bool descending){
 if(width<1||width>256)throw std::invalid_argument("indexed select width must be1..256");
 W out=unknown(width);if(base.x||base.z)return out;
 if(base.v>2147483647ULL)throw std::out_of_range("known indexed base exceeds qualified range0..INT32_MAX");
 for(int bit=0;bit<width;++bit){
  uint64_t offset=unsigned(descending?width-1-bit:bit),index;
  if(descending){if(base.v<offset)continue;index=uint64_t(base.v)-offset;}
  else{if(base.v>UINT64_MAX-offset)continue;index=uint64_t(base.v)+offset;}
  if(index>=uint64_t(a.w))continue;
  auto dst=Bits(1)<<bit,src=Bits(1)<<index;
  out.x&=~dst;if(a.v&src)out.v|=dst;if(a.x&src)out.x|=dst;if(a.z&src)out.z|=dst;
 }
 return out;
}


inline W parse(const std::string& text){if(text.empty()||text.size()>256)throw std::runtime_error("invalid word");Bits v=0,x=0,z=0;for(auto c:text){v<<=1;x<<=1;z<<=1;if(c=='1')v|=1;else if(c=='x')x|=1;else if(c=='z')z|=1;else if(c!='0')throw std::runtime_error("invalid digit");}return {v,x,z,int(text.size())};}
inline std::string text(W a){std::string s;for(int i=a.w-1;i>=0;--i){auto b=Bits(1)<<i;s+=(a.z&b)?'z':(a.x&b)?'x':(a.v&b)?'1':'0';}return s;}
inline int truth(W a){return a.v?1:(a.x|a.z)?2:0;}
inline bool yes(W a){return truth(a)==1;}
inline W logical_not(W a){int t=truth(a);return t==2?unknown(1):W{uint64_t(!t),0,0,1};}
inline W logical_and(W a,W b){int l=truth(a),r=truth(b);return l==0||r==0?W{0,0,0,1}:l==1&&r==1?W{1,0,0,1}:unknown(1);}
inline W logical_or(W a,W b){int l=truth(a),r=truth(b);return l==1||r==1?W{1,0,0,1}:l==0&&r==0?W{0,0,0,1}:unknown(1);}
inline W bit_not(W a,int w){a=resize(a,w);auto u=a.x|a.z;return word(~(a.v|u),u,0,w);}
inline W bit_binary(W a,W b,int w,int op){
 a=resize(a,w);b=resize(b,w);auto au=a.x|a.z,bu=b.x|b.z;
 auto az=~(a.v|au),bz=~(b.v|bu);Bits v,u;
 if(op==0){v=a.v&b.v;u=~(az|bz|v);}
 else if(op==1){v=a.v|b.v;u=~(v|(az&bz));}
 else{u=au|bu;v=(a.v^b.v)&~u;}
 return word(v,u,0,w);
}
inline bool exact(W a,W b){int w=a.w>b.w?a.w:b.w;a=resize(a,w);b=resize(b,w);return a.v==b.v&&a.x==b.x&&a.z==b.z;}
inline W equal(W a,W b){int w=a.w>b.w?a.w:b.w;a=resize(a,w);b=resize(b,w);auto u=a.x|a.z|b.x|b.z;if((a.v^b.v)&~u)return {0,0,0,1};return u?unknown(1):W{1,0,0,1};}
inline W compare(W a,W b,int op){if(a.x||a.z||b.x||b.z)return unknown(1);bool v=op==0?a.v<b.v:op==1?a.v<=b.v:op==2?a.v>b.v:a.v>=b.v;return W{uint64_t(v),0,0,1};}
inline W shift(W a,W amount,int w,bool right){
 a=resize(a,w);if(amount.x||amount.z)return unknown(w);
 if(amount.v>=uint64_t(w))return W{0,0,0,w};
 auto n=unsigned(amount.v);return right?word(a.v>>n,a.x>>n,a.z>>n,w):word(a.v<<n,a.x<<n,a.z<<n,w);
}
inline W reduce(W a,int op){
 W r;auto u=a.x|a.z;
 if(op<2)r=(~(a.v|u)&mask(a.w))?W{0,0,0,1}:u?unknown(1):W{1,0,0,1};
 else if(op<4)r=a.v?W{1,0,0,1}:u?unknown(1):W{0,0,0,1};
 else r=u?unknown(1):W{uint64_t((std::popcount(a.v.limb[0])+std::popcount(a.v.limb[1])+std::popcount(a.v.limb[2])+std::popcount(a.v.limb[3]))&1),0,0,1};
 return op%2?logical_not(r):r;
}
inline W product(W a,W b,int w,int op){
 a=resize(a,w);b=resize(b,w);if(a.x||a.z||b.x||b.z||(op!=0&&b.v==0))return unknown(w);
 return word(op==0?a.v*b.v:op==1?a.v/b.v:a.v%b.v,0,0,w);
}
inline W conditional(W condition,W a,W b,int w){
 a=resize(a,w);b=resize(b,w);int t=truth(condition);if(t==1)return a;if(t==0)return b;
 auto same=~((a.v^b.v)|(a.x^b.x)|(a.z^b.z));return word(a.v&same,(a.x&same)|~same,a.z&same,w);
}
inline W arithmetic(W a,W b,int w,bool subtract){if(a.x||a.z||b.x||b.z)return unknown(w);return word(subtract?a.v-b.v:a.v+b.v,0,0,w);}
inline W concat(W a,W b){if(a.w+b.w>256)throw std::runtime_error("concat width");return word((a.v<<b.w)|b.v,(a.x<<b.w)|b.x,(a.z<<b.w)|b.z,a.w+b.w);}
inline W read(const std::vector<W>& m,W index,int w){return index.x||index.z||index.v>=m.size()?unknown(w):m[size_t(index.v)];}
inline void write(std::vector<W>& m,W index,W value,int w){if(!(index.x|index.z)&&index.v<m.size())m[size_t(index.v)]=resize(value,w);}
}
namespace vsrtl {
inline W deposit_indexed(W before,W base,int width,bool descending,W value){
 if(width<1||width>256)throw std::invalid_argument("selected write width must be1..256");
 if(base.x||base.z)return before;
 if(base.v>2147483647ULL)throw std::out_of_range("known selected write base exceeds qualified range0..INT32_MAX");
 value=resize(value,width);
 for(int bit=0;bit<width;++bit){
  uint64_t offset=unsigned(descending?width-1-bit:bit),index;
  if(descending){if(base.v<offset)continue;index=uint64_t(base.v)-offset;}
  else{if(base.v>UINT64_MAX-offset)continue;index=uint64_t(base.v)+offset;}
  if(index>=uint64_t(before.w))continue;
  auto dst=Bits(1)<<index,src=Bits(1)<<bit;
  before.v&=~dst;before.x&=~dst;before.z&=~dst;
  if(value.v&src)before.v|=dst;if(value.x&src)before.x|=dst;if(value.z&src)before.z|=dst;
 }
 return before;
}
}
namespace vsrtl {
inline W signed_resize(W a,int width){
 if(width<=a.w)return resize(a,width);
 auto fill=mask(width)^mask(a.w),sign=Bits(1)<<(a.w-1);
 return word(a.v|((a.v&sign)?fill:Bits(0)),a.x|((a.x&sign)?fill:Bits(0)),a.z|((a.z&sign)?fill:Bits(0)),width);
}
inline bool negative(W a){return bool(a.v&(Bits(1)<<(a.w-1)));}
inline W signed_compare(W a,W b,int op){
 if(a.x||a.z||b.x||b.z)return unknown(1);
 if(negative(a)==negative(b))return compare(a,b,op);
 return W{uint64_t(op<2?negative(a):!negative(a)),0,0,1};
}
inline W signed_shift(W a,W amount,int width){
 a=signed_resize(a,width);if(amount.x||amount.z)return unknown(width);
 auto sign=Bits(1)<<(width-1);Bits fillV=(a.v&sign)?mask(width):Bits(0),fillX=(a.x&sign)?mask(width):Bits(0),fillZ=(a.z&sign)?mask(width):Bits(0);
 if(amount.v>=uint64_t(width))return word(fillV,fillX,fillZ,width);
 unsigned n=unsigned(amount.v);auto fill=mask(width)^mask(width-int(n));
 return word((a.v>>n)|(fillV&fill),(a.x>>n)|(fillX&fill),(a.z>>n)|(fillZ&fill),width);
}
inline W signed_product(W a,W b,int width,int op){
 a=signed_resize(a,width);b=signed_resize(b,width);
 if(op==0)return product(a,b,width,op);
 if(a.x||a.z||b.x||b.z||!b.v)return unknown(width);
 bool an=negative(a),bn=negative(b);Bits av=an?(~a.v+1)&mask(width):a.v,bv=bn?(~b.v+1)&mask(width):b.v;
 Bits result=op==1?av/bv:av%bv;bool neg=op==1?(an!=bn):an;
 return word(neg?~result+1:result,0,0,width);
}
}
