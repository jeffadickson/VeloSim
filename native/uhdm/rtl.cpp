// Bounded, structural UHDM exporter for one-clock unsigned sequential RTL.
// No source-text pattern matching and no FIFO-specific state transition logic.
#include <uhdm/uhdm.h>
#include <uhdm/ExprEval.h>
#include <uhdm/vpi_user.h>
#include <uhdm/vpi_uhdm.h>
#include "json_escape.h"
#include <fstream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>
namespace {
using H=vpiHandle;
void need(bool b,const std::string& s){if(!b)throw std::runtime_error(s);}
H one(H h,int r){return h?vpi_handle(r,h):nullptr;}
int type(H h){return h?vpi_get(vpiType,h):-1;}
std::string name(H h){const char* p=h?vpi_get_str(vpiName,h):nullptr;return p?p:"";}
std::vector<H> many(H h,int r){std::vector<H> v;H it=h?vpi_iterate(r,h):nullptr;if(it){for(H x=vpi_scan(it);x;x=vpi_scan(it))v.push_back(x);vpi_release_handle(it);}return v;}
std::string q(const std::string& s){return velosim::json_escape(s);}
H mod; UHDM::Serializer* store; size_t nodes=0;
int width(H h){UHDM::ExprEval e;bool bad=false;auto n=e.size(h,bad,mod,H(nullptr),true,true);need(!bad&&n>=1&&n<=64,"unsigned word width must be 1..64: "+name(h));return int(n);}
void logic_word(H h){auto spec=one(one(h,vpiTypespec),vpiActual);need(type(spec)==vpiLogicTypespec&&!vpi_get(vpiSigned,spec),"only unsigned four-state logic words supported");}
std::string bits(H h){need(type(h)==vpiConstant,"constant required");UHDM::ExprEval eval;auto object=static_cast<const UHDM::constant*>(reinterpret_cast<const uhdm_handle*>(h)->object);std::string s=eval.toBinary(object);need(!s.empty()&&s.size()<=64&&s.find_first_not_of("01xzXZ")==std::string::npos,"unsupported constant");for(char& c:s)if(c=='X'||c=='Z')c+=32;return s;}
uint64_t integer(H h){if(type(h)==vpiOperation){auto a=many(h,vpiOperand);need(a.size()==2,"range expression unsupported");auto l=integer(a[0]),r=integer(a[1]);int op=vpi_get(vpiOpType,h);need(op==vpiAddOp||op==vpiSubOp,"range operation unsupported");return op==vpiAddOp?l+r:l-r;}need(type(h)==vpiConstant,"constant range required");auto s=bits(h);need(s.find_first_not_of("01")==std::string::npos,"unknown array bound");return std::stoull(s,nullptr,2);}
bool signed_expression(H h,int depth=0){
 need(h&&depth<64,"signedness depth exceeded");
 if(type(h)==vpiConstant){auto spec=one(one(h,vpiTypespec),vpiActual);const char* p=vpi_get_str(vpiDecompile,h);std::string literal=p?p:"";return vpi_get(vpiSigned,h)||(spec&&vpi_get(vpiSigned,spec))||vpi_get(vpiConstType,h)==vpiIntConst||literal.find("'s")!=std::string::npos||literal.find("'S")!=std::string::npos;}
 if(type(one(h,vpiActual))==vpiParameter)return true; // int parameter expressions are conservatively signed.
 if(type(h)!=vpiOperation)return false; // declarations are validated unsigned separately.
 int op=vpi_get(vpiOpType,h);auto a=many(h,vpiOperand);
 if(op==vpiConcatOp||op==vpiNotOp||op==vpiLogAndOp||op==vpiLogOrOp||(op>=vpiUnaryAndOp&&op<=vpiUnaryXNorOp)||(op>=vpiEqOp&&op<=vpiLeOp))return false;
 if(op==vpiConditionOp)return a.size()==3&&signed_expression(a[1],depth+1)&&signed_expression(a[2],depth+1);
 if(op==vpiLShiftOp||op==vpiRShiftOp||op==vpiArithLShiftOp||op==vpiArithRShiftOp)return !a.empty()&&signed_expression(a[0],depth+1);
 for(auto operand:a)if(!signed_expression(operand,depth+1))return false;
 return !a.empty();
}
std::string expr(H h,int depth=0){
 need(h&&depth<64&&++nodes<=20000,"expression missing or resource limit exceeded");
 if(type(one(h,vpiActual))==vpiParameter){auto p=static_cast<const UHDM::parameter*>(reinterpret_cast<const uhdm_handle*>(one(h,vpiActual))->object);need(type(one(one(one(h,vpiActual),vpiTypespec),vpiActual))==vpiIntTypespec,"only int parameters supported in expressions");need(!p->VpiValue().empty(),"unmaterialized integer parameter value unsupported");auto c=store->MakeConstant();c->VpiValue(p->VpiValue());c->VpiSize(32);c->VpiConstType(p->VpiConstType());UHDM::ExprEval eval;auto b=eval.toBinary(c);need(!b.empty()&&b.size()<=64&&b.find_first_not_of("01")==std::string::npos,"only known integer parameters supported");need(p->VpiValue().find(":-")==std::string::npos && !(p->VpiSigned()&&b.front()=='1'),"negative signed parameter unsupported");return "{\"kind\":\"const\",\"bits\":"+q(b)+"}";}
 if(type(h)==vpiConstant){auto b=bits(h);const char* d=vpi_get_str(vpiDecompile,h);std::string literal=d?d:"";need(literal!="'1"&&literal!="'x"&&literal!="'z"&&literal!="'X"&&literal!="'Z","unbased fill literal unsupported except zero");need(!(signed_expression(h)&&b.front()=='1'),"negative signed literal unsupported");return "{\"kind\":\"const\",\"bits\":"+q(b)+"}";}
 if(type(h)==vpiBitSelect){need(type(one(h,vpiActual))==vpiArrayVar,"only unpacked memory selection supported");return "{\"kind\":\"index\",\"name\":"+q(name(h))+",\"index\":"+expr(one(h,vpiIndex),depth+1)+"}";}
 if(type(h)==vpiOperation){
  need(!signed_expression(h),"signed operation expressions unsupported");
  static const std::map<int,std::string> ops={{vpiAddOp,"add"},{vpiSubOp,"sub"},{vpiLogAndOp,"and"},{vpiNotOp,"not"},{vpiEqOp,"eq"},{vpiConcatOp,"concat"},{vpiBitAndOp,"bitand"},{vpiBitOrOp,"bitor"},{vpiBitXorOp,"bitxor"},{vpiBitNegOp,"bitnot"},
   {vpiLogOrOp,"or"},{vpiNeqOp,"neq"},{vpiCaseEqOp,"caseeq"},{vpiCaseNeqOp,"caseneq"},{vpiLtOp,"lt"},{vpiLeOp,"le"},{vpiGtOp,"gt"},{vpiGeOp,"ge"},{vpiBitXNorOp,"bitxnor"},{vpiMinusOp,"neg"},
   {vpiLShiftOp,"shl"},{vpiRShiftOp,"shr"},{vpiArithRShiftOp,"ashr"},{vpiUnaryAndOp,"redand"},{vpiUnaryNandOp,"rednand"},{vpiUnaryOrOp,"redor"},{vpiUnaryNorOp,"rednor"},{vpiUnaryXorOp,"redxor"},{vpiUnaryXNorOp,"redxnor"},{vpiPlusOp,"pos"},
   {vpiMultOp,"mul"},{vpiDivOp,"div"},{vpiModOp,"mod"},{vpiConditionOp,"cond"}};
  auto it=ops.find(vpi_get(vpiOpType,h));need(it!=ops.end(),"unsupported expression operation "+std::to_string(vpi_get(vpiOpType,h)));auto operands=many(h,vpiOperand);
  bool unary=it->second=="not"||it->second=="bitnot"||it->second=="neg"||it->second=="pos"||it->second.rfind("red",0)==0;
  need(operands.size()==(unary?1:it->second=="cond"?3:2),"unsupported expression arity");
  std::string s="{\"kind\":"+q(it->second)+",\"args\":[";bool first=true;for(auto a:operands){if(!first)s+=",";first=false;s+=expr(a,depth+1);}return s+"]}";
 }
 need(!name(h).empty()&&(type(h)==vpiReg||type(h)==vpiNet||type(h)==vpiRefObj||type(h)==vpiVariables),"unsupported reference type "+std::to_string(type(h)));
 return "{\"kind\":\"ref\",\"name\":"+q(name(h))+"}";
}
std::string stmt(H h,int depth=0){
 need(h&&depth<64&&++nodes<=20000,"statement missing or resource limit exceeded");
 if(type(h)==vpiBegin){std::string s="{\"kind\":\"block\",\"items\":[";bool first=true;for(auto a:many(h,vpiStmt)){if(!first)s+=",";first=false;s+=stmt(a,depth+1);}return s+"]}";}
 if(type(h)==vpiAssignment){need(vpi_get(vpiBlocking,h)==0,"blocking procedural assignment unsupported");need(!one(h,vpiDelayControl)&&!one(h,vpiEventControl),"assignment timing unsupported");return "{\"kind\":\"nba\",\"target\":"+expr(one(h,vpiLhs))+",\"value\":"+expr(one(h,vpiRhs))+"}";}
 if(type(h)==vpiIf||type(h)==vpiIfElse){auto e=one(h,vpiElseStmt);return "{\"kind\":\"if\",\"condition\":"+expr(one(h,vpiCondition))+",\"then\":"+stmt(one(h,vpiStmt),depth+1)+",\"else\":"+(e?stmt(e,depth+1):"{\"kind\":\"block\",\"items\":[]}")+"}";}
 if(type(h)==vpiCase){need(vpi_get(vpiCaseType,h)==vpiCaseExact,"only exact case supported");std::string s="{\"kind\":\"case\",\"value\":"+expr(one(h,vpiCondition))+",\"items\":[";bool first=true;for(auto item:many(h,vpiCaseItem)){if(!first)s+=",";first=false;auto conditions=many(item,vpiExpr);need(conditions.size()<=1,"multiple case labels unsupported");s+="{\"match\":"+(conditions.empty()?"null":expr(conditions[0]))+",\"body\":"+stmt(one(item,vpiStmt),depth+1)+"}";}return s+"]}";}
 throw std::runtime_error("unsupported statement type "+std::to_string(type(h)));
}
}
int main(int argc,char** argv){
 if(argc!=4){std::cerr<<"usage: velosim-uhdm-rtl <design.uhdm> <rtl.json> <top>\n";return 2;}
 try{
  UHDM::Serializer serializer;store=&serializer;auto designs=serializer.Restore(std::string(argv[1]));need(designs.size()==1,"one design required");auto modules=many(designs[0],UHDM::uhdmtopModules);need(modules.size()==1,"one top required");mod=modules[0];need(name(mod)==argv[3]||name(mod)==std::string("work@")+argv[3],"top mismatch");
  for(int relation:{vpiModule,vpiPrimitive,vpiPrimitiveArray,vpiGenScope,vpiGenScopeArray,vpiFunction,vpiTask})need(many(mod,relation).empty(),"hierarchy, primitives, generate scopes, functions and tasks unsupported");
  std::map<std::string,int> directions;for(auto p:many(mod,vpiPort)){int d=vpi_get(vpiDirection,p);need(d==vpiInput||d==vpiOutput,"inout unsupported");directions[name(p)]=d;}
  struct Sig{int width,direction;};std::map<std::string,Sig> signals;std::string memories="[";bool firstmem=true;
  for(int relation:{vpiNet,vpiReg,vpiVariables})for(auto h:many(mod,relation)){
   auto n=name(h);need(!n.empty(),"unnamed declaration");need(!vpi_get(vpiSigned,h),"signed declarations unsupported");need(!one(h,vpiExpr),"declaration initialization unsupported");
   if(type(h)==vpiArrayVar){auto ranges=many(h,vpiRange);auto elements=many(h,vpiReg);need(ranges.size()==1&&elements.size()==1,"only one-dimensional unpacked word arrays supported");need(!vpi_get(vpiSigned,elements[0])&&!one(elements[0],vpiExpr),"signed or initialized memory elements unsupported");logic_word(elements[0]);auto lo=integer(one(ranges[0],vpiLeftRange)),hi=integer(one(ranges[0],vpiRightRange));need(lo==0&&hi>=1&&hi<256,"memory must have ascending zero-based bounds and 2..256 words");int w=width(elements[0]);if(!firstmem)memories+=",";firstmem=false;memories+="{\"name\":"+q(n)+",\"width\":"+std::to_string(w)+",\"depth\":"+std::to_string(hi+1)+"}";
   }else{logic_word(h);if(relation==vpiNet){auto nettype=vpi_get(vpiNetType,h);need(nettype==vpiWire||nettype==vpiLogicNet,"resolved/pull net types unsupported");}int w=width(h);auto [it,inserted]=signals.emplace(n,Sig{w,directions[n]});need(inserted||it->second.width==w,"conflicting signal widths");}
  }
  need(signals.size()<=256,"signal limit exceeded");std::string out="{\"format\":\"velosim.rtl.v1\",\"signals\":[";bool first=true;for(auto& [n,s]:signals){if(!first)out+=",";first=false;out+="{\"name\":"+q(n)+",\"width\":"+std::to_string(s.width)+",\"direction\":"+q(s.direction==vpiInput?"input":s.direction==vpiOutput?"output":"internal")+"}";}out+="],\"memories\":"+memories+"],\"continuous\":[";first=true;
  for(auto c:many(mod,vpiContAssign)){auto object=static_cast<const UHDM::cont_assign*>(reinterpret_cast<const uhdm_handle*>(c)->object);need(!object->Delay()&&!object->VpiStrength0()&&!object->VpiStrength1(),"continuous delays and strengths unsupported");if(!first)out+=",";first=false;out+="{\"target\":"+expr(one(c,vpiLhs))+",\"value\":"+expr(one(c,vpiRhs))+"}";}
  auto processes=many(mod,vpiProcess);need(processes.size()==1,"exactly one sequential process required");auto p=processes[0];need(type(p)==vpiAlways&&vpi_get(vpiAlwaysType,p)==vpiAlwaysFF,"one always_ff required");auto event=one(p,vpiStmt);need(type(event)==vpiEventControl,"event control required");auto edge=one(event,vpiCondition);need(type(edge)==vpiOperation&&vpi_get(vpiOpType,edge)==vpiPosedgeOp,"only posedge supported");auto operands=many(edge,vpiOperand);need(operands.size()==1,"one clock required");
  out+="],\"clock\":"+q(name(operands[0]))+",\"body\":"+stmt(one(event,vpiStmt))+"}\n";
  std::ofstream file(argv[2],std::ios::binary|std::ios::trunc);file<<out;need(bool(file),"write failed");return 0;
 }catch(const std::exception& e){std::cerr<<"RTL lowering rejected: "<<e.what()<<"\n";return 1;}
}
