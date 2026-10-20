// VeloSim native multibit experimental kernel.
// This is not a complete IEEE 1800 scheduler or a UHDM compiler.
#include <cstdint>
#include <vector>
#include <atomic>
#include <thread>
#include <algorithm>
#include <limits>
#include "../include/velosim.h"
namespace {
struct Word {uint64_t value, xmask, zmask; uint32_t width;};
struct Proc {uint32_t clk,dst,a,reset,b; uint8_t edge,opcode;};
struct Engine {std::vector<Word> sig; std::vector<Proc> proc; uint64_t time=0;};
uint64_t mask(uint32_t w){return w==64?UINT64_MAX:((uint64_t{1}<<w)-1);}
Word normalize(Word a){auto m=mask(a.width);a.value &=m;a.xmask &=m;a.zmask &=m;a.xmask &=~a.zmask;a.value &=~(a.xmask|a.zmask);return a;}
int scalar(Word a){if(a.xmask||a.zmask)return 2;return (a.value&1)?1:0;}
bool rising(int a,int b){return (a==0&&b!=0)||(a==2&&b==1);}
bool falling(int a,int b){return (a==1&&b!=1)||(a==2&&b==0);}
Word resize(Word a,uint32_t w){a.width=w;return normalize(a);}
Word xword(uint32_t w){return Word{0,mask(w),0,w};}
// Four-state increment: resolve the carry per bit, not the entire word.
Word increment(Word a,uint32_t width){
 Word out{0,0,0,width};
 int carry=1;
 for(uint32_t i=0;i<width;i++){
   uint64_t bit=uint64_t{1}<<i;
   int input=((a.xmask|a.zmask)&bit)?2:((a.value&bit)?1:0);
   int sum;
   if(carry==0){sum=input;}
   else if(carry==1){sum=input==2?2:!input;}
   else {sum=2;}
   if(sum==1)out.value|=bit;
   if(sum==2)out.xmask|=bit;
   if(carry==1)carry=input;
   else if(carry==2)carry=(input==0)?0:2;
 }
 return normalize(out);
}
Word compute(const Proc& p,const std::vector<Word>& sig){
 const Word a=sig[p.a];auto w=sig[p.dst].width;
 if(p.opcode==0)return resize(a,w);
 if(p.opcode>=6){
   const Word original_b=sig[p.b];
   if(p.opcode>=10){
     if(a.xmask||a.zmask||original_b.xmask||original_b.zmask)return xword(w);
     uint64_t result=p.opcode==10?a.value+original_b.value:p.opcode==11?a.value-original_b.value:a.value*original_b.value;
     return normalize(Word{result,0,0,w});
   }
   const Word lhs=resize(a,w),rhs=resize(original_b,w);auto m=mask(w);
   auto ax=lhs.xmask|lhs.zmask,bx=rhs.xmask|rhs.zmask;
   auto az=~(lhs.value|ax)&m,bz=~(rhs.value|bx)&m;
   if(p.opcode==6){auto one=lhs.value&rhs.value;return Word{one,m&~(one|az|bz),0,w};}
   if(p.opcode==7){auto one=lhs.value|rhs.value;return Word{one,m&~(one|(az&bz)),0,w};}
   if(p.opcode==8)return Word{(lhs.value^rhs.value)&~(ax|bx)&m,(ax|bx)&m,0,w};
   return Word{az,ax,0,w}; // Bitwise NOT converts input Z to X.
 }
 Word next=increment(a,w);
 if(p.opcode==1)return next;
 // Strict IEEE arithmetic: any X/Z operand makes the arithmetic result unknown.
 if(p.opcode==4 || p.opcode==5){
   if(a.xmask || a.zmask)next=xword(w);
   if(p.opcode==4)return next;
 }
 if(p.opcode==2 || p.opcode==3 || p.opcode==5){
   int r=scalar(sig[p.reset]);
   if(r==0)return Word{0,0,0,w};
   if(r==2 && p.opcode==2){
     uint64_t unknown=next.value|next.xmask|next.zmask;
     return Word{0,unknown,0,w};
   }
   return next;
 }
 return xword(w);
}
}
extern "C" {
uint32_t vs_ir_abi_version() noexcept {return 2;}
uint32_t vs_ir_build_profile() noexcept {
#ifdef NDEBUG
 return 1;
#else
 return 0;
#endif
}
uint64_t vs_ir_ticks(void* p) noexcept {return p?static_cast<Engine*>(p)->time:0;}
void* vs_ir_new() noexcept {try{return new Engine;}catch(...){return nullptr;}}
void vs_ir_free(void* p) noexcept {delete static_cast<Engine*>(p);}
int vs_ir_signal(void* p,uint32_t width,uint64_t value,uint64_t xmask,uint64_t zmask) noexcept {
 if(!p||width<1||width>64||(xmask&zmask))return -1;
 try {auto& e=*static_cast<Engine*>(p);e.sig.push_back(normalize(Word{value,xmask,zmask,width}));return int(e.sig.size()-1);}catch(...){return -1;}
}
int vs_ir_process_v2(void* p,uint32_t clk,uint32_t dst,uint32_t a,uint32_t b,uint32_t reset,uint8_t edge,uint8_t op) noexcept {
 if(!p||edge>1||op>12)return -1;
 auto& e=*static_cast<Engine*>(p);auto n=e.sig.size();
 if(clk>=n||dst>=n||a>=n||b>=n||reset>=n||e.sig[clk].width!=1)return -1;
 if((op==2||op==3||op==5)&&e.sig[reset].width!=1)return -1;
 for(auto q:e.proc)if(q.dst==dst)return -2;
 try{e.proc.push_back(Proc{clk,dst,a,reset,b,edge,op});return 0;}catch(...){return -1;}
}
int vs_ir_process(void* p,uint32_t clk,uint32_t dst,uint32_t a,uint32_t reset,uint8_t edge,uint8_t op) noexcept {
 if(op>5)return -1;
 return vs_ir_process_v2(p,clk,dst,a,a,reset,edge,op);
}
int vs_ir_read(void* p,uint32_t id,uint64_t* value,uint64_t* xmask,uint64_t* zmask) noexcept {
 if(!p||!value||!xmask||!zmask)return -1;
 auto& e=*static_cast<Engine*>(p);if(id>=e.sig.size())return -1;
 auto v=e.sig[id];*value=v.value;*xmask=v.xmask;*zmask=v.zmask;return int(v.width);
}
int vs_ir_drive(void* p,const uint32_t* ids,const uint64_t* vals,const uint64_t* xs,const uint64_t* zs,uint32_t n,uint32_t workers) noexcept {
 if(!p||workers<1||workers>256||(n&&(!ids||!vals||!xs||!zs)))return -1;
 try{
 auto& e=*static_cast<Engine*>(p);auto old=e.sig,pre=old;
 std::vector<bool> seen(pre.size(),false);
 for(uint32_t i=0;i<n;i++){
   if(ids[i]>=pre.size()||seen[ids[i]]||(xs[i]&zs[i]))return -2;
   seen[ids[i]]=true;pre[ids[i]]=normalize(Word{vals[i],xs[i],zs[i],pre[ids[i]].width});
 }
 std::vector<size_t> active;
 for(size_t i=0;i<e.proc.size();i++){
   auto q=e.proc[i];int before=scalar(old[q.clk]),after=scalar(pre[q.clk]);
   if(q.edge==0?rising(before,after):falling(before,after))active.push_back(i);
 }
 std::vector<Word> results(active.size());
 auto evaluate=[&](size_t j){results[j]=compute(e.proc[active[j]],pre);};
 if(workers==1||active.size()<2){for(size_t j=0;j<active.size();j++)evaluate(j);}
 else{
   std::atomic<size_t> next{0};auto task=[&](){for(;;){auto j=next.fetch_add(1);if(j>=active.size())break;evaluate(j);}};
   auto count=std::min<size_t>(workers,active.size());std::vector<std::jthread> pool;
   for(size_t i=1;i<count;i++)pool.emplace_back(task);
   task();for(auto& t:pool)t.join();
 }
 for(size_t j=0;j<active.size();j++)pre[e.proc[active[j]].dst]=results[j];
 e.sig.swap(pre);e.time++;return int(active.size());
 }catch(...){return -1;}
}
}
