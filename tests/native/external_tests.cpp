#include "external_model.h"
#include <iostream>
#include <thread>
using namespace vsrtl;
void check(bool condition){if(!condition)throw std::runtime_error("external interface assertion failed");}
struct TestModel:Model {
 bool previous=false;
 Port clk{*this,0},data{*this,1},q{*this,2};
 TestModel():Model("test",{{"clk",1,vpiInput},{"data",64,vpiInput},{"q",64,vpiOutput}},0){}
 void evaluate() override{auto c=get(0);if(c.x||c.z)throw std::runtime_error("unknown clock");if(!previous&&c.v)s[2]=s[1];previous=bool(c.v);}
};
int changes=0,ends=0,starts=0,readOnly=0;vpiHandle dataHandle;
PLI_INT32 changed(p_cb_data cb){++changes;check(cb->value&&cb->value->format==vpiVectorVal);return 0;}
PLI_INT32 ended(p_cb_data){++ends;return 0;}
PLI_INT32 started(p_cb_data){++starts;return 0;}
PLI_INT32 ro(p_cb_data){s_vpi_value value{};value.format=vpiIntVal;value.value.integer=22;vpi_put_value(dataHandle,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr)==vpiError);++readOnly;return 0;}
PLI_INT32 drive(p_cb_data){s_vpi_value value{};value.format=vpiIntVal;value.value.integer=7;vpi_put_value(dataHandle,&value,nullptr,vpiNoDelay);check(!vpi_chk_error(nullptr));return 0;}
vpiHandle named(const char* name){return vpi_handle_by_name(const_cast<char*>(name),nullptr);}
vpiHandle removedStart;int badStart=0;TestModel* alternate=nullptr;bool pinned=false;
bool firstChange=false;
PLI_INT32 firstChanged(p_cb_data){firstChange=true;return 0;}
PLI_INT32 registerFirstChange(p_cb_data){s_cb_data cb{};cb.reason=cbValueChange;cb.obj=named("test.q");cb.cb_rtn=firstChanged;check(vpi_register_cb(&cb));return 0;}
PLI_INT32 removeStart(p_cb_data){check(vpi_remove_cb(removedStart));return 0;}
PLI_INT32 shouldNotStart(p_cb_data){++badStart;return 0;}
PLI_INT32 switchContext(p_cb_data){alternate->useVpi();return 0;}
PLI_INT32 pinnedContext(p_cb_data){auto q=named("test.q");s_vpi_value value{};value.format=vpiBinStrVal;vpi_get_value(q,&value);pinned=q&&std::string(value.value.str)==std::string(63,'0')+"1";return 0;}
#include "external_cycles.h"
#include "external_wide_tests.h"
#include "external_256_tests.h"
// The absent-clock sentinel must preserve every actual input and reject all other
// negative indices, invalid scalar clocks and malformed public interfaces.
void clocklessInterfaceTests(){
 struct Probe:Model{
  Probe(std::vector<Signal> ports,int clock):Model("clockless",std::move(ports),clock){}
  void evaluate() override{if(s.size()==1)s[0]=word(165,0,0,8);else s[1]=bit_not(s[0],4);}
 };
 Probe model({{"clk",4,vpiInput},{"q",4,vpiOutput}},-1);check(text(model.get(0))=="xxxx");model.put(0,word(5,0,0,4));model.eval();check(text(model.get(1))=="1010");model.eval();check(text(model.get(1))=="1010");model.timeInc(2);model.eval();check(text(model.get(1))=="1010");model.final();
 Probe constant({{"q",8,vpiOutput}},-1);constant.eval();check(text(constant.get(0))=="10100101");
 Model& base=constant;check(!base.evalNeeded());base.timeInc(0);check(!base.evalNeeded());
 base.timeInc(1);check(base.evalNeeded());base.eval();check(!base.evalNeeded());constant.final();
 for(int clock:{-2,2,0,1}){bool rejected=false;try{Probe invalid({{"a",4,vpiInput},{"q",4,vpiOutput}},clock);}catch(const std::invalid_argument&){rejected=true;}check(rejected);}
 bool rejected=false;try{Probe invalid({{nullptr,1,vpiInput}},-1);}catch(const std::invalid_argument&){rejected=true;}check(rejected);
}
int main(int argc,char** argv){try{
 clocklessInterfaceTests();
 externalWideTests();
 external256Tests();
 runCycles();
 check(!named("test.q")&&vpi_chk_error(nullptr));
 vpiHandle expired=nullptr;
 {
 TestModel model;model.useVpi();if(argc==2)model.loadVpi(argv[1]);dataHandle=named("test.data");auto q=named("TOP.test.q");check(q&&dataHandle);expired=q;
 check(vpi_get(vpiSize,dataHandle)==64&&vpi_get(vpiDirection,dataHandle)==vpiInput);
 check(std::string(vpi_get_str(vpiFullName,q))=="test.q");check(!named("test.absent")&&!vpi_chk_error(nullptr));
 auto modules=vpi_iterate(vpiModule,nullptr),module=vpi_scan(modules);check(module&&vpi_get(vpiType,module)==vpiModule&&!vpi_scan(modules));
 auto ports=vpi_iterate(vpiPort,module);int count=0;while(auto p=vpi_scan(ports)){auto reg=vpi_handle(vpiLowConn,p);check(reg&&vpi_get(vpiType,reg)==vpiReg);vpi_free_object(reg);vpi_free_object(p);++count;}check(count==3);
 check(named("test")&&vpi_handle_by_name(const_cast<char*>("q"),module));
 s_vpi_value value{};value.format=vpiVectorVal;s_vpi_vecval vector[2]{{0x80000001u,0x80000000u},{0x80000001u,1u}};value.value.vector=vector;
 vpi_put_value(dataHandle,&value,nullptr,vpiNoDelay);check(!vpi_chk_error(nullptr));vpi_get_value(dataHandle,&value);check(value.value.vector[0].aval==vector[0].aval&&value.value.vector[0].bval==vector[0].bval&&value.value.vector[1].aval==vector[1].aval&&value.value.vector[1].bval==vector[1].bval);
 value.format=vpiIntVal;vpi_get_value(dataHandle,&value);check(vpi_chk_error(nullptr));
 std::string binary=std::string(60,'0')+"10XZ";value.format=vpiBinStrVal;value.value.str=binary.data();vpi_put_value(dataHandle,&value,nullptr,vpiNoDelay);check(!vpi_chk_error(nullptr)&&model.data.bits().substr(60)=="10xz");
 auto before=model.data.bits();value.value.str=const_cast<char*>("bad");vpi_put_value(dataHandle,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr)&&model.data.bits()==before);
 value.format=vpiIntVal;value.value.integer=9;vpi_put_value(q,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));
 value.value.integer=-1;vpi_put_value(dataHandle,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));value.value.integer=9;
 s_vpi_time delay{vpiSimTime,0,1,0};vpi_put_value(dataHandle,&value,&delay,vpiNoDelay);check(vpi_chk_error(nullptr));vpi_put_value(dataHandle,&value,nullptr,2);check(vpi_chk_error(nullptr));
 s_cb_data cb{};cb.reason=cbStartOfSimulation;cb.cb_rtn=started;check(vpi_register_cb(&cb));cb.reason=cbEndOfSimulation;cb.cb_rtn=ended;check(vpi_register_cb(&cb));
 s_vpi_value callbackValue{};callbackValue.format=vpiVectorVal;s_vpi_time callbackTime{vpiSimTime,0,0,0};cb={};cb.reason=cbValueChange;cb.cb_rtn=changed;cb.obj=q;cb.value=&callbackValue;cb.time=&callbackTime;auto changeHandle=vpi_register_cb(&cb);check(changeHandle);callbackValue.format=99;callbackTime.low=99;
 model.data=12;model.eval();check(starts==1&&changes==0);model.clk=1;model.eval();check(changes==1&&uint64_t(model.q)==12);model.data=13;model.eval();check(changes==1&&uint64_t(model.q)==12);model.clk=0;model.eval();model.clk=1;model.eval();check(changes==2&&uint64_t(model.q)==13);
 check(vpi_remove_cb(changeHandle));model.clk=0;model.eval();model.data=14;model.clk=1;model.eval();check(changes==2);
 cb={};cb.reason=cbReadOnlySynch;cb.cb_rtn=ro;check(vpi_register_cb(&cb));model.eval();check(readOnly==1&&uint64_t(model.data)==14);model.eval();check(readOnly==1);
 cb={};cb.reason=cbValueChange;cb.cb_rtn=drive;cb.obj=q;auto driving=vpi_register_cb(&cb);check(driving);model.clk=0;model.eval();model.data=15;model.clk=1;model.eval();check(model.evalNeeded()&&uint64_t(model.data)==15);model.eval();check(uint64_t(model.data)==7&&!model.evalNeeded());vpi_remove_cb(driving);
 model.timeInc((uint64_t(1)<<32)+5);s_vpi_time time{vpiSimTime,0,0,0};vpi_get_time(nullptr,&time);check(time.high==1&&time.low==5);
 bool rejected=false;try{model.timeInc(UINT64_MAX);}catch(const std::runtime_error&){rejected=true;}check(rejected);
 bool wrongThread=false;std::thread thread([&](){try{model.eval();}catch(const std::runtime_error&){wrongThread=true;}});thread.join();check(wrongThread);
 TestModel other;other.useVpi();other.data=55;other.clk=1;other.eval();check(uint64_t(other.q)==55&&uint64_t(model.q)==15);check(vpi_get(vpiSize,q)==64);model.useVpi();
 check(vpi_control(vpiFinish,0));check(model.gotFinish());model.final();model.final();check(ends==1);
 rejected=false;try{model.eval();}catch(const std::runtime_error&){rejected=true;}check(rejected);
 check(vpi_free_object(dataHandle));check(!vpi_free_object(dataHandle)&&vpi_chk_error(nullptr));
 }
 check(vpi_get(vpiSize,expired)==0&&vpi_chk_error(nullptr));check(!named("test.q")&&vpi_chk_error(nullptr));
 check(vpi_get(vpiSize,reinterpret_cast<vpiHandle>(uintptr_t(1)))==0&&vpi_chk_error(nullptr));
 {
 TestModel first,other;alternate=&other;first.useVpi();s_cb_data cb{};cb.reason=cbStartOfSimulation;cb.cb_rtn=removeStart;check(vpi_register_cb(&cb));cb.cb_rtn=shouldNotStart;removedStart=vpi_register_cb(&cb);check(removedStart);cb.cb_rtn=registerFirstChange;check(vpi_register_cb(&cb));
 auto q=named("test.q");cb={};cb.reason=cbValueChange;cb.obj=q;cb.cb_rtn=switchContext;check(vpi_register_cb(&cb));cb.cb_rtn=pinnedContext;check(vpi_register_cb(&cb));
 first.data=1;first.clk=1;first.eval();check(!badStart&&pinned&&firstChange);cb={};cb.reason=cbStartOfSimulation;cb.cb_rtn=shouldNotStart;check(!vpi_register_cb(&cb)&&vpi_chk_error(nullptr));
 auto data=named("test.data");auto clk=named("test.clk");auto module=named("test");
 check(std::string(vpi_get_str(vpiName,data))=="data");check(std::string(vpi_get_str(vpiDefName,module))=="test");
 s_vpi_vlog_info info{};check(vpi_get_vlog_info(&info)&&std::string(info.product)=="VeloSim");check(!vpi_get_vlog_info(nullptr)&&vpi_chk_error(nullptr));
 s_vpi_error_info details{};check(vpi_chk_error(&details)&&details.level==vpiError&&details.message);
 check(!vpi_handle_by_name(nullptr,nullptr)&&vpi_chk_error(nullptr));check(!vpi_handle_by_name(const_cast<char*>("q"),data)&&vpi_chk_error(nullptr));
 check(!vpi_handle(999,module)&&vpi_chk_error(nullptr));check(!vpi_iterate(999,nullptr)&&vpi_chk_error(nullptr));check(!vpi_iterate(vpiPort,data)&&vpi_chk_error(nullptr));check(!vpi_scan(data)&&vpi_chk_error(nullptr));
 check(!vpi_get(999,data)&&vpi_chk_error(nullptr));check(!vpi_get_str(999,data)&&vpi_chk_error(nullptr));check(!vpi_get_str(vpiName,removedStart)&&vpi_chk_error(nullptr));
 s_vpi_value value{};value.format=999;vpi_get_value(data,&value);check(vpi_chk_error(nullptr));vpi_get_value(data,nullptr);check(vpi_chk_error(nullptr));
 value.format=vpiSuppressVal;vpi_get_value(data,&value);check(!vpi_chk_error(nullptr));value.format=vpiScalarVal;vpi_get_value(data,&value);check(vpi_chk_error(nullptr));
 for(int scalar:{vpi0,vpi1,vpiX,vpiZ}){value.format=vpiScalarVal;value.value.scalar=scalar;vpi_put_value(clk,&value,nullptr,vpiNoDelay);check(!vpi_chk_error(nullptr));vpi_get_value(clk,&value);check(!vpi_chk_error(nullptr)&&value.value.scalar==scalar);}
 first.clk=0;first.eval();
 value.format=vpiScalarVal;value.value.scalar=9;vpi_put_value(clk,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));
 value.format=vpiVectorVal;value.value.vector=nullptr;vpi_put_value(data,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));
 value.format=vpiBinStrVal;value.value.str=nullptr;vpi_put_value(data,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));
 value.value.str=const_cast<char*>("1");vpi_put_value(data,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));
 std::string invalid(64,'g');value.value.str=invalid.data();vpi_put_value(data,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));
 std::string oversized(65,'1');value.value.str=oversized.data();vpi_put_value(data,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));
 value.format=999;vpi_put_value(data,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));vpi_put_value(data,nullptr,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));
 s_vpi_time time{vpiSimTime,0,0,0};vpi_get_time(data,&time);check(!vpi_chk_error(nullptr));time.type=999;vpi_get_time(nullptr,&time);check(vpi_chk_error(nullptr));
 check(!vpi_register_cb(nullptr)&&vpi_chk_error(nullptr));cb={};check(!vpi_register_cb(&cb)&&vpi_chk_error(nullptr));cb.cb_rtn=shouldNotStart;cb.reason=999;check(!vpi_register_cb(&cb)&&vpi_chk_error(nullptr));
 cb.reason=cbReadOnlySynch;cb.obj=data;check(!vpi_register_cb(&cb)&&vpi_chk_error(nullptr));cb.obj=nullptr;time={vpiSimTime,0,1,0};cb.time=&time;check(!vpi_register_cb(&cb)&&vpi_chk_error(nullptr));
 cb.time=nullptr;value.format=999;cb.value=&value;check(!vpi_register_cb(&cb)&&vpi_chk_error(nullptr));cb.value=nullptr;
 check(!vpi_remove_cb(data)&&vpi_chk_error(nullptr));check(!vpi_control(0)&&vpi_chk_error(nullptr));
 bool failed=false;try{first.loadVpi("/missing.so");}catch(const std::runtime_error&){failed=true;}check(failed);
 first.final();value.format=vpiIntVal;value.value.integer=1;vpi_put_value(data,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));check(!vpi_register_cb(&cb)&&vpi_chk_error(nullptr));
 }
 // Wave 3: VPI force/release, cbAfterDelay, cbReadWriteSynch, vpi_get_vlog_info plusargs, and vpiMemory/vpiMemoryWord.
 {
  static int delayHits=0, rwHits=0;
  auto onDelay=[](p_cb_data)->PLI_INT32{++delayHits;return 0;};
  auto onRw=[](p_cb_data)->PLI_INT32{++rwHits;return 0;};
  struct MemModel final : vsrtl::Model {
   vsrtl::Port clk, q;
   void evaluate() override {
    if(s[0].v) s[1]=vsrtl::word(42,0,0,8); else s[1]=vsrtl::word(7,0,0,8);
   }
   MemModel():vsrtl::Model("memtop",{{"clk",1,vpiInput},{"q",8,vpiOutput}},0),clk(*this,0),q(*this,1){
    mem={std::vector<vsrtl::W>(4,vsrtl::word(0,0,0,8))};
    mem[0][2]=vsrtl::word(0x5A,0,0,8);
   }
  };
  MemModel mm; mm.useVpi();
  mm.setPlusargs({"+MODE=fast", "SEED=123"});
  s_vpi_vlog_info vi{}; check(vpi_get_vlog_info(&vi) && vi.argc==2 && std::string(vi.argv[0])=="+MODE=fast" && std::string(vi.argv[1])=="+SEED=123");
  auto mod=named("memtop"); auto qh=named("memtop.q");
  mm.clk=1; mm.eval(); check(uint64_t(mm.q)==42);
  // Force output q to 99 via VPI vpiForceFlag; eval() must preserve forced value even when clk=0 drives 7.
  s_vpi_value fv{}; fv.format=vpiIntVal; fv.value.integer=99;
  vpi_put_value(qh,&fv,nullptr,vpiForceFlag); check(!vpi_chk_error(nullptr) && mm.isForced(1) && uint64_t(mm.q)==99);
  mm.clk=0; mm.eval(); check(uint64_t(mm.q)==99);
  // Release via vpiReleaseFlag; next eval() restores driven value 7.
  vpi_put_value(qh,nullptr,nullptr,vpiReleaseFlag); check(!vpi_chk_error(nullptr) && !mm.isForced(1));
  mm.eval(); check(uint64_t(mm.q)==7);
  // cbAfterDelay and cbReadWriteSynch.
  s_vpi_time dt{vpiSimTime,0,10,0}; s_cb_data dcb{}; dcb.reason=cbAfterDelay; dcb.cb_rtn=onDelay; dcb.time=&dt;
  check(vpi_register_cb(&dcb));
  s_cb_data rwcb{}; rwcb.reason=cbReadWriteSynch; rwcb.cb_rtn=onRw;
  check(vpi_register_cb(&rwcb));
  mm.eval(); check(rwHits==1 && delayHits==0);
  mm.timeInc(5); mm.eval(); check(delayHits==0);
  mm.timeInc(5); mm.eval(); check(delayHits==1);
  mm.timeInc(10); mm.eval(); check(delayHits==1);
  // vpiMemory / vpiMemoryWord inspection and write.
  auto mit=vpi_iterate(vpiMemory,mod); check(mit);
  auto mh=vpi_scan(mit); check(mh && vpi_get(vpiType,mh)==vpiMemory && vpi_get(vpiSize,mh)==4 && !vpi_scan(mit));
  auto wit=vpi_iterate(vpiMemoryWord,mh); check(wit);
  vpiHandle w2=nullptr; int wcount=0;
  while(auto wh=vpi_scan(wit)){ if(wcount==2)w2=wh; else vpi_free_object(wh); ++wcount; }
  check(wcount==4 && w2 && vpi_get(vpiSize,w2)==8);
  s_vpi_value mv{}; mv.format=vpiIntVal; vpi_get_value(w2,&mv); check(!vpi_chk_error(nullptr) && mv.value.integer==0x5A);
  mv.value.integer=0xA5; vpi_put_value(w2,&mv,nullptr,vpiNoDelay); check(!vpi_chk_error(nullptr));
  mv.value.integer=0; vpi_get_value(w2,&mv); check(!vpi_chk_error(nullptr) && mv.value.integer==0xA5);
  vpi_free_object(w2); vpi_free_object(mh);
  mm.final();
 }
 // Wave 4: functional covergroups (bins/ranges/default/ignore/illegal/cross), SVA property stats, $fatal exitCode, and $random LCG.
 {
  struct CovModel final : vsrtl::Model {
   void evaluate() override {}
   CovModel():vsrtl::Model("covtop",{{"clk",1,vpiInput}},0){}
  };
  CovModel cm;
  cm.setRandomSeed(0x12345678ULL);
  int32_t r1=cm.nextRandom(), r2=cm.nextRandom();
  check(r1!=r2);
  cm.setRandomSeed(0x12345678ULL);
  check(cm.nextRandom()==r1 && cm.nextRandom()==r2);

  vsrtl::CovergroupSpec spec;
  spec.name="cg_bus";
  spec.points.push_back({"op",{
   {"zero",0,0,false,false,false,0},
   {"mid",1,3,false,false,false,0},
   {"ign",4,4,false,true,false,0},
   {"bad",7,7,false,false,true,0},
   {"other",0,0,true,false,false,0}
  }});
  spec.points.push_back({"mode",{
   {"m0",0,0,false,false,false,0},
   {"m1",1,1,false,false,false,0}
  }});
  spec.crosses.push_back({"op_x_mode",0,1,{}});
  size_t cgId=cm.registerCovergroup(std::move(spec));
  cm.sampleCovergroup(cgId,{0,0},{true,true}); // zero x m0
  cm.sampleCovergroup(cgId,{2,1},{true,true}); // mid x m1
  cm.sampleCovergroup(cgId,{4,0},{true,true}); // ignored op -> no op hit, no cross hit
  cm.sampleCovergroup(cgId,{6,0},{true,true}); // default(other) x m0
  bool illegalCaught=false;
  try{ cm.sampleCovergroup(cgId,{7,0},{true,true}); }catch(const std::runtime_error&){ illegalCaught=true; }
  check(illegalCaught);
  const auto& cg=cm.covergroups().at(cgId);
  check(cg.samples==5);
  check(cg.points[0].bins[0].hits==1 && cg.points[0].bins[1].hits==1 && cg.points[0].bins[2].hits==0 && cg.points[0].bins[4].hits==1);
  check(cg.crosses[0].hits[0*2+0]==1 && cg.crosses[0].hits[1*2+1]==1 && cg.crosses[0].hits[4*2+0]==1);

  size_t pid=cm.registerProperty("p_req_ack","assert");
  cm.recordPropertyVacuous(pid); cm.recordPropertyPass(pid); cm.recordPropertyPass(pid);
  check(cm.propertyStats()[pid].passes==2 && cm.propertyStats()[pid].vacuous==1 && cm.propertyStats()[pid].failures==0);

  bool fatalCaught=false;
  try{ cm.fatal(42,"boom"); }catch(const std::runtime_error&){ fatalCaught=true; }
  check(fatalCaught && cm.gotFinish() && cm.exitCode()==42);
 }
 std::cout<<"external interface tests pass\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
