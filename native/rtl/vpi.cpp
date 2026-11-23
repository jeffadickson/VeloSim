#include "external_model.h"
#include "vcd_trace.h"
#include "checkpoint.h"
#include <algorithm>
#include <charconv>
#include <cstdarg>
#include <cstdio>
#include <iostream>
#include <limits>
#include <unordered_map>
#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#endif
namespace {
using vsrtl::Model; using vsrtl::W; using vsrtl::Bits;
struct Handle {
 Model* model{}; std::weak_ptr<int> life; int kind{}; size_t id{}; size_t memIndex=0; bool released=false, active=true;
 std::vector<size_t> items; size_t cursor=0; int itemKind=0; size_t parentId=0;
 s_cb_data cb{}; s_vpi_time callbackTime{}; s_vpi_value callbackValue{}; uint64_t targetTime=0;
 std::string stringValue; s_vpi_vecval vectorValue[VSRTL_MAX_WORD_BITS / 32]{}; W previous{};
};
thread_local std::unordered_map<vpiHandle,std::unique_ptr<Handle>> handles;
thread_local Model* current=nullptr;
thread_local std::weak_ptr<int> currentLife;
thread_local svScope dpiScope=nullptr;
thread_local std::string error, resultString;
thread_local s_vpi_vecval resultVector[VSRTL_MAX_WORD_BITS / 32];
thread_local std::vector<std::string> vlogArgStrings;
thread_local std::vector<PLI_BYTE8*> vlogArgv;
Model& selected(){if(!current||currentLife.expired())throw std::runtime_error("no active VeloSim model; call useVpi()");current->checkThread();return *current;}
struct Activation {
 Model* previous=current; std::weak_ptr<int> life=currentLife;
 explicit Activation(Model& m){m.useVpi();}
 ~Activation(){current=previous;currentLife=life;}
};
Handle& handle(vpiHandle value){auto it=handles.find(value);if(it==handles.end()||it->second->released||it->second->life.expired())throw std::runtime_error("invalid, released or expired VPI handle");it->second->model->checkThread();return *it->second;}
vpiHandle make(Model& model,int kind,size_t id=0,size_t memIndex=0){if(handles.size()>=65536)throw std::runtime_error("VPI handle budget exceeded");auto h=std::make_unique<Handle>();h->model=&model;h->life=model.lifetime();h->kind=kind;h->id=id;h->memIndex=memIndex;auto key=reinterpret_cast<vpiHandle>(h.get());handles.emplace(key,std::move(h));return key;}
Handle& signal(vpiHandle h){auto& x=handle(h);if(x.kind!=vpiReg&&x.kind!=vpiPort)throw std::runtime_error("signal handle required");return x;}
template<class F> auto api(F f,decltype(f()) failure)->decltype(f()){error.clear();try{return f();}catch(const std::exception& e){error=e.what();}catch(...){error="unknown VPI failure";}return failure;}
template<class F> void action(F f){api([&](){f();return 1;},0);}
void valueFormat(int format){if(format!=vpiBinStrVal&&format!=vpiHexStrVal&&format!=vpiOctStrVal&&format!=vpiDecStrVal&&format!=vpiVectorVal&&format!=vpiIntVal&&format!=vpiScalarVal&&format!=vpiSuppressVal)throw std::runtime_error("unsupported VPI value format");}
std::string radixText(W w,int group){
 std::string out;
 for(int low=((w.w-1)/group)*group;low>=0;low-=group){
  auto bits=vsrtl::mask(std::min(group,w.w-low));auto x=(w.x>>low)&bits,z=(w.z>>low)&bits,v=(w.v>>low)&bits;
  out+=x?(x==bits?'x':'X'):z?(z==bits?'z':'Z'):"0123456789abcdef"[unsigned(v)];
 }
 return out;
}
W radixWord(const char* input,int width,int group){
 if(!input)throw std::runtime_error("null radix string");
 const int cap=width<=128?128:(width<=256?256:VSRTL_MAX_WORD_BITS);
 const int limit=(cap+group-1)/group;
 Bits v=0,x=0,z=0;bool any=false;
 for(int i=0;i<=limit;i++){
  unsigned char c=input[i];if(!c){if(!any)throw std::runtime_error("empty radix string");return vsrtl::word(v,x,z,width);}
  if(i==limit)throw std::runtime_error(cap<=256?"radix string exceeds256-bit digit budget":"radix string exceeds512-bit digit budget");
  if(c>='A'&&c<='Z')c+=32;
  if(!i){if(c=='x')x=~Bits(0);else if(c=='z')z=~Bits(0);}
  v<<=group;x<<=group;z<<=group;auto bits=vsrtl::mask(group);
  if(c=='x')x|=bits;else if(c=='z')z|=bits;else{int digit=c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:-1;if(digit<0||digit>=(1<<group))throw std::runtime_error("invalid radix digit");v|=unsigned(digit);}
  any=true;
 }
 throw std::runtime_error("invalid radix string");
}
W decimalWord(const char* input,int width){
 if(!input)throw std::runtime_error("null decimal string");
 const int cap=width<=128?128:(width<=256?256:VSRTL_MAX_WORD_BITS);
 const size_t digits=width<=128?39:(width<=256?78:155);
 size_t n=0;while(n<=digits&&input[n])++n;
 if(!n||n>digits)throw std::runtime_error(cap<=256?"decimal string requires1..78characters":"decimal string requires1..155characters");
 if(n==1&&(input[0]=='x'||input[0]=='X'))return vsrtl::unknown(width);
 if(n==1&&(input[0]=='z'||input[0]=='Z'))return vsrtl::word(0,0,~Bits(0),width);
 Bits value=0;for(size_t i=0;i<n;++i){if(input[i]<'0'||input[i]>'9')throw std::runtime_error("unsigned decimal digit error");unsigned digit=input[i]-'0';if(value>(vsrtl::mask(cap)-digit)/10)throw std::runtime_error(cap<=256?"256-bit decimal overflow":"512-bit decimal overflow");value=value*10+digit;}
 return vsrtl::word(value,0,0,width);
}
std::string decimalText(Bits v){std::string s;do{s+=char('0'+v%10);v/=10;}while(v);std::reverse(s.begin(),s.end());return s;}
void encode(W w,p_vpi_value value,std::string& buffer,s_vpi_vecval* vector,bool is_signed=false){
 valueFormat(value->format);
 switch(value->format){
 case vpiBinStrVal:buffer=vsrtl::text(w);value->value.str=buffer.data();break;
 case vpiDecStrVal:buffer=w.x?(w.x==vsrtl::mask(w.w)?"x":"X"):w.z?(w.z==vsrtl::mask(w.w)?"z":"Z"):((is_signed&&vsrtl::negative(w))?"-"+decimalText((~w.v+1)&vsrtl::mask(w.w)):decimalText(w.v));value->value.str=buffer.data();break;
 case vpiOctStrVal:buffer=radixText(w,3);value->value.str=buffer.data();break;
 case vpiHexStrVal:buffer=radixText(w,4);value->value.str=buffer.data();break;
 case vpiVectorVal:{Bits a=w.v|w.x,b=w.x|w.z;for(int i=0;i<VSRTL_MAX_WORD_BITS/32;i++)vector[i]={uint32_t(a>>(32*i)),uint32_t(b>>(32*i))};value->value.vector=vector;break;}
 case vpiIntVal:if(w.x||w.z)throw std::runtime_error("X/Z integer read; request binary or vector format");value->value.integer=static_cast<int32_t>(uint32_t(w.v));break;
 case vpiScalarVal:if(w.w!=1)throw std::runtime_error("scalar format requires width1");value->value.scalar=w.x?vpiX:w.z?vpiZ:w.v?vpi1:vpi0;break;
 default:break;
 }
}
W decodeVpiValue(p_vpi_value value,int width){
 if(!value)throw std::runtime_error("null VPI value");
 if(value->format==vpiBinStrVal){if(!value->value.str)throw std::runtime_error("null binary string");std::string bits;for(int i=0;i<=VSRTL_MAX_WORD_BITS;i++){char c=value->value.str[i];if(!c)break;if(i==VSRTL_MAX_WORD_BITS)throw std::runtime_error(VSRTL_MAX_WORD_BITS<=256?"binary value exceeds256bits":"binary value exceeds512bits");bits+=c=='X'?'x':c=='Z'?'z':c;}if(bits.size()!=size_t(width))throw std::runtime_error("binary deposit must match declared width");return vsrtl::parse(bits);}
 if(value->format==vpiDecStrVal)return decimalWord(value->value.str,width);
 if(value->format==vpiOctStrVal)return radixWord(value->value.str,width,3);
 if(value->format==vpiHexStrVal)return radixWord(value->value.str,width,4);
 if(value->format==vpiIntVal){if(width>32&&value->value.integer<0)throw std::runtime_error("negative integer deposit wider than32bits; use vector format");return vsrtl::word(uint32_t(value->value.integer),0,0,32);}
 if(value->format==vpiScalarVal){if(width!=1||value->value.scalar<0||value->value.scalar>3)throw std::runtime_error("invalid scalar deposit");int n=value->value.scalar;return vsrtl::word(n==vpi1,n==vpiX,n==vpiZ,1);}
 if(value->format==vpiVectorVal){if(!value->value.vector)throw std::runtime_error("null vector");Bits a=0,b=0;for(int i=0;i<(width+31)/32;i++){a|=Bits(value->value.vector[i].aval)<<(i*32);b|=Bits(value->value.vector[i].bval)<<(i*32);}return vsrtl::word(a&~b,a&b,~a&b,width);}
 throw std::runtime_error("unsupported VPI deposit format");
}
void timeNow(Model& m,p_vpi_time t){if(!t||t->type!=vpiSimTime)throw std::runtime_error("vpiSimTime required");auto n=m.time();t->high=uint32_t(n>>32);t->low=uint32_t(n);t->real=0;}
std::vector<Handle*> callbacks(Model& m){std::vector<Handle*> result;for(auto& [key,h]:handles)if(h->model==&m&&!h->life.expired()&&h->kind==vpiCallback&&h->active&&!h->released)result.push_back(h.get());
 // Handles are allocated in registration order; retain that order explicitly below.
 std::sort(result.begin(),result.end(),[](auto a,auto b){return a->id<b->id;});return result;}
void invoke(Handle& h){if(!h.active||h.released)return;Activation active(*h.model);auto data=h.cb;if(data.time){h.callbackTime=h.cb.time->type==vpiSuppressTime?s_vpi_time{vpiSuppressTime,0,0,0}:s_vpi_time{vpiSimTime,uint32_t(h.model->time()>>32),uint32_t(h.model->time()),0};data.time=&h.callbackTime;}if(data.value&&data.obj){auto& obj=signal(data.obj);encode(h.model->get(obj.id),&h.callbackValue,h.stringValue,h.vectorValue,obj.model->signals()[obj.id].is_signed);data.value=&h.callbackValue;}data.cb_rtn(&data);}
std::string jsonEscape(const std::string& input){
 std::string out;out.reserve(input.size()+4);
 for(unsigned char c:input){
  if(c=='\\')out+="\\\\";
  else if(c=='"')out+="\\\"";
  else if(c=='\n')out+="\\n";
  else if(c=='\r')out+="\\r";
  else if(c=='\t')out+="\\t";
  else out+=char(c);
 }
 return out;
}
uint64_t popcountBits(const Bits& b){
 uint64_t c=0;
 for(int i=0;i<Bits::kLimbs;++i){
  uint64_t x=b.limb[i];
  while(x){x&=(x-1);++c;}
 }
 return c;
}
std::string bitsHex(const Bits& b,int width){
 std::string out;
 const int nibbles=(width+3)/4;
 out.reserve(size_t(nibbles));
 for(int n=nibbles-1;n>=0;--n){
  unsigned v=unsigned((b>>(n*4))&Bits(0xFULL));
  out.push_back("0123456789abcdef"[v&0xFu]);
 }
 return out;
}
bool parsePlusargValue(const std::string& raw,char fmt,int width,W& out){
 std::string clean;
 clean.reserve(raw.size());
 for(char c:raw)if(c!='_')clean.push_back(c);
 if(clean.empty())return false;
 char f=char(fmt>='A'&&fmt<='Z'?fmt+32:fmt);
 try{
  if(f=='d'||f=='u'){
   bool neg=false;
   size_t pos=0;
   if(f=='d'&&clean[0]=='-'){neg=true;pos=1;}
   else if(clean[0]=='+'){pos=1;}
   if(pos>=clean.size())return false;
   W parsed=decimalWord(clean.c_str()+pos,width);
   if(neg){
    Bits m=vsrtl::mask(width);
    parsed=vsrtl::word((~parsed.v+Bits(1))&m,0,0,width);
   }
   out=parsed;
   return true;
  }
  if(f=='h'||f=='x'){out=radixWord(clean.c_str(),width,4);return true;}
  if(f=='o'){out=radixWord(clean.c_str(),width,3);return true;}
  if(f=='b'){out=radixWord(clean.c_str(),width,1);return true;}
 }catch(...){return false;}
 return false;
}
}
namespace vsrtl {
struct Model::State {
 std::string name; std::vector<std::string> names; std::vector<Signal> signals;
 std::shared_ptr<int> life=std::make_shared<int>(0); std::thread::id owner=std::this_thread::get_id();
 uint64_t now=0; bool finished=false,finishRequested=false,started=false,evaluating=false,dispatching=false,readOnly=false,requested=false;
 bool hasCallbacks=false;
 std::unordered_map<size_t,W> pending; std::vector<void*> plugins;
 std::unordered_map<size_t,W> forced;
 std::vector<std::string> plusargs;
 uint64_t rngState=0x9E3779B97F4A7C15ULL;
 uint32_t lcgState=0x9E3779B9u;
 int exitCode=0;
 bool toggleCoverageEnabled=false;
 std::vector<bool> toggleInitialized;
 std::vector<Bits> togglePrev0, togglePrev1, toggleRise, toggleFall;
 std::vector<CovergroupSpec> covergroups;
 std::vector<PropertyStat> properties;
 VcdWriter vcd;
 std::vector<ModelSnapshot> ttRing;
 size_t ttCapacity=0, ttHead=0, ttCount=0;
 std::function<void(const std::string&)> displaySink;

