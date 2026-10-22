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
#include <algorithm>
namespace {
using H=vpiHandle;
void need(bool b,const std::string& s){if(!b)throw std::runtime_error(s);}
H one(H h,int r){return h?vpi_handle(r,h):nullptr;}
int type(H h){return h?vpi_get(vpiType,h):-1;}
std::string name(H h){const char* p=h?vpi_get_str(vpiName,h):nullptr;return p?p:"";}
std::vector<H> many(H h,int r){std::vector<H> v;H it=h?vpi_iterate(r,h):nullptr;if(it){for(H x=vpi_scan(it);x;x=vpi_scan(it))v.push_back(x);vpi_release_handle(it);}return v;}
std::string q(const std::string& s){return velosim::json_escape(s);}
H mod; UHDM::Serializer* store; size_t nodes=0;
uint64_t integer(H h,int depth=0);
bool signed_expression(H h,int depth);
uint64_t selected_constant(H h){need(type(h)==vpiConstant,"select bounds and replication counts require materialized constants");return integer(h);}
int width(H h,int depth=0){need(h&&depth<64,"word width depth exceeded");if(type(h)==vpiBitSelect&&type(one(h,vpiActual))!=vpiArrayVar)return 1;if(type(h)==vpiIndexedPartSelect){auto n=selected_constant(one(h,vpiWidthExpr));need(n>=1&&n<=64,"indexed select width must be1..64");return int(n);}if(type(h)==vpiPartSelect){auto l=selected_constant(one(h,vpiLeftRange)),r=selected_constant(one(h,vpiRightRange));need(l>=r&&l-r<64,"unsupported constant part-select width");return int(l-r+1);}if(type(h)==vpiOperation){int op=vpi_get(vpiOpType,h);auto args=many(h,vpiOperand);if(op==vpiConcatOp){need(!args.empty()&&args.size()<=64,"concat operand limit exceeded");int n=0;for(auto a:args){n+=width(a,depth+1);need(n<=64,"concat exceeds64bits");}return n;}if(op==vpiMultiConcatOp){need(args.size()==2,"replication requires count and expression");auto count=selected_constant(args[0]);int n=width(args[1],depth+1);need(count>=1&&count<=64&&count*uint64_t(n)<=64,"replication must produce1..64bits");return int(count*n);}if(op==vpiNotOp||op==vpiLogAndOp||op==vpiLogOrOp||(op>=vpiUnaryAndOp&&op<=vpiUnaryXNorOp)||(op>=vpiEqOp&&op<=vpiLeOp))return 1;need(!args.empty(),"expression width requires operands");if(op==vpiLShiftOp||op==vpiRShiftOp||op==vpiArithRShiftOp)return width(args[0],depth+1);size_t start=op==vpiConditionOp?1:0;need(start<args.size(),"conditional width requires branches");int n=0;for(size_t i=start;i<args.size();i++)n=std::max(n,width(args[i],depth+1));return n;}UHDM::ExprEval e;bool bad=false;auto n=e.size(h,bad,mod,H(nullptr),true,true);need(!bad&&n>=1&&n<=64,"unsigned word width must be 1..64: "+name(h)+" type="+std::to_string(type(h)));return int(n);}
void logic_word(H h){auto spec=one(one(h,vpiTypespec),vpiActual);need(type(spec)==vpiLogicTypespec&&!vpi_get(vpiSigned,spec),"only unsigned four-state logic words supported");auto ranges=many(spec,vpiRange);need(ranges.size()<=1,"multidimensional packed words unsupported");if(!ranges.empty())need(integer(one(ranges[0],vpiRightRange))==0&&integer(one(ranges[0],vpiLeftRange))==uint64_t(width(h)-1),"packed words require descending zero-based bounds [width-1:0]");}
std::string bits(H h){need(type(h)==vpiConstant,"constant required");UHDM::ExprEval eval;auto object=static_cast<const UHDM::constant*>(reinterpret_cast<const uhdm_handle*>(h)->object);std::string s=eval.toBinary(object);need(!s.empty()&&s.size()<=64&&s.find_first_not_of("01xzXZ")==std::string::npos,"unsupported constant");for(char& c:s)if(c=='X'||c=='Z')c+=32;return s;}
uint64_t integer(H h,int depth){need(h&&depth<64,"constant integer depth exceeded");if(type(h)==vpiOperation){auto a=many(h,vpiOperand);need(a.size()==2,"range expression unsupported");auto l=integer(a[0],depth+1),r=integer(a[1],depth+1);int op=vpi_get(vpiOpType,h);need(op==vpiAddOp||op==vpiSubOp,"range operation unsupported");return op==vpiAddOp?l+r:l-r;}need(type(h)==vpiConstant,"constant range required");auto s=bits(h);need(s.find_first_not_of("01")==std::string::npos,"unknown array bound");need(!(signed_expression(h,0)&&s.front()=='1'),"negative signed bounds and replication counts unsupported");return std::stoull(s,nullptr,2);}
std::string concat_tree(const std::vector<std::string>& values,size_t begin,size_t end){if(end-begin==1)return values[begin];auto mid=begin+(end-begin)/2;return "{\"kind\":\"concat\",\"args\":["+concat_tree(values,begin,mid)+","+concat_tree(values,mid,end)+"]}";}
void output_budget(const std::string& value){need(value.size()<=4*1024*1024,"normalized RTL exceeds4MiB");int nesting=0;size_t objects=0;bool quoted=false,escaped=false;for(char c:value){if(quoted){if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c=='\"')quoted=false;continue;}if(c=='\"')quoted=true;else if(c=='{'||c=='['){need(++nesting<=64,"normalized RTL JSON depth exceeds64");if(c=='{')need(++objects<=20000,"normalized RTL node budget exceeded");}else if(c=='}'||c==']')--nesting;}}
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
 if(type(h)==vpiBitSelect){auto actual=one(h,vpiActual);if(type(actual)==vpiArrayVar)return "{\"kind\":\"index\",\"name\":"+q(name(h))+",\"index\":"+expr(one(h,vpiIndex),depth+1)+"}";need(actual&&type(actual)!=vpiBitSelect&&type(actual)!=vpiPartSelect,"packed select requires a declared word");return "{\"kind\":\"bitselect\",\"value\":"+expr(actual,depth+1)+",\"index\":"+expr(one(h,vpiIndex),depth+1)+"}";}
 if(type(h)==vpiIndexedPartSelect){auto actual=one(h,vpiActual);need(actual&&type(actual)!=vpiArrayVar&&type(actual)!=vpiBitSelect&&type(actual)!=vpiPartSelect&&type(actual)!=vpiIndexedPartSelect,"indexed select requires a declared packed word");auto n=selected_constant(one(h,vpiWidthExpr));need(n>=1&&n<=64,"indexed select width must be1..64");auto direction=vpi_get(vpiIndexedPartSelectType,h);need(direction==vpiPosIndexed||direction==vpiNegIndexed,"unsupported indexed select direction");return "{\"kind\":\"indexedselect\",\"value\":"+expr(actual,depth+1)+",\"base\":"+expr(one(h,vpiBaseExpr),depth+1)+",\"width\":"+std::to_string(n)+",\"descending\":"+(direction==vpiNegIndexed?"true":"false")+"}";}
 if(type(h)==vpiPartSelect){auto actual=one(h,vpiActual);need(actual&&type(actual)!=vpiArrayVar,"part select requires a packed word");auto left=selected_constant(one(h,vpiLeftRange)),right=selected_constant(one(h,vpiRightRange));need(left<=INT32_MAX&&right<=left&&left-right<64,"part select requires nonnegative descending constant bounds of 1..64 bits");return "{\"kind\":\"partselect\",\"value\":"+expr(actual,depth+1)+",\"left\":"+std::to_string(left)+",\"right\":"+std::to_string(right)+"}";}
 if(type(h)==vpiOperation){
  need(!signed_expression(h),"signed operation expressions unsupported");
  auto operation=vpi_get(vpiOpType,h);auto concatOperands=many(h,vpiOperand);
  if(operation==vpiConcatOp||operation==vpiMultiConcatOp){std::vector<std::string> values;if(operation==vpiMultiConcatOp){need(concatOperands.size()==2,"replication requires count and expression");auto count=selected_constant(concatOperands[0]);need(count>=1&&count<=64&&count*uint64_t(width(concatOperands[1]))<=64,"replication must produce 1..64 bits");auto value=expr(concatOperands[1],depth+1);need(value.size()*count+count*32<=4*1024*1024,"replication exceeds normalized output budget");for(uint64_t i=0;i<count;i++)values.push_back(value);}else{need(!concatOperands.empty()&&concatOperands.size()<=64,"concat operand limit exceeded");int total=0;for(auto operand:concatOperands){total+=width(operand);need(total<=64,"concat exceeds64bits");values.push_back(expr(operand,depth+1));}}auto value=concat_tree(values,0,values.size());output_budget(value);return value;}
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
void target_shape(H h,int depth=0){
 need(h&&depth<64,"assignment target depth exceeded");
 if(type(h)==vpiOperation){need(vpi_get(vpiOpType,h)==vpiConcatOp,"only concatenation operations supported in assignment targets");for(auto child:many(h,vpiOperand))target_shape(child,depth+1);}
}
std::string stmt(H h,int depth=0){
 need(h&&depth<64&&++nodes<=20000,"statement missing or resource limit exceeded");
 if(type(h)==vpiBegin){std::string s="{\"kind\":\"block\",\"items\":[";bool first=true;for(auto a:many(h,vpiStmt)){if(!first)s+=",";first=false;s+=stmt(a,depth+1);}return s+"]}";}
 if(type(h)==vpiAssignment){need(vpi_get(vpiBlocking,h)==0,"blocking procedural assignment unsupported");need(!one(h,vpiDelayControl)&&!one(h,vpiEventControl),"assignment timing unsupported");target_shape(one(h,vpiLhs));return "{\"kind\":\"nba\",\"target\":"+expr(one(h,vpiLhs))+",\"value\":"+expr(one(h,vpiRhs))+"}";}
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
  output_budget(out);std::ofstream file(argv[2],std::ios::binary|std::ios::trunc);file<<out;need(bool(file),"write failed");return 0;
 }catch(const std::exception& e){std::cerr<<"RTL lowering rejected: "<<e.what()<<"\n";return 1;}
}
