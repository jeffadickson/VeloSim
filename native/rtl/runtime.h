#pragma once
#include <cstdint>
#include <string>
#include <stdexcept>
#include <vector>
namespace vsrtl {
struct W {uint64_t v,x,z;int w;};
inline uint64_t mask(int w){return w==64?UINT64_MAX:(uint64_t(1)<<w)-1;}
inline W word(uint64_t v,uint64_t x,uint64_t z,int w){auto m=mask(w);return {v&~(x|z)&m,x&~z&m,z&m,w};}
inline W resize(W a,int w){return word(a.v,a.x,a.z,w);}
inline W unknown(int w){return {0,mask(w),0,w};}
inline W parse(const std::string& text){if(text.empty()||text.size()>64)throw std::runtime_error("invalid word");uint64_t v=0,x=0,z=0;for(auto c:text){v<<=1;x<<=1;z<<=1;if(c=='1')v|=1;else if(c=='x')x|=1;else if(c=='z')z|=1;else if(c!='0')throw std::runtime_error("invalid digit");}return {v,x,z,int(text.size())};}
inline std::string text(W a){std::string s;for(int i=a.w-1;i>=0;--i){auto b=uint64_t(1)<<i;s+=(a.z&b)?'z':(a.x&b)?'x':(a.v&b)?'1':'0';}return s;}
inline int truth(W a){return a.v?1:(a.x|a.z)?2:0;}
inline bool yes(W a){return truth(a)==1;}
inline W logical_not(W a){int t=truth(a);return t==2?unknown(1):W{uint64_t(!t),0,0,1};}
inline W logical_and(W a,W b){int l=truth(a),r=truth(b);return l==0||r==0?W{0,0,0,1}:l==1&&r==1?W{1,0,0,1}:unknown(1);}
inline W bit_not(W a,int w){a=resize(a,w);auto u=a.x|a.z;return word(~(a.v|u),u,0,w);}
inline W bit_binary(W a,W b,int w,int op){
 a=resize(a,w);b=resize(b,w);auto au=a.x|a.z,bu=b.x|b.z;
 auto az=~(a.v|au),bz=~(b.v|bu);uint64_t v,u;
 if(op==0){v=a.v&b.v;u=~(az|bz|v);}
 else if(op==1){v=a.v|b.v;u=~(v|(az&bz));}
 else{u=au|bu;v=(a.v^b.v)&~u;}
 return word(v,u,0,w);
}
inline bool exact(W a,W b){int w=a.w>b.w?a.w:b.w;a=resize(a,w);b=resize(b,w);return a.v==b.v&&a.x==b.x&&a.z==b.z;}
inline W equal(W a,W b){int w=a.w>b.w?a.w:b.w;a=resize(a,w);b=resize(b,w);auto u=a.x|a.z|b.x|b.z;if((a.v^b.v)&~u)return {0,0,0,1};return u?unknown(1):W{1,0,0,1};}
inline W arithmetic(W a,W b,int w,bool subtract){if(a.x||a.z||b.x||b.z)return unknown(w);return word(subtract?a.v-b.v:a.v+b.v,0,0,w);}
inline W concat(W a,W b){if(a.w+b.w>64)throw std::runtime_error("concat width");return word((a.v<<b.w)|b.v,(a.x<<b.w)|b.x,(a.z<<b.w)|b.z,a.w+b.w);}
inline W read(const std::vector<W>& m,W index,int w){return index.x||index.z||index.v>=m.size()?unknown(w):m[index.v];}
inline void write(std::vector<W>& m,W index,W value,int w){if(!(index.x|index.z)&&index.v<m.size())m[index.v]=resize(value,w);}
}
