#pragma once
#include <cstdint>
#include <string>
#include <stdexcept>
#include <vector>
#include <bit>
namespace vsrtl {
using Bits=unsigned __int128;
struct W {Bits v,x,z;int w;};
inline Bits mask(int w){if(w<0||w>128)throw std::invalid_argument("word width");return w==128?~Bits(0):(Bits(1)<<w)-1;}
inline W word(Bits v,Bits x,Bits z,int w){auto m=mask(w);return {v&~(x|z)&m,x&~z&m,z&m,w};}
inline W resize(W a,int w){return word(a.v,a.x,a.z,w);}
inline W unknown(int w){return {0,mask(w),0,w};}
inline W select_bit(W a,W index){if(index.x||index.z||index.v>=uint64_t(a.w))return unknown(1);auto n=unsigned(index.v);return word(a.v>>n,a.x>>n,a.z>>n,1);}
inline W select_part(W a,int left,int right){int w=left-right+1;W out=unknown(w);for(int i=0;i<w;i++){auto index=uint64_t(right)+unsigned(i);if(index>=uint64_t(a.w))continue;auto dst=Bits(1)<<i,src=Bits(1)<<unsigned(index);out.x&=~dst;if(a.v&src)out.v|=dst;if(a.x&src)out.x|=dst;if(a.z&src)out.z|=dst;}return out;}
inline W select_indexed(W a,W base,int width,bool descending){
 if(width<1||width>128)throw std::invalid_argument("indexed select width must be1..128");
 W out=unknown(width);if(base.x||base.z)return out;
 if(base.v>2147483647ULL)throw std::out_of_range("known indexed base exceeds qualified range0..INT32_MAX");
 for(int bit=0;bit<width;++bit){
  uint64_t offset=unsigned(descending?width-1-bit:bit),index;
  if(descending){if(base.v<offset)continue;index=base.v-offset;}
  else{if(base.v>UINT64_MAX-offset)continue;index=base.v+offset;}
  if(index>=uint64_t(a.w))continue;
  auto dst=Bits(1)<<bit,src=Bits(1)<<index;
  out.x&=~dst;if(a.v&src)out.v|=dst;if(a.x&src)out.x|=dst;if(a.z&src)out.z|=dst;
 }
 return out;
}


inline W parse(const std::string& text){if(text.empty()||text.size()>128)throw std::runtime_error("invalid word");Bits v=0,x=0,z=0;for(auto c:text){v<<=1;x<<=1;z<<=1;if(c=='1')v|=1;else if(c=='x')x|=1;else if(c=='z')z|=1;else if(c!='0')throw std::runtime_error("invalid digit");}return {v,x,z,int(text.size())};}
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
 else r=u?unknown(1):W{uint64_t((std::popcount(uint64_t(a.v))+std::popcount(uint64_t(a.v>>64)))&1),0,0,1};
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
inline W concat(W a,W b){if(a.w+b.w>128)throw std::runtime_error("concat width");return word((a.v<<b.w)|b.v,(a.x<<b.w)|b.x,(a.z<<b.w)|b.z,a.w+b.w);}
inline W read(const std::vector<W>& m,W index,int w){return index.x||index.z||index.v>=m.size()?unknown(w):m[size_t(index.v)];}
inline void write(std::vector<W>& m,W index,W value,int w){if(!(index.x|index.z)&&index.v<m.size())m[size_t(index.v)]=resize(value,w);}
}
namespace vsrtl {
inline W deposit_indexed(W before,W base,int width,bool descending,W value){
 if(width<1||width>128)throw std::invalid_argument("selected write width must be1..128");
 if(base.x||base.z)return before;
 if(base.v>2147483647ULL)throw std::out_of_range("known selected write base exceeds qualified range0..INT32_MAX");
 value=resize(value,width);
 for(int bit=0;bit<width;++bit){
  uint64_t offset=unsigned(descending?width-1-bit:bit),index;
  if(descending){if(base.v<offset)continue;index=base.v-offset;}
  else{if(base.v>UINT64_MAX-offset)continue;index=base.v+offset;}
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
