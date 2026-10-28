#include "external_model.h"
#include <algorithm>
#include <charconv>
#include <cstdarg>
#include <cstdio>
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
 Model* model{}; std::weak_ptr<int> life; int kind{}; size_t id{}; bool released=false, active=true;
 std::vector<size_t> items; size_t cursor=0; int itemKind=0;
 s_cb_data cb{}; s_vpi_time callbackTime{}; s_vpi_value callbackValue{};
 std::string stringValue; s_vpi_vecval vectorValue[8]{}; W previous{};
};
thread_local std::unordered_map<vpiHandle,std::unique_ptr<Handle>> handles;
thread_local Model* current=nullptr;
thread_local std::weak_ptr<int> currentLife;
thread_local std::string error, resultString;
thread_local s_vpi_vecval resultVector[8];
Model& selected(){if(!current||currentLife.expired())throw std::runtime_error("no active VeloSim model; call useVpi()");current->checkThread();return *current;}
struct Activation {
 Model* previous=current; std::weak_ptr<int> life=currentLife;
 explicit Activation(Model& m){m.useVpi();}
 ~Activation(){current=previous;currentLife=life;}
};
Handle& handle(vpiHandle value){auto it=handles.find(value);if(it==handles.end()||it->second->released||it->second->life.expired())throw std::runtime_error("invalid, released or expired VPI handle");it->second->model->checkThread();return *it->second;}
vpiHandle make(Model& model,int kind,size_t id=0){if(handles.size()>=65536)throw std::runtime_error("VPI handle budget exceeded");auto h=std::make_unique<Handle>();h->model=&model;h->life=model.lifetime();h->kind=kind;h->id=id;auto key=reinterpret_cast<vpiHandle>(h.get());handles.emplace(key,std::move(h));return key;}
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
 const int limit=((width<=128?128:256)+group-1)/group;
 Bits v=0,x=0,z=0;bool any=false;
 for(int i=0;i<=limit;i++){
  unsigned char c=input[i];if(!c){if(!any)throw std::runtime_error("empty radix string");return vsrtl::word(v,x,z,width);}
  if(i==limit)throw std::runtime_error("radix string exceeds256-bit digit budget");
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
 size_t n=0;const size_t digits=width<=128?39:78;while(n<=digits&&input[n])++n;
 if(!n||n>digits)throw std::runtime_error("decimal string requires1..78characters");
 if(n==1&&(input[0]=='x'||input[0]=='X'))return vsrtl::unknown(width);
 if(n==1&&(input[0]=='z'||input[0]=='Z'))return vsrtl::word(0,0,~Bits(0),width);
 Bits value=0;for(size_t i=0;i<n;++i){if(input[i]<'0'||input[i]>'9')throw std::runtime_error("unsigned decimal digit error");unsigned digit=input[i]-'0';if(value>(vsrtl::mask(width<=128?128:256)-digit)/10)throw std::runtime_error("256-bit decimal overflow");value=value*10+digit;}
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
 case vpiVectorVal:{Bits a=w.v|w.x,b=w.x|w.z;for(int i=0;i<8;i++)vector[i]={uint32_t(a>>(32*i)),uint32_t(b>>(32*i))};value->value.vector=vector;break;}
 case vpiIntVal:if(w.x||w.z)throw std::runtime_error("X/Z integer read; request binary or vector format");value->value.integer=static_cast<int32_t>(uint32_t(w.v));break;
 case vpiScalarVal:if(w.w!=1)throw std::runtime_error("scalar format requires width1");value->value.scalar=w.x?vpiX:w.z?vpiZ:w.v?vpi1:vpi0;break;
 default:break;
 }
}
void timeNow(Model& m,p_vpi_time t){if(!t||t->type!=vpiSimTime)throw std::runtime_error("vpiSimTime required");auto n=m.time();t->high=uint32_t(n>>32);t->low=uint32_t(n);t->real=0;}
std::vector<Handle*> callbacks(Model& m){std::vector<Handle*> result;for(auto& [key,h]:handles)if(h->model==&m&&!h->life.expired()&&h->kind==vpiCallback&&h->active&&!h->released)result.push_back(h.get());
 // Handles are allocated in registration order; retain that order explicitly below.
 std::sort(result.begin(),result.end(),[](auto a,auto b){return a->id<b->id;});return result;}
