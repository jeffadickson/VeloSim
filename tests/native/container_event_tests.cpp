#include "../../native/rtl/containers.h"
#include "../../native/rtl/timed_events.h"
#include <cstdlib>
#include <iostream>
#include <random>
using namespace vsrtl;
void check(bool value) { if(!value){std::cerr<<"container/event assertion failed\n";std::abort();} }
template<class Error,class Action> void rejects(Action action) {
 try { action(); } catch(const Error&) { return; } check(false);
}
int main() {
 rejects<std::invalid_argument>([]{DynamicArray a(0);});
 rejects<std::invalid_argument>([]{DynamicArray a(257);});
 DynamicArray a(8);check(a.size()==0&&text(a.read(0))=="xxxxxxxx");
 a.allocate(3);a.write(0,parse("1010"));a.write(2,parse("z"));
 a.write(99,parse("1"));check(text(a.read(0))=="00001010");
 a.allocate(5,true);check(text(a.read(0))=="00001010"&&text(a.read(4))=="xxxxxxxx");
 a.allocate(1,true);check(a.size()==1&&text(a.read(0))=="00001010");
 auto snapshot=a;a.allocate(1);check(text(a.read(0))=="xxxxxxxx"&&text(snapshot.read(0))=="00001010");
 rejects<std::length_error>([&]{a.allocate(257);});check(a.size()==1);a.clear();check(a.size()==0);
 DynamicArray bits(3,true);bits.allocate(2);bits.write(0,parse("1xz"));
 check(text(bits.read(0))=="100"&&text(bits.read(1))=="000"&&text(bits.read(999))=="000");
 Queue q(8);check(text(q.pop())=="xxxxxxxx");check(text(q.pop(true))=="xxxxxxxx");
 q.push(parse("1"));q.push(parse("10"),true);q.write(1,parse("11"));q.write(5,parse("0"));
 check(text(q.read(0))=="00000010"&&text(q.read(9))=="xxxxxxxx");
 check(text(q.pop(true))=="00000010"&&text(q.pop())=="00000011");
 Queue qb(1,true);check(text(qb.pop())=="0");qb.push(parse("z"));check(text(qb.pop())=="0");
 for(unsigned i=0;i<256;++i)q.push(word(i,0,0,8));
 rejects<std::length_error>([&]{q.push(parse("0"));});check(q.size()==256);q.clear();check(q.size()==0);
 AssociativeArray map(8);Bits wide=Bits(1)<<200;
 check(!map.exists(wide)&&text(map.read(wide))=="xxxxxxxx");
 map.write(wide,parse("101"));map.write(0,parse("11"));
 check(map.size()==2&&text(map.read(wide))=="00000101");
 map.erase(wide);map.erase(wide);check(map.size()==1);map.clear();
 for(unsigned i=0;i<256;++i)map.write(i,word(i,0,0,8));
 map.write(1,parse("0"));rejects<std::length_error>([&]{map.write(wide,parse("0"));});check(map.size()==256);
 AssociativeArray mb(2,true);mb.write(1,parse("xz"));check(text(mb.read(1))=="00"&&text(mb.read(0))=="00");
 // Model snapshots own containers and event queues, including wide X/Z cells.
 std::string wideText(256,'1');wideText[0]='z';wideText[64]='x';
 auto wideValue=parse(wideText);Queue wideQueue(256);wideQueue.push(wideValue);
 auto queueCopy=wideQueue;wideQueue.clear();check(text(queueCopy.pop())==wideText);
 AssociativeArray wideMap(256);wideMap.write(wide,wideValue);wideMap.write(wide+1,parse("0"));
 auto mapCopy=wideMap;wideMap.erase(wide);check(text(mapCopy.read(wide))==wideText&&mapCopy.read(wide+1).v==Bits(0));
 DynamicArray wideArray(256);wideArray.allocate(1);wideArray.write(0,wideValue);check(text(wideArray.read(0))==wideText);

 // Reproducible operation fuzz/property test, using independent STL storage
 // and integer expectations. The printed seed makes failures reproducible.
 constexpr unsigned seed=20261030;std::cout<<"seed="<<seed<<"\n";std::mt19937 rng(seed);
 std::deque<unsigned> oracle;
 for(unsigned trial=0;trial<10000;++trial) {
  bool front=rng()%2;
  if(oracle.empty()||(oracle.size()<256&&rng()%2)) {
   unsigned value=rng()%256;q.push(word(value,0,0,8),front);
   if(front)oracle.push_front(value);else oracle.push_back(value);
  } else {
   auto value=front?oracle.front():oracle.back();check(q.pop(front).v==Bits(value));
   if(front)oracle.pop_front();else oracle.pop_back();
  }
  check(q.size()==oracle.size());
 }
 TimedEvents events;check(!events.pop()&&events.now()==0);
 rejects<std::invalid_argument>([&]{events.schedule(0,static_cast<TimedEvents::Region>(3),1);});
 using R=TimedEvents::Region;
 events.schedule(5,R::active,5);events.schedule(0,R::nba,3);events.schedule(0,R::active,1);events.schedule(0,R::active,2);
 check(events.pop()->process==1&&events.pop()->process==2&&events.pop()->process==3);
 events.schedule(0,R::active,4);auto e=events.pop();check(e->time==0&&e->delta==1&&e->process==4);
 e=events.pop();check(e->time==5&&e->delta==0&&e->process==5);
 events.schedule(0,R::inactive,6);check(events.pop()->region==R::inactive);
 events.schedule(UINT64_MAX-5,R::active,7);check(events.pop()->time==UINT64_MAX);
 rejects<std::overflow_error>([&]{events.schedule(1,R::active,8);});check(events.size()==0&&events.now()==UINT64_MAX);
 TimedEvents budget(1);budget.schedule(1,R::active,1);check(budget.pop()->process==1);
 budget.schedule(1,R::active,2);rejects<std::runtime_error>([&]{budget.pop();});check(budget.size()==1&&budget.now()==1);
 TimedEvents rounds(6);rounds.schedule(0,R::nba,0);
 for(unsigned round=0;round<3;++round){check(rounds.pop()->delta==round);rounds.schedule(0,R::active,1);check(rounds.pop()->delta==round+1);rounds.schedule(0,R::nba,0);}
 auto eventCopy=rounds;rejects<std::runtime_error>([&]{rounds.pop();});check(rounds.size()==1&&eventCopy.size()==1);
 TimedEvents independent;independent.schedule(3,R::active,9);auto independentCopy=independent;independent.pop();check(independentCopy.now()==0&&independentCopy.pop()->process==9);
 TimedEvents full;for(unsigned i=0;i<TimedEvents::capacity;++i)full.schedule(0,R::active,i);
 rejects<std::length_error>([&]{full.schedule(0,R::active,0);});check(full.size()==TimedEvents::capacity);
 // Independent stable sort checks transport ordering for randomized timestamps.
 TimedEvents shuffled;std::vector<std::tuple<uint64_t,R,unsigned>> expected;
 for(unsigned i=0;i<1000;++i){auto time=rng()%100;auto region=static_cast<R>(rng()%3);shuffled.schedule(time,region,i);expected.emplace_back(time,region,i);}
 std::sort(expected.begin(),expected.end());
 for(auto [time,region,id]:expected){auto actual=shuffled.pop();check(actual&&actual->time==time&&actual->region==region&&actual->process==id);}
 check(!shuffled.pop());
}
