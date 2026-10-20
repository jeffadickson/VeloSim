// VeloSim v0.10: experimental single-bit 4-state edge scheduler.
// Independent process evaluation may run on multiple threads; NBA-style commit is simultaneous.
// Not a full IEEE SystemVerilog event scheduler.
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <thread>
#include <vector>
#include <memory>
#include "include/velosim.h"
namespace {
struct Process {uint32_t clk,dst,a,b; uint8_t edge,op;};
struct Sim {std::vector<uint8_t> sig; std::vector<Process> procs; uint64_t tick=0;};
constexpr uint8_t X=2;
bool rising(uint8_t a,uint8_t b){return (a==0&&b!=0)||(a>=2&&b==1);}
bool falling(uint8_t a,uint8_t b){return (a==1&&b!=1)||(a>=2&&b==0);}
uint8_t calc(const Process& p,const std::vector<uint8_t>& v){
 uint8_t a=v[p.a],b=v[p.b];
 switch(p.op){
 case 0:return a;
 case 1:return a==0||b==0?0:a==1&&b==1?1:X;
 case 2:return a==1||b==1?1:a==0&&b==0?0:X;
 case 3:return a>=2||b>=2?X:a^b;
 case 4:return a>=2?X:!a;
 default:return X;
 }
}
}
extern "C" {
uint32_t vs_sched_abi_version() noexcept {return 1;}
void* vs_sched_new(uint32_t n) noexcept {
 try {if(!n||n>1000000)return nullptr;auto s=std::make_unique<Sim>();s->sig.assign(n,X);return s.release();}
 catch(...){return nullptr;}
}
void vs_sched_free(void* p) noexcept {delete static_cast<Sim*>(p);}
int vs_sched_get(void* p,uint32_t id) noexcept {
 if(!p)return -1;auto& s=*static_cast<Sim*>(p);return id<s.sig.size()?s.sig[id]:-1;
}
uint64_t vs_sched_ticks(void* p) noexcept {return p?static_cast<Sim*>(p)->tick:0;}
int vs_sched_add(void* p,uint32_t clk,uint32_t dst,uint32_t a,uint32_t b,uint8_t edge,uint8_t op) noexcept {
 if(!p||edge>1||op>4)return -1;auto& s=*static_cast<Sim*>(p);
 if(clk>=s.sig.size()||dst>=s.sig.size()||a>=s.sig.size()||b>=s.sig.size())return -1;
 for(const auto& q:s.procs)if(q.dst==dst)return -2;
 try{s.procs.push_back({clk,dst,a,b,edge,op});return 0;}catch(...){return -1;}
}
int vs_sched_drive(void* p,const uint32_t* ids,const uint8_t* vals,uint32_t n,uint32_t workers) noexcept {
 if(!p||(!ids&&n)||(!vals&&n)||workers==0||workers>256)return -1;
 try{
  auto& s=*static_cast<Sim*>(p);std::vector<uint8_t> old=s.sig,pre=old,seen(old.size(),0);
  for(uint32_t i=0;i<n;i++){
   if(ids[i]>=old.size()||vals[i]>3||seen[ids[i]])return -2;
   seen[ids[i]]=1;pre[ids[i]]=vals[i];
  }
  std::vector<size_t> active;
  for(size_t i=0;i<s.procs.size();i++){
   const auto& q=s.procs[i];
   if(q.edge==0?rising(old[q.clk],pre[q.clk]):falling(old[q.clk],pre[q.clk]))active.push_back(i);
  }
  std::vector<uint8_t> results(active.size());
  auto work=[&](size_t j){results[j]=calc(s.procs[active[j]],pre);};
  if(workers==1||active.size()<2){for(size_t j=0;j<active.size();j++)work(j);}
  else{
   std::atomic<size_t> next{0};
   auto run=[&](){for(;;){size_t j=next.fetch_add(1);if(j>=active.size())break;work(j);}};
   size_t count=std::min<size_t>(workers,active.size());std::vector<std::jthread> threads;
   for(size_t i=1;i<count;i++)threads.emplace_back(run);
   run();for(auto& t:threads)t.join();
  }
  for(size_t j=0;j<active.size();j++)pre[s.procs[active[j]].dst]=results[j];
  s.sig.swap(pre);s.tick++;return static_cast<int>(active.size());
 }catch(...){return -1;}
}
}