void invoke(Handle& h){if(!h.active||h.released)return;Activation active(*h.model);auto data=h.cb;if(data.time){h.callbackTime=h.cb.time->type==vpiSuppressTime?s_vpi_time{vpiSuppressTime,0,0,0}:s_vpi_time{vpiSimTime,uint32_t(h.model->time()>>32),uint32_t(h.model->time()),0};data.time=&h.callbackTime;}if(data.value&&data.obj){auto& obj=signal(data.obj);encode(h.model->get(obj.id),&h.callbackValue,h.stringValue,h.vectorValue,obj.model->signals()[obj.id].is_signed);data.value=&h.callbackValue;}data.cb_rtn(&data);}
}
namespace vsrtl {
struct Model::State {
 std::string name; std::vector<std::string> names; std::vector<Signal> signals;
 std::shared_ptr<int> life=std::make_shared<int>(0); std::thread::id owner=std::this_thread::get_id();
 uint64_t now=0; bool finished=false,finishRequested=false,started=false,evaluating=false,dispatching=false,readOnly=false,dirty=true,requested=false;
 std::unordered_map<size_t,W> pending; std::vector<void*> plugins;
};
Model::Model(std::string name,std::vector<Signal> signals,int clock,int abi):state(std::make_unique<State>()){
 if(abi!=2||name.empty()||signals.empty()||signals.size()>256||clock < -1||(clock >= 0 && (size_t(clock)>=signals.size()||signals[clock].width!=1||signals[clock].direction!=vpiInput)))throw std::invalid_argument("invalid external model interface");
 state->name=std::move(name);state->names.reserve(signals.size());
 for(auto sig:signals){if(!sig.name||!*sig.name||sig.width<1||sig.width>256||(sig.direction!=vpiInput&&sig.direction!=vpiOutput&&sig.direction!=vpiNoDirection))throw std::invalid_argument("invalid external signal");if(std::find(state->names.begin(),state->names.end(),sig.name)!=state->names.end())throw std::invalid_argument("duplicate external signal");state->names.emplace_back(sig.name);s.push_back(sig.two_state?word(0,0,0,sig.width):unknown(sig.width));}
 for(size_t i=0;i<signals.size();i++)state->signals.push_back({state->names[i].c_str(),signals[i].width,signals[i].direction,signals[i].is_signed,signals[i].two_state});if(clock>=0)s[clock]=word(0,0,0,1);
}
Model::~Model(){
 // Derived state is already gone; destruction invalidates callbacks, never calls HDL or plugins.
 for(auto& [key,h]:handles)if(h->model==this&&h->life.lock()==state->life)h->active=false;
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
W Model::get(size_t id) const{checkThread();return s.at(id);}
void Model::put(size_t id,W value){checkThread();if(state->finished||state->finishRequested||state->readOnly)throw std::runtime_error("model is finished or in read-only callback");if(state->signals.at(id).direction!=vpiInput)throw std::runtime_error("only input ports may be deposited");value=word(value.v,value.x,value.z,state->signals[id].width);if(state->signals[id].two_state)value=two_state(value);if(state->dispatching)state->pending[id]=value;else s.at(id)=value;state->dirty=true;}
void Model::useVpi(){checkThread();current=this;currentLife=state->life;}
uint64_t Model::time() const{checkThread();return state->now;}
void Model::timeInc(uint64_t delta){checkThread();if(state->evaluating||gotFinish()||delta>UINT64_MAX-state->now)throw std::runtime_error("invalid time advance");state->now+=delta;
 // Time is an evaluation input for suspended HDL and timed callbacks. Mark a
 // positive advance dirty even through a base Model reference, preserving ABI 2.
 // Zero advances do not invent work; explicit requestEval remains available.
 if(delta)state->dirty=true;
}
bool Model::gotFinish() const{checkThread();return state->finished||state->finishRequested;}
bool Model::evalNeeded() const{checkThread();return !gotFinish()&&state->dirty;}
bool Model::started() const{checkThread();return state->started;}
void Model::requestEval(){checkThread();if(gotFinish())throw std::runtime_error("finished model evaluation request");state->requested=true;state->dirty=true;}
void Model::finish(){checkThread();state->finishRequested=true;}
void Model::eval(){
 checkThread();if(state->evaluating||gotFinish())throw std::runtime_error("reentrant or finished model evaluation");Activation active(*this);state->evaluating=true;
 try{
 state->requested=false;
 auto list=callbacks(*this);
 if(!state->started){state->started=true;state->dispatching=true;for(auto h:list)if(h->cb.reason==cbStartOfSimulation){invoke(*h);h->active=false;}state->dispatching=false;}
 if(state->finishRequested){state->evaluating=false;return;}
 list=callbacks(*this); // Include value callbacks registered by simulation-start callbacks.
 for(auto [id,w]:state->pending)s[id]=w;state->pending.clear();state->dirty=false;evaluate();
 // Snapshot changed values before any callback can queue a new drive.
 std::vector<Handle*> changed;for(auto h:list)if(h->active&&h->cb.reason==cbValueChange){auto w=get(signal(h->cb.obj).id);if(!exact(w,h->previous)){h->previous=w;changed.push_back(h);}}
 state->dispatching=true;for(auto h:changed)invoke(*h);
 state->readOnly=true;for(auto h:callbacks(*this))if(h->active&&h->cb.reason==cbReadOnlySynch){invoke(*h);h->active=false;}state->readOnly=false;state->dispatching=false;state->evaluating=false;
 state->dirty=!state->pending.empty()||state->requested;for(auto h:callbacks(*this))if(h->cb.reason==cbReadOnlySynch)state->dirty=true;
 }catch(...){state->readOnly=false;state->dispatching=false;state->evaluating=false;state->dirty=true;throw;}
}
void Model::final(){checkThread();if(state->evaluating)throw std::runtime_error("reentrant final()");if(state->finished)return;Activation active(*this);state->finished=true;state->evaluating=true;state->readOnly=true;try{for(auto h:callbacks(*this))if(h->cb.reason==cbEndOfSimulation){invoke(*h);h->active=false;}state->evaluating=false;state->readOnly=false;}catch(...){state->evaluating=false;state->readOnly=false;throw;}}
void Model::loadVpi(const std::string& path){
 checkThread();if(state->evaluating||state->started||gotFinish()||state->plugins.size()>=64)throw std::runtime_error("load VPI plugins before first eval; maximum64");Activation active(*this);
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
vpiHandle vpi_handle(PLI_INT32 relation,vpiHandle object){return api([&]()->vpiHandle{auto& h=handle(object);if((relation==vpiScope||relation==vpiModule)&&(h.kind==vpiReg||h.kind==vpiPort))return make(*h.model,vpiModule);if(relation==vpiScope&&h.kind==vpiModule)return nullptr;if(relation!=vpiLowConn||h.kind!=vpiPort)throw std::runtime_error("unsupported VPI relation");return make(*h.model,vpiReg,h.id);},vpiHandle{});}
vpiHandle vpi_iterate(PLI_INT32 kind,vpiHandle scope){return api([&]()->vpiHandle{Model* m=&selected();if(!scope){if(kind!=vpiModule)throw std::runtime_error("only top-module iteration allowed without scope");}else{auto& h=handle(scope);if(h.kind!=vpiModule||(kind!=vpiReg&&kind!=vpiPort))throw std::runtime_error("unsupported VPI iterator");m=h.model;}std::vector<size_t> items;if(kind==vpiModule)items.push_back(0);else for(size_t i=0;i<m->signals().size();i++)if(kind==vpiReg||m->signals()[i].direction!=vpiNoDirection)items.push_back(i);if(items.empty())return nullptr;auto key=make(*m,vpiIterator);auto& h=handle(key);h.items=std::move(items);h.itemKind=kind;return key;},vpiHandle{});}
vpiHandle vpi_scan(vpiHandle iterator){return api([&]()->vpiHandle{auto& h=handle(iterator);if(h.kind!=vpiIterator)throw std::runtime_error("iterator required");if(h.cursor==h.items.size()){h.released=true;return nullptr;}return make(*h.model,h.itemKind,h.items[h.cursor++]);},vpiHandle{});}
PLI_INT32 vpi_free_object(vpiHandle object){return api([&](){auto& h=handle(object);h.released=true;h.active=false;return PLI_INT32(1);},PLI_INT32(0));}
PLI_INT32 vpi_compare_objects(vpiHandle left,vpiHandle right){return api([&](){auto& a=handle(left);auto& b=handle(right);return PLI_INT32(a.model==b.model&&a.kind==b.kind&&((a.kind==vpiReg||a.kind==vpiPort||a.kind==vpiModule)?a.id==b.id:left==right));},PLI_INT32(0));}
PLI_INT32 vpi_get(PLI_INT32 property,vpiHandle object){return api([&](){auto& h=handle(object);if(property==vpiType)return PLI_INT32(h.kind);if(property==vpiTopModule&&h.kind==vpiModule)return PLI_INT32(1);auto& x=signal(object);if(property==vpiScalar)return PLI_INT32(x.model->signals()[x.id].width==1);if(property==vpiVector)return PLI_INT32(x.model->signals()[x.id].width>1);if(property==vpiSigned)return PLI_INT32(x.model->signals()[x.id].is_signed);if(property==vpiSize)return PLI_INT32(x.model->signals()[x.id].width);if(property==vpiDirection)return PLI_INT32(x.model->signals()[x.id].direction);throw std::runtime_error("unsupported VPI property");},PLI_INT32(0));}
PLI_BYTE8* vpi_get_str(PLI_INT32 property,vpiHandle object){return api([&]()->PLI_BYTE8*{auto& h=handle(object);if(h.kind!=vpiModule&&h.kind!=vpiReg&&h.kind!=vpiPort)throw std::runtime_error("named object required");std::string local=h.kind==vpiModule?h.model->name():h.model->signals()[h.id].name;if(property==vpiName)resultString=local;else if(property==vpiFullName)resultString=h.kind==vpiModule?local:h.model->name()+"."+local;else if(property==vpiDefName&&h.kind==vpiModule)resultString=local;else throw std::runtime_error("unsupported VPI string property");return resultString.data();},static_cast<PLI_BYTE8*>(nullptr));}
void vpi_get_value(vpiHandle object,p_vpi_value value){action([&](){if(!value)throw std::runtime_error("null VPI value");auto& h=signal(object);encode(h.model->get(h.id),value,resultString,resultVector,h.model->signals()[h.id].is_signed);});}
vpiHandle vpi_put_value(vpiHandle object,p_vpi_value value,p_vpi_time time,PLI_INT32 flags){return api([&]()->vpiHandle{
 if(!value||flags!=vpiNoDelay||(time&&(time->type!=vpiSimTime&&time->type!=vpiSuppressTime||time->high||time->low)))throw std::runtime_error("only immediate input deposits supported");auto& h=signal(object);int width=h.model->signals()[h.id].width;W w{};
 if(value->format==vpiBinStrVal){if(!value->value.str)throw std::runtime_error("null binary string");std::string bits;for(int i=0;i<=256;i++){char c=value->value.str[i];if(!c)break;if(i==256)throw std::runtime_error("binary value exceeds256bits");bits+=c=='X'?'x':c=='Z'?'z':c;}if(bits.size()!=size_t(width))throw std::runtime_error("binary deposit must match declared width");w=vsrtl::parse(bits);}
 else if(value->format==vpiDecStrVal)w=decimalWord(value->value.str,width);
 else if(value->format==vpiOctStrVal)w=radixWord(value->value.str,width,3);
 else if(value->format==vpiHexStrVal)w=radixWord(value->value.str,width,4);
 else if(value->format==vpiIntVal){if(width>32&&value->value.integer<0)throw std::runtime_error("negative integer deposit wider than32bits; use vector format");w=vsrtl::word(uint32_t(value->value.integer),0,0,32);}
 else if(value->format==vpiScalarVal){if(width!=1||value->value.scalar<0||value->value.scalar>3)throw std::runtime_error("invalid scalar deposit");int n=value->value.scalar;w=vsrtl::word(n==vpi1,n==vpiX,n==vpiZ,1);}
 else if(value->format==vpiVectorVal){if(!value->value.vector)throw std::runtime_error("null vector");Bits a=0,b=0;for(int i=0;i<(width+31)/32;i++){a|=Bits(value->value.vector[i].aval)<<(i*32);b|=Bits(value->value.vector[i].bval)<<(i*32);}w=vsrtl::word(a&~b,a&b,~a&b,width);}
 else throw std::runtime_error("unsupported VPI deposit format");h.model->put(h.id,w);return nullptr;
 },vpiHandle{});}
void vpi_get_time(vpiHandle object,p_vpi_time time){action([&](){auto& m=object?*handle(object).model:selected();timeNow(m,time);});}
vpiHandle vpi_register_cb(p_cb_data data){return api([&]()->vpiHandle{
 if(!data||!data->cb_rtn)throw std::runtime_error("callback routine required");auto& m=selected();if(m.gotFinish())throw std::runtime_error("finished model");if(data->reason!=cbValueChange&&data->reason!=cbReadOnlySynch&&data->reason!=cbStartOfSimulation&&data->reason!=cbEndOfSimulation)throw std::runtime_error("unsupported callback reason");if(data->time&&(data->time->type!=vpiSimTime&&data->time->type!=vpiSuppressTime||data->time->high||data->time->low))throw std::runtime_error("only zero-delay callbacks supported");if(data->value)valueFormat(data->value->format);
 vpiHandle object=nullptr;W previous{};if(data->reason==cbValueChange){auto& obj=signal(data->obj);if(obj.model!=&m)throw std::runtime_error("callback model mismatch");previous=m.get(obj.id);if(data->value&&data->value->format==vpiScalarVal&&previous.w!=1)throw std::runtime_error("scalar callback format requires width1");object=make(m,vpiReg,obj.id);}else if(data->obj)throw std::runtime_error("lifecycle callback object must be null");
 if(data->reason==cbStartOfSimulation&&m.started())throw std::runtime_error("start callbacks must be registered before first eval");
 size_t order=handles.size();auto key=make(m,vpiCallback,order);auto& h=handle(key);h.cb=*data;h.cb.obj=object;h.previous=previous;if(data->time){h.callbackTime=*data->time;h.cb.time=&h.callbackTime;}if(data->value){h.callbackValue=*data->value;h.cb.value=&h.callbackValue;}if(data->reason==cbReadOnlySynch)m.requestEval();return key;
 },vpiHandle{});}
PLI_INT32 vpi_remove_cb(vpiHandle object){return api([&](){auto& h=handle(object);if(h.kind!=vpiCallback)throw std::runtime_error("callback handle required");h.active=false;return PLI_INT32(1);},PLI_INT32(0));}
PLI_INT32 vpi_chk_error(p_vpi_error_info info){if(error.empty())return 0;if(info)*info={vpiRun,vpiError,error.data(),const_cast<char*>("VeloSim"),const_cast<char*>("VS_VPI"),nullptr,0};return vpiError;}
PLI_INT32 vpi_control(PLI_INT32 operation,...){return api([&](){if(operation!=vpiFinish)throw std::runtime_error("only vpiFinish supported");selected().finish();return PLI_INT32(1);},PLI_INT32(0));}
PLI_INT32 vpi_printf(PLI_BYTE8* format,...){if(!format)return 0;va_list args;va_start(args,format);int n=std::vprintf(format,args);va_end(args);return n;}
PLI_INT32 vpi_get_vlog_info(p_vpi_vlog_info info){return api([&](){if(!info)throw std::runtime_error("null vlog info");*info={0,nullptr,const_cast<char*>("VeloSim"),const_cast<char*>("external.v2")};return PLI_INT32(1);},PLI_INT32(0));}
}
