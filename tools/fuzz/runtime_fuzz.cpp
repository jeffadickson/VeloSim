#include "../../native/rtl/containers.h"
#include "../../native/rtl/timed_events.h"
#include <algorithm>
#include <cstdlib>
#include <fstream>
#include <filesystem>
#include <iostream>
#include <iterator>
#include <tuple>
using namespace vsrtl;
namespace {
void require(bool condition){if(!condition){std::cerr<<"FUZZ_SEMANTIC_MISMATCH domain="<<FUZZ_DOMAIN<<"\n";std::abort();}}
// Every byte stream is valid. Exhausted operands read zero; the input byte limit
// and operation count bound both oracle and runtime work on malformed inputs.
struct Input {
 const uint8_t* data;size_t size,pos=0;
 uint8_t byte(){return pos<size?data[pos++]:0;}
 std::string bits(unsigned width,bool states=false){std::string result;for(unsigned i=0;i<width;++i)result+=(states?"01xz":"01")[byte()%(states?4:2)];return result;}
};
// Text-position arithmetic oracle uses no Bits limbs or runtime masks/shifts.
std::string add(std::string a,std::string b,bool subtract){int carry=subtract?1:0;for(int i=int(a.size())-1;i>=0;--i){int sum=(a[i]-'0')+(subtract?1-(b[i]-'0'):b[i]-'0')+carry;a[i]=char('0'+sum%2);carry=sum/2;}return a;}
void arithmetic(Input& in){
 unsigned width=1+in.byte();auto a=in.bits(width),b=in.bits(width);auto x=parse(a),y=parse(b);
 require(text(vsrtl::arithmetic(x,y,width,false))==add(a,b,false));
 require(text(vsrtl::arithmetic(x,y,width,true))==add(a,b,true));
 unsigned amount=in.byte();auto shifted=amount>=width?std::string(width,'0'):a.substr(amount)+std::string(amount,'0');
 require(text(shift(x,word(amount,0,0,32),width,false))==shifted);
 std::string productExpected(width,'0');
 for(unsigned bit=0;bit<width;++bit)if(b[width-1-bit]=='1')productExpected=add(productExpected,a.substr(bit)+std::string(bit,'0'),false);
 require(text(product(x,y,width,0))==productExpected);
 // Narrow division is independently checked with host unsigned integers; wide
 // division is covered by the existing managed BigInteger suite, not this oracle.
 if(width<=64){uint64_t av=std::stoull(a,nullptr,2),bv=std::stoull(b,nullptr,2);if(bv){require(product(x,y,width,1).v==Bits(av/bv));require(product(x,y,width,2).v==Bits(av%bv));}else require(text(product(x,y,width,1))==std::string(width,'x'));}
}
void fourState(Input& in){
 unsigned width=1+in.byte();auto a=in.bits(width,true),b=in.bits(width,true);auto x=parse(a),y=parse(b);
 for(int op=0;op<3;++op){std::string expected;for(unsigned i=0;i<width;++i){char l=a[i],r=b[i];bool known=(l=='0'||l=='1')&&(r=='0'||r=='1');expected+=op==0?(l=='0'||r=='0'?'0':l=='1'&&r=='1'?'1':'x'):op==1?(l=='1'||r=='1'?'1':l=='0'&&r=='0'?'0':'x'):known?(l==r?'0':'1'):'x';}require(text(bit_binary(x,y,width,op))==expected);}
 auto coerced=a;for(char& c:coerced)if(c=='x'||c=='z')c='0';require(text(two_state(x))==coerced);
 unsigned index=in.byte();require(text(select_bit(x,word(index,0,0,32)))==(index<width?std::string(1,a[width-1-index]):"x"));
}
void containers(Input& in){
 DynamicArray array(8);Queue queue(8);AssociativeArray map(8);std::vector<W> emitted;
 std::vector<unsigned> expected;std::deque<unsigned> q;std::map<unsigned,unsigned> m;
 for(unsigned steps=0;steps<512&&in.pos<in.size;++steps){unsigned op=in.byte()%10,index=in.byte(),value=in.byte();
  switch(op){
   case 0:case 1:array.allocate(index,op==1);dynamic_allocate(emitted,word(index,0,0,32),8,false,op==1,false);if(op==1)expected.resize(index,256);else expected.assign(index,256);break;
   case 2:array.write(index,word(value,0,0,8));write(emitted,word(index,0,0,32),word(value,0,0,8),8);if(index<expected.size())expected[index]=value;break;
   case 3:if(q.size()<256){queue.push(word(value,0,0,8),index%2);if(index%2)q.push_front(value);else q.push_back(value);}break;
   case 4:{auto actual=queue.pop(index%2);if(q.empty())require(text(actual)=="xxxxxxxx");else{require(actual.v==Bits(index%2?q.front():q.back()));if(index%2)q.pop_front();else q.pop_back();}}break;
   case 5:map.write(index,word(value,0,0,8));m[index]=value;break;
   case 6:map.erase(index);m.erase(index);break;
   case 7:{auto copy=array;array.clear();emitted.clear();require(copy.size()==expected.size());for(size_t i=0;i<expected.size();++i)require(text(copy.read(i))==(expected[i]==256?"xxxxxxxx":text(word(expected[i],0,0,8))));expected.clear();}break;
   case 8:queue.clear();q.clear();break;
   case 9:map.clear();m.clear();break;
  }
  require(array.size()==expected.size()&&queue.size()==q.size()&&map.size()==m.size());
  require(text(array.read(index))==(index>=expected.size()||expected[index]==256?"xxxxxxxx":text(word(expected[index],0,0,8))));
  require(emitted.size()==expected.size()&&text(dynamic_read(emitted,word(index,0,0,32),8,false))==text(array.read(index)));
  require(map.exists(index)==m.contains(index));require(text(map.read(index))==(m.contains(index)?text(word(m[index],0,0,8)):"xxxxxxxx"));
 }
}
void events(Input& in){
 using R=TimedEvents::Region;TimedEvents events(512);
 using Key=std::tuple<uint64_t,uint64_t,unsigned,uint64_t>;
 std::map<Key,uint64_t> expected;uint64_t now=0,delta=0,serial=0;unsigned region=0;bool started=false;
 for(unsigned steps=0;steps<512&&in.pos<in.size;++steps){unsigned op=in.byte(),delay=in.byte(),target=in.byte()%3;
  if(op%3||expected.empty()){
   uint64_t nextDelta=delay?0:delta+uint64_t(started&&target<region);events.schedule(delay,static_cast<R>(target),serial);expected.emplace(Key{now+delay,nextDelta,target,serial},serial);++serial;
  }else{auto actual=events.pop();auto first=expected.begin();auto [t,d,r,s]=first->first;require(actual&&actual->time==t&&actual->delta==d&&unsigned(actual->region)==r&&actual->process==first->second);now=t;delta=d;region=r;started=true;expected.erase(first);}
  require(events.size()==expected.size()&&events.now()==now);
 }
}
}
extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data,size_t size){
 Input in{data,std::min(size,size_t(4096))};
 if constexpr(FUZZ_DOMAIN==0)arithmetic(in);
 if constexpr(FUZZ_DOMAIN==1)fourState(in);
 if constexpr(FUZZ_DOMAIN==2)containers(in);
 if constexpr(FUZZ_DOMAIN==3)events(in);
 return 0;
}
#ifdef VELOSIM_FUZZ_REPLAY
// Separate coverage replay does not confuse libFuzzer edge counters with line
// and branch measurement. Corpus files remain the reproducible inputs.
int main(int argc,char** argv){if(argc!=2)return 2;for(const auto& entry:std::filesystem::directory_iterator(argv[1]))if(entry.is_regular_file()){std::ifstream file(entry.path(),std::ios::binary);std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)),{});LLVMFuzzerTestOneInput(bytes.data(),bytes.size());}return 0;}
#endif