 void recordToggles(const std::vector<W>& s){
  if(!toggleCoverageEnabled)return;
  if(toggleInitialized.size()!=s.size()){
   toggleInitialized.assign(s.size(),false);
   togglePrev0.assign(s.size(),Bits(0));
   togglePrev1.assign(s.size(),Bits(0));
   toggleRise.assign(s.size(),Bits(0));
   toggleFall.assign(s.size(),Bits(0));
  }
  for(size_t i=0;i<s.size();++i){
   const Bits m=mask_unchecked(s[i].w);
   const Bits known=~(s[i].x|s[i].z)&m;
   const Bits cur1=s[i].v&known;
   const Bits cur0=~s[i].v&known;
   if(toggleInitialized[i]){
    toggleRise[i]|=togglePrev0[i]&cur1;
    toggleFall[i]|=togglePrev1[i]&cur0;
   }
   togglePrev0[i]=cur0;
   togglePrev1[i]=cur1;
   toggleInitialized[i]=true;
  }
 }
};
void Model::applyForces() noexcept {
 if (state->forced.empty()) return;
 syncToBase();
 for (const auto& [id, val] : state->forced) s[id] = val;
 syncFromBase();
}
void Model::refreshFastFlags() noexcept {
 fast_port_ok = !state->finished && !state->finishRequested && !state->readOnly && !state->dispatching && state->forced.empty();
 fast_eligible = state->started && !state->finished && !state->finishRequested && !state->hasCallbacks && state->pending.empty() && !state->dispatching && !state->readOnly && state->forced.empty() && !state->toggleCoverageEnabled;
 fast_eval_ok = fast_eligible && !fast_evaluating && !state->evaluating;
}
void Model::notifyCallbackStateChanged(bool active) noexcept {
 if (active) state->hasCallbacks = true;
 refreshFastFlags();
}
Model::Model(std::string name,std::vector<Signal> signals,int clock,int abi):state(std::make_unique<State>()),owner_thread(state->owner),fast_thread_tag(localThreadTag()){
 if(abi!=2||name.empty()||signals.empty()||signals.size()>65536||clock < -1||(clock >= 0 && (size_t(clock)>=signals.size()||signals[clock].width!=1||signals[clock].direction!=vpiInput)))throw std::invalid_argument("invalid external model interface");
 state->name=std::move(name);state->names.reserve(signals.size());s.reserve(signals.size());
 for(auto sig:signals){if(!sig.name||!*sig.name||sig.width<1||sig.width>VSRTL_MAX_WORD_BITS||(sig.direction!=vpiInput&&sig.direction!=vpiOutput&&sig.direction!=vpiNoDirection))throw std::invalid_argument("invalid external signal");if(std::find(state->names.begin(),state->names.end(),sig.name)!=state->names.end())throw std::invalid_argument("duplicate external signal");state->names.emplace_back(sig.name);s.push_back(sig.two_state?word(0,0,0,sig.width):unknown(sig.width));}
 for(size_t i=0;i<signals.size();i++)state->signals.push_back({state->names[i].c_str(),signals[i].width,signals[i].direction,signals[i].is_signed,signals[i].two_state});if(clock>=0)s[clock]=word(0,0,0,1);
 refreshFastFlags();
}
Model::~Model(){
 state->vcd.close();
 // Derived state is already gone; destruction invalidates callbacks, never calls HDL or plugins.
 for(auto& [key,h]:handles)if(h->model==this&&h->life.lock()==state->life)h->active=false;
 if(dpiScope==static_cast<svScope>(this))dpiScope=nullptr;
 state->life.reset();for(auto module:state->plugins){
#if defined(_WIN32)
 FreeLibrary(static_cast<HMODULE>(module));
#else
 dlclose(module);
#endif
 }
}
void Model::checkThread() const{if(state->owner!=std::this_thread::get_id())throw std::runtime_error("model must be used on its owning thread");}
std::weak_ptr<int> Model::lifetime() const{return state->life;}
const std::string& Model::name() const{return state->name;}
const std::vector<Signal>& Model::signals() const{return state->signals;}
W Model::get(size_t id) const{checkThread();syncToBase();return s.at(id);}
void Model::put(size_t id,W value){checkThread();if(state->finished||state->finishRequested||state->readOnly)throw std::runtime_error("model is finished or in read-only callback");if(state->signals.at(id).direction!=vpiInput)throw std::runtime_error("only input ports may be deposited");value=word(value.v,value.x,value.z,state->signals[id].width);if(state->signals[id].two_state)value=two_state(value);if(state->forced.count(id)){fast_dirty=true;return;}if(state->dispatching){state->pending[id]=value;refreshFastFlags();}else{syncToBase();s.at(id)=value;syncFromBase();}fast_dirty=true;}
void Model::useVpi(){checkThread();current=this;currentLife=state->life;}
uint64_t Model::time() const{checkThread();return state->now;}
void Model::timeInc(uint64_t delta){checkThread();if(fast_evaluating||state->evaluating||gotFinish()||delta>UINT64_MAX-state->now)throw std::runtime_error("invalid time advance");state->now+=delta;
 // Time is an evaluation input for suspended HDL and timed callbacks. Mark a
 // positive advance dirty even through a base Model reference, preserving ABI 2.
 // Zero advances do not invent work; explicit requestEval remains available.
 if(delta)fast_dirty=true;
}
bool Model::gotFinish() const{checkThread();return state->finished||state->finishRequested;}
int Model::exitCode() const{checkThread();return state->exitCode;}
bool Model::evalNeeded() const{checkThread();return !gotFinish()&&fast_dirty;}
bool Model::started() const{checkThread();return state->started;}
void Model::requestEval(){checkThread();if(gotFinish())throw std::runtime_error("finished model evaluation request");state->requested=true;fast_dirty=true;}
void Model::finish(){checkThread();state->finishRequested=true;refreshFastFlags();}
void Model::fatal(int code,const std::string& message){
 checkThread();
 state->exitCode=code;
 state->finishRequested=true;
 refreshFastFlags();
 throw std::runtime_error(message.empty()?("RTL $fatal("+std::to_string(code)+")"):("RTL $fatal("+std::to_string(code)+"): "+message));
}
void Model::evalSlow(){
 checkThread();if(fast_evaluating||state->evaluating||gotFinish())throw std::runtime_error("reentrant or finished model evaluation");
 if(!state->hasCallbacks&&state->pending.empty()){
  state->started=true;state->evaluating=true;state->requested=false;fast_dirty=false;refreshFastFlags();
  try{
   if(!state->forced.empty()){syncToBase();for(const auto& [id,val]:state->forced)s[id]=val;syncFromBase();}
   evaluate();
   if(!state->forced.empty()){syncToBase();for(const auto& [id,val]:state->forced)s[id]=val;syncFromBase();}
   if(state->toggleCoverageEnabled){syncToBase();state->recordToggles(s);}
  }catch(...){state->evaluating=false;fast_dirty=true;refreshFastFlags();throw;}
  state->evaluating=false;fast_dirty=!state->pending.empty()||state->requested;refreshFastFlags();return;
 }
 Activation active(*this);state->evaluating=true;refreshFastFlags();
 try{
 state->requested=false;
 auto list=callbacks(*this);
 if(!state->started){state->started=true;state->dispatching=true;refreshFastFlags();for(auto h:list)if(h->cb.reason==cbStartOfSimulation){invoke(*h);h->active=false;}state->dispatching=false;refreshFastFlags();}
 if(state->finishRequested){state->evaluating=false;refreshFastFlags();return;}
 // Fire ready cbAfterDelay callbacks whose target simulation time has been reached.
 state->dispatching=true;refreshFastFlags();
 for(auto h:callbacks(*this))if(h->active&&h->cb.reason==cbAfterDelay&&h->targetTime<=state->now){invoke(*h);h->active=false;}
 state->dispatching=false;refreshFastFlags();
 if(state->finishRequested){state->evaluating=false;refreshFastFlags();return;}
 list=callbacks(*this); // Include value callbacks registered by simulation-start or delay callbacks.
 syncToBase();
 for(auto [id,w]:state->pending)if(!state->forced.count(id))s[id]=w;state->pending.clear();fast_dirty=false;
 for(const auto& [id,val]:state->forced)s[id]=val;
 syncFromBase();
 evaluate();
 syncToBase();
 for(const auto& [id,val]:state->forced)s[id]=val;
 if(!state->forced.empty())syncFromBase();
 state->recordToggles(s);
 // Snapshot changed values before any callback can queue a new drive.
 std::vector<Handle*> changed;for(auto h:list)if(h->active&&h->cb.reason==cbValueChange){auto w=get(signal(h->cb.obj).id);if(!exact(w,h->previous)){h->previous=w;changed.push_back(h);}}
 state->dispatching=true;refreshFastFlags();for(auto h:changed)invoke(*h);
 for(auto h:callbacks(*this))if(h->active&&h->cb.reason==cbReadWriteSynch){invoke(*h);h->active=false;}
 state->readOnly=true;refreshFastFlags();for(auto h:callbacks(*this))if(h->active&&h->cb.reason==cbReadOnlySynch){invoke(*h);h->active=false;}state->readOnly=false;state->dispatching=false;state->evaluating=false;
 auto remaining=callbacks(*this);
 state->hasCallbacks=!remaining.empty();
 fast_dirty=!state->pending.empty()||state->requested;
 for(auto h:remaining)if(h->cb.reason==cbReadOnlySynch||h->cb.reason==cbReadWriteSynch||(h->cb.reason==cbAfterDelay&&h->targetTime<=state->now))fast_dirty=true;
 refreshFastFlags();
 }catch(...){state->readOnly=false;state->dispatching=false;state->evaluating=false;fast_dirty=true;refreshFastFlags();throw;}
}
void Model::final(){checkThread();if(fast_evaluating||state->evaluating)throw std::runtime_error("reentrant final()");if(state->finished)return;Activation active(*this);state->finished=true;state->evaluating=true;state->readOnly=true;refreshFastFlags();try{if(state->hasCallbacks)for(auto h:callbacks(*this))if(h->cb.reason==cbEndOfSimulation){invoke(*h);h->active=false;}state->vcd.close();state->evaluating=false;state->readOnly=false;refreshFastFlags();}catch(...){state->vcd.close();state->evaluating=false;state->readOnly=false;refreshFastFlags();throw;}}
void Model::openVcd(const std::string& path,const std::string& timescale,const std::vector<std::string>& filter_prefixes){checkThread();state->vcd.open(path,state->name,state->signals,timescale,false,filter_prefixes);}
void Model::openVcdGz(const std::string& path,const std::string& timescale,const std::vector<std::string>& filter_prefixes){checkThread();state->vcd.open(path,state->name,state->signals,timescale,true,filter_prefixes);}
void Model::dumpVcd(){checkThread();syncToBase();state->vcd.dump(state->now,s);}
void Model::closeVcd(){checkThread();state->vcd.close();}
void Model::saveCheckpoint(const std::string& path) const{checkThread();if(fast_evaluating||state->evaluating)throw std::runtime_error("cannot save checkpoint during evaluation");syncToBase();std::vector<uint8_t> extra;saveExtraState(extra);checkpoint::save(path,state->name,state->signals,state->now,s,mem,extra);}
void Model::restoreCheckpoint(const std::string& path){checkThread();if(fast_evaluating||state->evaluating)throw std::runtime_error("cannot restore checkpoint during evaluation");uint64_t nextNow=state->now;auto nextS=s;auto nextMem=mem;std::vector<uint8_t> extra;checkpoint::load(path,state->name,state->signals,nextNow,nextS,nextMem,extra);restoreExtraState(extra.data(),extra.size());state->now=nextNow;s=std::move(nextS);mem=std::move(nextMem);syncFromBase();state->pending.clear();fast_dirty=true;refreshFastFlags();}
ModelSnapshot Model::snapshot() const{
 checkThread();
 if(fast_evaluating||state->evaluating)throw std::runtime_error("cannot snapshot during evaluation");
 syncToBase();
 ModelSnapshot snap;
 snap.schema=checkpoint::schema_hash(state->name,state->signals,mem);
 snap.time=state->now;
 snap.rngState=state->rngState;
 snap.lcgState=state->lcgState;
 snap.started=state->started;
 snap.finished=state->finished;
 snap.finishRequested=state->finishRequested;
 snap.exitCode=state->exitCode;
 snap.s=s;
 snap.mem=mem;
 saveExtraState(snap.extra);
 snap.forced.reserve(state->forced.size());
 for(const auto& kv:state->forced)snap.forced.emplace_back(kv.first,kv.second);
 snap.properties=state->properties;
 return snap;
}
void Model::restore(const ModelSnapshot& snap){
 checkThread();
 if(fast_evaluating||state->evaluating)throw std::runtime_error("cannot restore snapshot during evaluation");
 uint64_t expected=checkpoint::schema_hash(state->name,state->signals,mem);
 if(snap.schema!=expected||snap.s.size()!=s.size()||snap.mem.size()!=mem.size())
  throw std::runtime_error("snapshot schema mismatch");
 restoreExtraState(snap.extra.data(),snap.extra.size());
 state->now=snap.time;
 state->rngState=snap.rngState;
 state->lcgState=snap.lcgState;
 state->started=snap.started;
 state->finished=snap.finished;
 state->finishRequested=snap.finishRequested;
 state->exitCode=snap.exitCode;
 s=snap.s;
 mem=snap.mem;
 state->forced.clear();
 for(const auto& kv:snap.forced)state->forced.emplace(kv.first,kv.second);
 if(snap.properties.size()==state->properties.size())state->properties=snap.properties;
 syncFromBase();
 state->pending.clear();
 fast_dirty=true;
 refreshFastFlags();
}
void Model::enableTimeTravel(size_t capacity){
 checkThread();
 state->ttRing.clear();
 state->ttCapacity=capacity;
 state->ttHead=0;
 state->ttCount=0;
 if(capacity>0)state->ttRing.resize(capacity);
}
void Model::recordTimeTravel(){
 checkThread();
 if(state->ttCapacity==0)return;
 state->ttRing[state->ttHead]=snapshot();
 state->ttHead=(state->ttHead+1)%state->ttCapacity;
 if(state->ttCount<state->ttCapacity)++state->ttCount;
}
size_t Model::timeTravelDepth() const{
 checkThread();
 return state->ttCount;
}
bool Model::rewindTimeTravel(size_t steps_back){
 checkThread();
 if(state->ttCount==0||steps_back>=state->ttCount)return false;
 size_t idx=(state->ttHead+state->ttCapacity-1-steps_back)%state->ttCapacity;
 restore(state->ttRing[idx]);
 state->ttHead=(idx+1)%state->ttCapacity;
 state->ttCount-=steps_back;
 return true;
}
size_t Model::dumpTimeTravelVcd(const std::string& path,const std::string& timescale,bool gzip,const std::vector<std::string>& filter_prefixes) const{
 checkThread();
 if(state->ttCount==0)return 0;
 VcdWriter writer;
 writer.open(path,state->name,state->signals,timescale,gzip,filter_prefixes);
 size_t oldest=(state->ttHead+state->ttCapacity-state->ttCount)%state->ttCapacity;
 for(size_t i=0;i<state->ttCount;++i){
  const auto& frame=state->ttRing[(oldest+i)%state->ttCapacity];
  writer.dump(frame.time,frame.s);
 }
 writer.close();
 return state->ttCount;
}
size_t Model::dumpTimeTravelOnFailure(const std::string& path,const std::string& timescale,bool gzip,const std::vector<std::string>& filter_prefixes) const{
 checkThread();
 bool failed=state->exitCode!=0;
 for(const auto& p:state->properties)if(p.failures>0)failed=true;
 if(!failed)return 0;
 return dumpTimeTravelVcd(path,timescale,gzip,filter_prefixes);
}
size_t Model::loadMemoryHex(size_t mem_id,const std::string& path,size_t start_addr,size_t end_addr){checkThread();size_t loaded=0;if(loadDerivedMemory(mem_id,path,true,start_addr,end_addr,loaded))return loaded;if(mem_id>=mem.size()||mem[mem_id].empty())throw std::out_of_range("invalid memory id for loadMemoryHex");return load_mem_file(mem[mem_id],mem[mem_id][0].w,path,true,start_addr,end_addr);}
size_t Model::loadMemoryBin(size_t mem_id,const std::string& path,size_t start_addr,size_t end_addr){checkThread();size_t loaded=0;if(loadDerivedMemory(mem_id,path,false,start_addr,end_addr,loaded))return loaded;if(mem_id>=mem.size()||mem[mem_id].empty())throw std::out_of_range("invalid memory id for loadMemoryBin");return load_mem_file(mem[mem_id],mem[mem_id][0].w,path,false,start_addr,end_addr);}
W Model::readMemoryWord(size_t mem_id,size_t addr) const{checkThread();if(mem_id>=mem.size()||addr>=mem[mem_id].size())throw std::out_of_range("memory word address out of range");return mem[mem_id][addr];}
void Model::writeMemoryWord(size_t mem_id,size_t addr,W value){checkThread();if(state->finished||state->finishRequested||state->readOnly)throw std::runtime_error("model is finished or in read-only callback");if(mem_id>=mem.size()||addr>=mem[mem_id].size())throw std::out_of_range("memory word address out of range");mem[mem_id][addr]=word(value.v,value.x,value.z,mem[mem_id][addr].w);fast_dirty=true;}
void Model::setDisplaySink(std::function<void(const std::string&)> sink){checkThread();state->displaySink=std::move(sink);}
void Model::emitDisplay(const std::string& text){checkThread();if(state->displaySink)state->displaySink(text);else std::cout<<text;}
void Model::setPlusargs(std::vector<std::string> args){
 checkThread();
 state->plusargs.clear();
 state->plusargs.reserve(args.size());
 for(auto& a:args){
  if(!a.empty()&&a[0]=='+')a.erase(0,1);
  state->plusargs.push_back(std::move(a));
 }
}
void Model::addPlusarg(std::string arg){
 checkThread();
 if(!arg.empty()&&arg[0]=='+')arg.erase(0,1);
 state->plusargs.push_back(std::move(arg));
}
const std::vector<std::string>& Model::plusargs() const{checkThread();return state->plusargs;}
uint32_t Model::testPlusargs(const std::string& prefix) const{
 checkThread();
 std::string p=prefix;
 if(!p.empty()&&p[0]=='+')p.erase(0,1);
 for(const auto& a:state->plusargs)if(a.rfind(p,0)==0)return 1u;
 return 0u;
}
uint32_t Model::valuePlusargs(const std::string& spec,W& out) const{
 checkThread();
 std::string sspec=spec;
 if(!sspec.empty()&&sspec[0]=='+')sspec.erase(0,1);
 size_t pct=sspec.find('%');
 if(pct==std::string::npos||pct+1>=sspec.size())return 0u;
 std::string prefix=sspec.substr(0,pct);
 size_t fi=pct+1;
 if(sspec[fi]=='0'&&fi+1<sspec.size())++fi;
 char fmt=sspec[fi];
 for(const auto& a:state->plusargs){
  if(a.rfind(prefix,0)==0){
   if(parsePlusargValue(a.substr(prefix.size()),fmt,out.w,out))return 1u;
  }
 }
 return 0u;
}
void Model::setRandomSeed(uint64_t seed){checkThread();state->rngState=seed;state->lcgState=static_cast<uint32_t>(seed^(seed>>32));}
uint64_t Model::randomSeed() const{checkThread();return state->rngState;}
uint32_t Model::nextUrandom(){
 checkThread();
 uint64_t z=(state->rngState+=0x9E3779B97F4A7C15ULL);
 z=(z^(z>>30))*0xBF58476D1CE4E5B9ULL;
 z=(z^(z>>27))*0x94D049BB133111EBULL;
 z^=(z>>31);
 return static_cast<uint32_t>(z);
}
uint32_t Model::nextUrandomRange(uint32_t max_val,uint32_t min_val){
 checkThread();
 if(max_val<min_val)std::swap(max_val,min_val);
 uint64_t span=uint64_t(max_val)-uint64_t(min_val)+1ULL;
 uint32_t r=nextUrandom();
 if(span>=0x100000000ULL)return r;
 return static_cast<uint32_t>(min_val+(uint64_t(r)%span));
}
int32_t Model::nextRandom(){
 checkThread();
 state->lcgState=static_cast<uint32_t>(69069u*state->lcgState+1u);
 return static_cast<int32_t>(state->lcgState);
}
void Model::forceSignal(size_t id,W value){
 checkThread();
 if(state->finished||state->finishRequested||state->readOnly)throw std::runtime_error("model is finished or in read-only callback");
 if(id>=state->signals.size())throw std::out_of_range("invalid signal id for forceSignal");
 value=word(value.v,value.x,value.z,state->signals[id].width);
 if(state->signals[id].two_state)value=two_state(value);
 syncToBase();
 state->forced[id]=value;
 s[id]=value;
 syncFromBase();
 fast_dirty=true;
 refreshFastFlags();
}
void Model::releaseSignal(size_t id){
 checkThread();
 if(state->finished||state->finishRequested||state->readOnly)throw std::runtime_error("model is finished or in read-only callback");
 if(id>=state->signals.size())throw std::out_of_range("invalid signal id for releaseSignal");
 state->forced.erase(id);
 fast_dirty=true;
 refreshFastFlags();
}
bool Model::isForced(size_t id) const{checkThread();return state->forced.count(id)!=0;}
void Model::enableToggleCoverage(bool enabled){
 checkThread();
 state->toggleCoverageEnabled=enabled;
 if(enabled){
  syncToBase();
  state->toggleInitialized.assign(s.size(),false);
  state->togglePrev0.assign(s.size(),Bits(0));
  state->togglePrev1.assign(s.size(),Bits(0));
  state->toggleRise.assign(s.size(),Bits(0));
  state->toggleFall.assign(s.size(),Bits(0));
  state->recordToggles(s);
 }
 refreshFastFlags();
}
std::vector<ToggleCoverage> Model::toggleCoverage() const{
 checkThread();
 std::vector<ToggleCoverage> out;
 if(!state->toggleCoverageEnabled)return out;
 out.reserve(state->signals.size());
 for(size_t i=0;i<state->signals.size();++i){
  Bits r=i<state->toggleRise.size()?state->toggleRise[i]:Bits(0);
  Bits f=i<state->toggleFall.size()?state->toggleFall[i]:Bits(0);
  out.push_back(ToggleCoverage{state->signals[i].name,state->signals[i].width,r,f});
 }
 return out;
}
size_t Model::registerCovergroup(CovergroupSpec spec){
 checkThread();
 for(auto& cr:spec.crosses){
  if(cr.cp_a>=spec.points.size()||cr.cp_b>=spec.points.size())
   throw std::out_of_range("invalid coverpoint index in cross specification");
  cr.hits.assign(spec.points[cr.cp_a].bins.size()*spec.points[cr.cp_b].bins.size(),0);
 }
 state->covergroups.push_back(std::move(spec));
 return state->covergroups.size()-1;
}
void Model::sampleCovergroup(size_t cg_id,const std::vector<uint64_t>& values,const std::vector<bool>& known){
 checkThread();
 if(cg_id>=state->covergroups.size())throw std::out_of_range("invalid covergroup id");
 auto& cg=state->covergroups[cg_id];
 if(values.size()!=cg.points.size()||known.size()!=cg.points.size())
  throw std::invalid_argument("covergroup sample arity mismatch");
 ++cg.samples;
 std::vector<int64_t> matched(cg.points.size(),-1);
 for(size_t pi=0;pi<cg.points.size();++pi){
  if(!known[pi])continue;
  uint64_t val=values[pi];
  auto& bins=cg.points[pi].bins;
  // Pass 1: check illegal_bins and ignore_bins first (IEEE 1800 §19.5.4/19.5.5).
  bool ignored=false;
  for(size_t bi=0;bi<bins.size();++bi){
   const auto& b=bins[bi];
   if(b.is_default)continue;
   if(val>=b.lo&&val<=b.hi){
    if(b.is_illegal)throw std::runtime_error("illegal_bins hit in covergroup '"+cg.name+"."+cg.points[pi].name+"."+b.name+"' value="+std::to_string(val));
    if(b.is_ignore){ignored=true;break;}
   }
  }
  if(ignored)continue;
  // Pass 2: match explicit/range bins.
  int64_t defaultIdx=-1;
  bool anyHit=false;
  for(size_t bi=0;bi<bins.size();++bi){
   auto& b=bins[bi];
   if(b.is_ignore||b.is_illegal)continue;
   if(b.is_default){if(defaultIdx<0)defaultIdx=int64_t(bi);continue;}
   if(val>=b.lo&&val<=b.hi){
    ++b.hits;
    if(matched[pi]<0)matched[pi]=int64_t(bi);
    anyHit=true;
   }
  }
  if(!anyHit&&defaultIdx>=0){
   ++bins[size_t(defaultIdx)].hits;
   matched[pi]=defaultIdx;
  }
 }
 for(auto& cr:cg.crosses){
  int64_t a=matched[cr.cp_a],b=matched[cr.cp_b];
  if(a>=0&&b>=0){
   size_t cols=cg.points[cr.cp_b].bins.size();
   size_t idx=size_t(a)*cols+size_t(b);
   if(idx<cr.hits.size())++cr.hits[idx];
  }
 }
}
const std::vector<CovergroupSpec>& Model::covergroups() const{checkThread();return state->covergroups;}
size_t Model::registerProperty(std::string name,std::string kind){
 checkThread();
 state->properties.push_back(PropertyStat{std::move(name),std::move(kind),0,0,0});
 return state->properties.size()-1;
}
void Model::recordPropertyPass(size_t prop_id){checkThread();if(prop_id<state->properties.size())++state->properties[prop_id].passes;}
void Model::recordPropertyFail(size_t prop_id){checkThread();if(prop_id<state->properties.size())++state->properties[prop_id].failures;}
void Model::recordPropertyVacuous(size_t prop_id){checkThread();if(prop_id<state->properties.size())++state->properties[prop_id].vacuous;}
const std::vector<PropertyStat>& Model::propertyStats() const{checkThread();return state->properties;}
void Model::writeCoverageJson(const std::string& path) const{
 checkThread();
 auto pts=coveragePoints();
 auto togs=toggleCoverage();
 uint64_t covered=0;
 for(const auto& p:pts)if(p.hits>0)++covered;
 std::ofstream out(path,std::ios::binary|std::ios::trunc);
 if(!out)throw std::runtime_error("failed to open coverage file for writing: "+path);
 out<<"{\"schema\":\"velosim.dut.coverage.v1\",\"model\":\""<<jsonEscape(state->name)<<"\",\"total\":"<<pts.size()<<",\"covered\":"<<covered<<",\"points\":[";
 for(size_t i=0;i<pts.size();++i){
  if(i)out<<',';
  out<<"{\"id\":\""<<jsonEscape(pts[i].id?pts[i].id:"")<<"\",\"kind\":\""<<jsonEscape(pts[i].kind?pts[i].kind:"")<<"\",\"hits\":"<<pts[i].hits<<'}';
 }
 out<<"],\"toggles\":[";
 for(size_t i=0;i<togs.size();++i){
  if(i)out<<',';
  out<<"{\"signal\":\""<<jsonEscape(togs[i].signal?togs[i].signal:"")<<"\",\"width\":"<<togs[i].width
     <<",\"rise_hex\":\""<<bitsHex(togs[i].rise,togs[i].width)<<"\",\"fall_hex\":\""<<bitsHex(togs[i].fall,togs[i].width)
     <<"\",\"rise_hits\":"<<popcountBits(togs[i].rise)<<",\"fall_hits\":"<<popcountBits(togs[i].fall)<<'}';
 }
 out<<']';
 if(!state->covergroups.empty()){
  out<<",\"covergroups\":[";
  for(size_t gi=0;gi<state->covergroups.size();++gi){
   if(gi)out<<',';
   const auto& cg=state->covergroups[gi];
   out<<"{\"name\":\""<<jsonEscape(cg.name)<<"\",\"samples\":"<<cg.samples<<",\"coverpoints\":[";
   for(size_t pi=0;pi<cg.points.size();++pi){
    if(pi)out<<',';
    const auto& cp=cg.points[pi];
    out<<"{\"name\":\""<<jsonEscape(cp.name)<<"\",\"bins\":[";
    for(size_t bi=0;bi<cp.bins.size();++bi){
     if(bi)out<<',';
     const auto& b=cp.bins[bi];
     out<<"{\"name\":\""<<jsonEscape(b.name)<<"\",\"lo\":"<<b.lo<<",\"hi\":"<<b.hi
        <<",\"default\":"<<(b.is_default?"true":"false")<<",\"ignore\":"<<(b.is_ignore?"true":"false")
        <<",\"illegal\":"<<(b.is_illegal?"true":"false")<<",\"hits\":"<<b.hits<<'}';
    }
    out<<"]}";
   }
   out<<"],\"crosses\":[";
   for(size_t ci=0;ci<cg.crosses.size();++ci){
    if(ci)out<<',';
    const auto& cr=cg.crosses[ci];
    const auto& ba=cg.points[cr.cp_a].bins;
    const auto& bb=cg.points[cr.cp_b].bins;
    out<<"{\"name\":\""<<jsonEscape(cr.name)<<"\",\"cp_a\":\""<<jsonEscape(cg.points[cr.cp_a].name)
       <<"\",\"cp_b\":\""<<jsonEscape(cg.points[cr.cp_b].name)<<"\",\"bins\":[";
    bool first=true;
    for(size_t ai=0;ai<ba.size();++ai){
     for(size_t bi=0;bi<bb.size();++bi){
      if(!first)out<<',';
      first=false;
      size_t idx=ai*bb.size()+bi;
      out<<"{\"a\":\""<<jsonEscape(ba[ai].name)<<"\",\"b\":\""<<jsonEscape(bb[bi].name)<<"\",\"hits\":"<<(idx<cr.hits.size()?cr.hits[idx]:0)<<'}';
     }
    }
    out<<"]}";
   }
   out<<"]}";
  }
  out<<']';
 }
 if(!state->properties.empty()){
  out<<",\"properties\":[";
  for(size_t i=0;i<state->properties.size();++i){
   if(i)out<<',';
   const auto& p=state->properties[i];
   out<<"{\"name\":\""<<jsonEscape(p.name)<<"\",\"kind\":\""<<jsonEscape(p.kind)
      <<"\",\"passes\":"<<p.passes<<",\"failures\":"<<p.failures<<",\"vacuous\":"<<p.vacuous<<'}';
  }
  out<<']';
 }
 out<<"}\n";
 if(!out)throw std::runtime_error("failed writing coverage file: "+path);
}
void Model::loadVpi(const std::string& path){
 checkThread();if(fast_evaluating||state->evaluating||state->started||gotFinish()||state->plugins.size()>=64)throw std::runtime_error("load VPI plugins before first eval; maximum64");Activation active(*this);
#if defined(_WIN32)
 auto module=LoadLibraryA(path.c_str());if(!module)throw std::runtime_error("cannot load VPI plugin");auto startup=reinterpret_cast<void(**)()>(GetProcAddress(module,"vlog_startup_routines"));
#else
 auto module=dlopen(path.c_str(),RTLD_NOW|RTLD_LOCAL);if(!module)throw std::runtime_error(std::string("cannot load VPI plugin: ")+dlerror());auto startup=reinterpret_cast<void(**)()>(dlsym(module,"vlog_startup_routines"));
#endif
 // Keep even failed startup modules loaded until model destruction, protecting registered pointers.
 state->plugins.push_back(module);if(!startup)throw std::runtime_error("VPI plugin lacks vlog_startup_routines");
 for(size_t i=0;i<256;i++){if(!startup[i])return;error.clear();startup[i]();if(!error.empty())throw std::runtime_error("VPI startup: "+error);}throw std::runtime_error("VPI startup routine budget exceeded");
}
}
extern "C" {
vpiHandle vpi_handle_by_name(PLI_BYTE8* name,vpiHandle scope){return api([&]()->vpiHandle{if(!name)throw std::runtime_error("null VPI name");Model* m=&selected();std::string n=name;if(scope){auto& h=handle(scope);if(h.kind!=vpiModule)throw std::runtime_error("module scope required");m=h.model;}std::string prefix=m->name()+".";if(n==m->name()||n=="TOP."+m->name())return make(*m,vpiModule);if(n.rfind("TOP.",0)==0)n.erase(0,4);if(n.rfind(prefix,0)==0)n.erase(0,prefix.size());else if(!scope)return nullptr;for(size_t i=0;i<m->signals().size();i++)if(n==m->signals()[i].name)return make(*m,vpiReg,i);return nullptr;},vpiHandle{});}
vpiHandle vpi_handle(PLI_INT32 relation,vpiHandle object){return api([&]()->vpiHandle{auto& h=handle(object);if((relation==vpiScope||relation==vpiModule)&&(h.kind==vpiReg||h.kind==vpiPort||h.kind==vpiMemory||h.kind==vpiMemoryWord))return make(*h.model,vpiModule);if(relation==vpiScope&&h.kind==vpiModule)return nullptr;if(relation!=vpiLowConn||h.kind!=vpiPort)throw std::runtime_error("unsupported VPI relation");return make(*h.model,vpiReg,h.id);},vpiHandle{});}
vpiHandle vpi_iterate(PLI_INT32 kind,vpiHandle scope){return api([&]()->vpiHandle{
 Model* m=&selected();
 if(!scope){
  if(kind!=vpiModule)throw std::runtime_error("only top-module iteration allowed without scope");
  std::vector<size_t> items{0};
  auto key=make(*m,vpiIterator);auto& it=handle(key);it.items=std::move(items);it.itemKind=vpiModule;return key;
 }
 auto& h=handle(scope);m=h.model;
 if(h.kind==vpiMemory&&kind==vpiMemoryWord){
  size_t depth=m->memoryDepth(h.id);
  if(depth==0)return nullptr;
  std::vector<size_t> items(depth);
  for(size_t i=0;i<depth;++i)items[i]=i;
  auto key=make(*m,vpiIterator);auto& it=handle(key);it.items=std::move(items);it.itemKind=vpiMemoryWord;it.parentId=h.id;return key;
 }
 if(h.kind!=vpiModule||(kind!=vpiReg&&kind!=vpiPort&&kind!=vpiMemory))throw std::runtime_error("unsupported VPI iterator");
 std::vector<size_t> items;
 if(kind==vpiMemory){
  for(size_t i=0;i<m->memoryCount();++i)items.push_back(i);
 }else{
  for(size_t i=0;i<m->signals().size();i++)if(kind==vpiReg||m->signals()[i].direction!=vpiNoDirection)items.push_back(i);
 }
 if(items.empty())return nullptr;
 auto key=make(*m,vpiIterator);auto& it=handle(key);it.items=std::move(items);it.itemKind=kind;return key;
},vpiHandle{});}
vpiHandle vpi_scan(vpiHandle iterator){return api([&]()->vpiHandle{auto& h=handle(iterator);if(h.kind!=vpiIterator)throw std::runtime_error("iterator required");if(h.cursor==h.items.size()){h.released=true;return nullptr;}size_t item=h.items[h.cursor++];if(h.itemKind==vpiMemoryWord)return make(*h.model,vpiMemoryWord,h.parentId,item);return make(*h.model,h.itemKind,item);},vpiHandle{});}
PLI_INT32 vpi_free_object(vpiHandle object){return api([&](){auto& h=handle(object);h.released=true;h.active=false;return PLI_INT32(1);},PLI_INT32(0));}
PLI_INT32 vpi_compare_objects(vpiHandle left,vpiHandle right){return api([&](){auto& a=handle(left);auto& b=handle(right);return PLI_INT32(a.model==b.model&&a.kind==b.kind&&((a.kind==vpiReg||a.kind==vpiPort||a.kind==vpiModule||a.kind==vpiMemory)?a.id==b.id:a.kind==vpiMemoryWord?(a.id==b.id&&a.memIndex==b.memIndex):left==right));},PLI_INT32(0));}
PLI_INT32 vpi_get(PLI_INT32 property,vpiHandle object){return api([&](){
 auto& h=handle(object);
 if(property==vpiType)return PLI_INT32(h.kind);
 if(property==vpiTopModule&&h.kind==vpiModule)return PLI_INT32(1);
 if(h.kind==vpiMemory){if(property==vpiSize)return PLI_INT32(h.model->memoryDepth(h.id));throw std::runtime_error("unsupported VPI property");}
 if(h.kind==vpiMemoryWord){if(property==vpiSize)return PLI_INT32(h.model->memoryWidth(h.id));throw std::runtime_error("unsupported VPI property");}
 auto& x=signal(object);
 if(property==vpiScalar)return PLI_INT32(x.model->signals()[x.id].width==1);
 if(property==vpiVector)return PLI_INT32(x.model->signals()[x.id].width>1);
 if(property==vpiSigned)return PLI_INT32(x.model->signals()[x.id].is_signed);
 if(property==vpiSize)return PLI_INT32(x.model->signals()[x.id].width);
 if(property==vpiDirection)return PLI_INT32(x.model->signals()[x.id].direction);
 throw std::runtime_error("unsupported VPI property");
},PLI_INT32(0));}
PLI_BYTE8* vpi_get_str(PLI_INT32 property,vpiHandle object){return api([&]()->PLI_BYTE8*{
 auto& h=handle(object);
 if(h.kind==vpiMemory){
  std::string local="mem_"+std::to_string(h.id);
  if(property==vpiName)resultString=local;
  else if(property==vpiFullName)resultString=h.model->name()+"."+local;
  else throw std::runtime_error("unsupported VPI string property");
  return resultString.data();
 }
 if(h.kind==vpiMemoryWord){
  std::string local="mem_"+std::to_string(h.id)+"["+std::to_string(h.memIndex)+"]";
  if(property==vpiName)resultString=local;
  else if(property==vpiFullName)resultString=h.model->name()+"."+local;
  else throw std::runtime_error("unsupported VPI string property");
  return resultString.data();
 }
 if(h.kind!=vpiModule&&h.kind!=vpiReg&&h.kind!=vpiPort)throw std::runtime_error("named object required");
 std::string local=h.kind==vpiModule?h.model->name():h.model->signals()[h.id].name;
 if(property==vpiName)resultString=local;
 else if(property==vpiFullName)resultString=h.kind==vpiModule?local:h.model->name()+"."+local;
 else if(property==vpiDefName&&h.kind==vpiModule)resultString=local;
 else throw std::runtime_error("unsupported VPI string property");
 return resultString.data();
},static_cast<PLI_BYTE8*>(nullptr));}
void vpi_get_value(vpiHandle object,p_vpi_value value){action([&](){
 if(!value)throw std::runtime_error("null VPI value");
 auto& raw=handle(object);
 if(raw.kind==vpiMemoryWord){
  encode(raw.model->readMemoryWord(raw.id,raw.memIndex),value,resultString,resultVector,false);
  return;
 }
 auto& h=signal(object);
 encode(h.model->get(h.id),value,resultString,resultVector,h.model->signals()[h.id].is_signed);
});}
vpiHandle vpi_put_value(vpiHandle object,p_vpi_value value,p_vpi_time time,PLI_INT32 flags){return api([&]()->vpiHandle{
 if(time&&(time->type!=vpiSimTime&&time->type!=vpiSuppressTime||time->high||time->low))throw std::runtime_error("only immediate input deposits supported");
 auto& raw=handle(object);
 if(flags==vpiReleaseFlag){
  auto& h=signal(object);
  h.model->releaseSignal(h.id);
  return nullptr;
 }
 if(flags!=vpiNoDelay&&flags!=vpiForceFlag)throw std::runtime_error("only immediate input deposits supported");
 if(!value)throw std::runtime_error("only immediate input deposits supported");
 if(raw.kind==vpiMemoryWord){
  if(flags!=vpiNoDelay)throw std::runtime_error("force/release unsupported on memory words");
  int width=raw.model->memoryWidth(raw.id);
  W w=decodeVpiValue(value,width);
  raw.model->writeMemoryWord(raw.id,raw.memIndex,w);
  return nullptr;
 }
 auto& h=signal(object);
 int width=h.model->signals()[h.id].width;
 W w=decodeVpiValue(value,width);
 if(flags==vpiForceFlag)h.model->forceSignal(h.id,w);
 else h.model->put(h.id,w);
 return nullptr;
},vpiHandle{});}
void vpi_get_time(vpiHandle object,p_vpi_time time){action([&](){auto& m=object?*handle(object).model:selected();timeNow(m,time);});}
vpiHandle vpi_register_cb(p_cb_data data){return api([&]()->vpiHandle{
 if(!data||!data->cb_rtn)throw std::runtime_error("callback routine required");
 auto& m=selected();
 if(m.gotFinish())throw std::runtime_error("finished model");
 if(data->reason!=cbValueChange&&data->reason!=cbReadOnlySynch&&data->reason!=cbReadWriteSynch&&data->reason!=cbAfterDelay&&data->reason!=cbStartOfSimulation&&data->reason!=cbEndOfSimulation)
  throw std::runtime_error("unsupported callback reason");
 uint64_t targetTime=m.time();
 if(data->reason==cbAfterDelay){
  if(!data->time||data->time->type!=vpiSimTime)throw std::runtime_error("cbAfterDelay requires vpiSimTime");
  uint64_t delay=(uint64_t(data->time->high)<<32)|uint64_t(data->time->low);
  if(delay>UINT64_MAX-targetTime)throw std::runtime_error("cbAfterDelay time overflow");
  targetTime+=delay;
 }else{
  if(data->time&&(data->time->type!=vpiSimTime&&data->time->type!=vpiSuppressTime||data->time->high||data->time->low))
   throw std::runtime_error("only zero-delay callbacks supported");
 }
 if(data->value)valueFormat(data->value->format);
 vpiHandle object=nullptr;W previous{};
 if(data->reason==cbValueChange){
  auto& obj=signal(data->obj);
  if(obj.model!=&m)throw std::runtime_error("callback model mismatch");
  previous=m.get(obj.id);
  if(data->value&&data->value->format==vpiScalarVal&&previous.w!=1)throw std::runtime_error("scalar callback format requires width1");
  object=make(m,vpiReg,obj.id);
 }else if(data->obj){
  throw std::runtime_error("lifecycle callback object must be null");
 }
 if(data->reason==cbStartOfSimulation&&m.started())throw std::runtime_error("start callbacks must be registered before first eval");
 size_t order=handles.size();
 auto key=make(m,vpiCallback,order);
 auto& h=handle(key);
 h.cb=*data;h.cb.obj=object;h.previous=previous;h.targetTime=targetTime;
 if(data->time){h.callbackTime=*data->time;h.cb.time=&h.callbackTime;}
 if(data->value){h.callbackValue=*data->value;h.cb.value=&h.callbackValue;}
 m.notifyCallbackStateChanged(true);
 if(data->reason==cbReadOnlySynch||data->reason==cbReadWriteSynch||(data->reason==cbAfterDelay&&targetTime<=m.time()))m.requestEval();
 return key;
},vpiHandle{});}
PLI_INT32 vpi_remove_cb(vpiHandle object){return api([&](){auto& h=handle(object);if(h.kind!=vpiCallback)throw std::runtime_error("callback handle required");h.active=false;return PLI_INT32(1);},PLI_INT32(0));}
PLI_INT32 vpi_chk_error(p_vpi_error_info info){if(error.empty())return 0;if(info)*info={vpiRun,vpiError,error.data(),const_cast<char*>("VeloSim"),const_cast<char*>("VS_VPI"),nullptr,0};return vpiError;}
PLI_INT32 vpi_control(PLI_INT32 operation,...){return api([&](){if(operation!=vpiFinish)throw std::runtime_error("only vpiFinish supported");selected().finish();return PLI_INT32(1);},PLI_INT32(0));}
PLI_INT32 vpi_printf(PLI_BYTE8* format,...){if(!format)return 0;va_list args;va_start(args,format);int n=std::vprintf(format,args);va_end(args);return n;}
PLI_INT32 vpi_get_vlog_info(p_vpi_vlog_info info){return api([&](){
 if(!info)throw std::runtime_error("null vlog info");
 vlogArgStrings.clear();vlogArgv.clear();
 if(current&&!currentLife.expired()){
  const auto& pa=current->plusargs();
  if(!pa.empty()){
   vlogArgStrings.reserve(pa.size());
   vlogArgv.reserve(pa.size());
   for(const auto& a:pa){vlogArgStrings.push_back("+"+a);}
   for(auto& s:vlogArgStrings)vlogArgv.push_back(s.data());
  }
 }
 *info={PLI_INT32(vlogArgv.size()),vlogArgv.empty()?nullptr:vlogArgv.data(),const_cast<char*>("VeloSim"),const_cast<char*>("external.v2")};
 return PLI_INT32(1);
},PLI_INT32(0));}
svScope svGetScope(void){
 if(dpiScope)return dpiScope;
 if(current&&!currentLife.expired())return static_cast<svScope>(current);
 return nullptr;
}
svScope svSetScope(const svScope scope){
 svScope prev=svGetScope();
 dpiScope=scope;
 return prev;
}
const char* svGetNameFromScope(const svScope scope){
 if(!scope)return nullptr;
 return reinterpret_cast<const Model*>(scope)->name().c_str();
}
svScope svGetScopeFromName(const char* scopeName){
 if(!scopeName)return nullptr;
 if(current&&!currentLife.expired()&&current->name()==scopeName)return static_cast<svScope>(current);
 if(dpiScope&&reinterpret_cast<const Model*>(dpiScope)->name()==scopeName)return dpiScope;
 return nullptr;
}
}
