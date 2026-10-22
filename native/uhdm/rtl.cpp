// Bounded, structural UHDM exporter for shared-clock sequential RTL.
// No source-text pattern matching and no FIFO-specific state transition logic.
#include <uhdm/uhdm.h>

#include <uhdm/vpi_user.h>
#include <uhdm/vpi_uhdm.h>
#include "json_escape.h"
#include <fstream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>
#include <algorithm>
#include <set>
namespace {
using H=vpiHandle;
void need(bool b,const std::string& s){if(!b)throw std::runtime_error(s);}
H one(H h,int r){return h?vpi_handle(r,h):nullptr;}
int type(H h){return h?vpi_get(vpiType,h):-1;}
std::string name(H h){const char* p=h?vpi_get_str(vpiName,h):nullptr;return p?p:"";}
std::vector<H> many(H h,int r){std::vector<H> v;H it=h?vpi_iterate(r,h):nullptr;if(it){for(H x=vpi_scan(it);x;x=vpi_scan(it))v.push_back(x);vpi_release_handle(it);}return v;}
std::string q(const std::string& s){return velosim::json_escape(s);}
H mod; UHDM::Serializer* store; size_t nodes=0;
std::map<const void*,std::string> flat_symbols;
std::map<std::string,std::string> active_symbols,clock_aliases;
const void* identity(H h){return h?reinterpret_cast<const uhdm_handle*>(h)->object:nullptr;}
std::string signal_name(H h){
 H current=h;std::string result;bool had_actual=false;
 for(int depth=0;current&&depth<64;++depth){auto found=flat_symbols.find(identity(current));if(found!=flat_symbols.end()){result=found->second;break;}auto actual=one(current,vpiActual);if(actual)had_actual=true;if(identity(actual)==identity(current))break;current=actual;}
 if(result.empty()){need(!had_actual,"reference actual is outside collected scopes: "+name(h));auto found=active_symbols.find(name(h));need(found!=active_symbols.end(),"unresolved scoped reference: "+name(h));result=found->second;}
 auto alias=clock_aliases.find(result);return alias==clock_aliases.end()?result:alias->second;
}
#include "rtl_original_expr.h"
bool unsized_decimal(H h){if(type(h)!=vpiConstant)return false;const char* raw=vpi_get_str(vpiDecompile,h);std::string text=raw?raw:"";return !text.empty()&&text.find_first_not_of("0123456789_")==std::string::npos;}
#include "rtl_constants.h"
uint64_t integer(H h,int depth=0);
bool signed_expression(H h,int depth);
uint64_t selected_constant(H h){if(auto original=original_expression(h))h=original;need(type(h)==vpiConstant,"select bounds and replication counts require literal constants");return integer(h);}
int width(H h,int depth=0){need(h&&depth<64,"word width depth exceeded");if(auto original=original_expression(h))return width(original,depth+1);if(unsized_decimal(h))return int(bits(h).size());if(type(h)==vpiBitSelect){auto actual=one(h,vpiActual);if(type(actual)!=vpiArrayVar)return 1;auto elements=many(actual,vpiReg);need(elements.size()==1,"one memory element declaration required");return width(elements[0],depth+1);}if(type(h)==vpiIndexedPartSelect){auto n=selected_constant(one(h,vpiWidthExpr));need(n>=1&&n<=128,"indexed select width must be1..128");return int(n);}if(type(h)==vpiPartSelect){auto l=selected_constant(one(h,vpiLeftRange)),r=selected_constant(one(h,vpiRightRange));need(l>=r&&l-r<128,"unsupported constant part-select width");return int(l-r+1);}if(type(h)==vpiOperation){int op=vpi_get(vpiOpType,h);auto args=many(h,vpiOperand);if(op==vpiConcatOp){need(!args.empty()&&args.size()<=128,"concat operand limit exceeded");int n=0;for(auto a:args){n+=width(a,depth+1);need(n<=128,"concat exceeds128bits");}return n;}if(op==vpiMultiConcatOp){need(args.size()==2,"replication requires count and expression");auto count=selected_constant(args[0]);int n=width(args[1],depth+1);need(count>=1&&count<=128&&count*uint64_t(n)<=128,"replication must produce1..128bits");return int(count*n);}if(op==vpiNotOp||op==vpiLogAndOp||op==vpiLogOrOp||(op>=vpiUnaryAndOp&&op<=vpiUnaryXNorOp)||(op>=vpiEqOp&&op<=vpiLeOp))return 1;need(!args.empty(),"expression width requires operands");if(op==vpiLShiftOp||op==vpiRShiftOp||op==vpiArithRShiftOp)return width(args[0],depth+1);size_t start=op==vpiConditionOp?1:0;need(start<args.size(),"conditional width requires branches");int n=0;for(size_t i=start;i<args.size();i++)n=std::max(n,width(args[i],depth+1));return n;}if(type(h)==vpiConstant)return int(bits(h).size());if(type(h)==vpiSysFuncCall){auto args=many(h,vpiArgument);need((name(h)=="$signed"||name(h)=="$unsigned")&&args.size()==1,"unsupported system function width");return width(args[0],depth+1);}auto actual=one(h,vpiActual);if(type(actual)==vpiParameter)return 32;if(actual&&identity(actual)!=identity(h))return width(actual,depth+1);auto spec=one(one(h,vpiTypespec),vpiActual);need((type(spec)==vpiLogicTypespec||type(spec)==vpiBitTypespec),"only logic word widths supported: "+name(h));auto ranges=many(spec,vpiRange);need(ranges.size()<=1,"multidimensional packed words unsupported");if(ranges.empty())return 1;auto l=integer(one(ranges[0],vpiLeftRange)),r=integer(one(ranges[0],vpiRightRange));need(l>=r&&l-r<128,"word width must be1..128");return int(l-r+1);}
bool declared_signed(H h){auto spec=one(one(h,vpiTypespec),vpiActual);return vpi_get(vpiSigned,h)||(spec&&vpi_get(vpiSigned,spec));}
void logic_word(H h){auto spec=one(one(h,vpiTypespec),vpiActual);need((type(spec)==vpiLogicTypespec||type(spec)==vpiBitTypespec),"only packed logic/bit words supported");auto ranges=many(spec,vpiRange);need(ranges.size()<=1,"multidimensional packed words unsupported");if(!ranges.empty())need(integer(one(ranges[0],vpiRightRange))==0&&integer(one(ranges[0],vpiLeftRange))==uint64_t(width(h)-1),"packed words require descending zero-based bounds [width-1:0]");}

uint64_t integer(H h,int depth){need(h&&depth<64,"constant integer depth exceeded");if(type(h)==vpiOperation){auto a=many(h,vpiOperand);need(a.size()==2,"range expression unsupported");auto l=integer(a[0],depth+1),r=integer(a[1],depth+1);int op=vpi_get(vpiOpType,h);need(op==vpiAddOp||op==vpiSubOp,"range operation unsupported");return op==vpiAddOp?l+r:l-r;}need(type(h)==vpiConstant,"constant range required");auto s=bits(h);need(s.find_first_not_of("01")==std::string::npos,"unknown array bound");need(!(signed_expression(h,0)&&s.front()=='1'),"negative signed bounds and replication counts unsupported");return std::stoull(s,nullptr,2);}
std::string concat_tree(const std::vector<std::string>& values,size_t begin,size_t end){if(end-begin==1)return values[begin];auto mid=begin+(end-begin)/2;return "{\"kind\":\"concat\",\"args\":["+concat_tree(values,begin,mid)+","+concat_tree(values,mid,end)+"]}";}
void output_budget(const std::string& value){need(value.size()<=4*1024*1024,"normalized RTL exceeds4MiB");int nesting=0;size_t objects=0;bool quoted=false,escaped=false;for(char c:value){if(quoted){if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c=='\"')quoted=false;continue;}if(c=='\"')quoted=true;else if(c=='{'||c=='['){need(++nesting<=64,"normalized RTL JSON depth exceeds64");if(c=='{')need(++objects<=20000,"normalized RTL node budget exceeded");}else if(c=='}'||c==']')--nesting;}}
bool signed_expression(H h,int depth=0){
 need(h&&depth<64,"signedness depth exceeded");
 if(type(h)==vpiConstant){auto spec=one(one(h,vpiTypespec),vpiActual);const char* p=vpi_get_str(vpiDecompile,h);std::string literal=p?p:"";return (!literal.empty()&&literal.find_first_not_of("0123456789_")==std::string::npos)||vpi_get(vpiSigned,h)||(spec&&vpi_get(vpiSigned,spec))||vpi_get(vpiConstType,h)==vpiIntConst||literal.find("'s")!=std::string::npos||literal.find("'S")!=std::string::npos;}
 if(type(one(h,vpiActual))==vpiParameter)return declared_signed(one(h,vpiActual));
 if(type(h)!=vpiOperation)return false; // Structural expression signedness is inferred from normalized declarations.
 int op=vpi_get(vpiOpType,h);auto a=many(h,vpiOperand);
 if(op==vpiConcatOp||op==vpiNotOp||op==vpiLogAndOp||op==vpiLogOrOp||(op>=vpiUnaryAndOp&&op<=vpiUnaryXNorOp)||(op>=vpiEqOp&&op<=vpiLeOp))return false;
 if(op==vpiConditionOp)return a.size()==3&&signed_expression(a[1],depth+1)&&signed_expression(a[2],depth+1);
 if(op==vpiLShiftOp||op==vpiRShiftOp||op==vpiArithLShiftOp||op==vpiArithRShiftOp)return !a.empty()&&signed_expression(a[0],depth+1);
 for(auto operand:a)if(!signed_expression(operand,depth+1))return false;
 return !a.empty();
}
std::string expr(H h,int depth=0){
 need(h&&depth<64&&++nodes<=20000,"expression missing or resource limit exceeded");if(auto original=original_expression(h))return expr(original,depth+1);
 if(type(one(h,vpiActual))==vpiParameter){
  auto parameter=one(h,vpiActual);need(type(one(one(parameter,vpiTypespec),vpiActual))==vpiIntTypespec,"only int parameters supported in expressions");std::string b;
  for(auto assignment:many(mod,vpiParamAssign))if(name(one(assignment,vpiLhs))==name(parameter)){auto rhs=one(assignment,vpiRhs);need(type(rhs)==vpiConstant,"integer parameter must be materialized by elaboration");b=constant_bits(static_cast<const UHDM::constant*>(identity(rhs)));break;}
  if(b.empty()){auto p=static_cast<const UHDM::parameter*>(identity(parameter));need(!p->VpiValue().empty(),"unmaterialized integer parameter value unsupported");auto c=store->MakeConstant();c->VpiValue(p->VpiValue());c->VpiSize(32);c->VpiConstType(p->VpiConstType());b=constant_bits(c);}
  need(!b.empty()&&b.find_first_not_of("01")==std::string::npos,"only known integer parameters supported");if(b.size()>32)b=b.substr(b.size()-32);else if(b.size()<32)b=std::string(32-b.size(),'0')+b;
  return "{\"kind\":\"const\",\"bits\":"+q(b)+(declared_signed(parameter)?",\"signed\":true":"")+"}";
 }
 if(type(h)==vpiConstant){auto b=bits(h);const char* d=vpi_get_str(vpiDecompile,h);std::string literal=d?d:"";need(literal!="'1"&&literal!="'x"&&literal!="'z"&&literal!="'X"&&literal!="'Z","unbased fill literal unsupported except zero");return "{\"kind\":\"const\",\"bits\":"+q(b)+(signed_expression(h)?",\"signed\":true":"")+"}";}
 if(type(h)==vpiBitSelect){auto index=one(h,vpiIndex);if(type(index)==vpiConstant)need(!(signed_expression(index)&&bits(index).front()=='1'),"negative constant packed indices unsupported");auto actual=one(h,vpiActual);if(type(actual)==vpiArrayVar)return "{\"kind\":\"index\",\"name\":"+q(signal_name(h))+",\"index\":"+expr(one(h,vpiIndex),depth+1)+"}";need(actual&&type(actual)!=vpiBitSelect&&type(actual)!=vpiPartSelect,"packed select requires a declared word");return "{\"kind\":\"bitselect\",\"value\":"+expr(actual,depth+1)+",\"index\":"+expr(one(h,vpiIndex),depth+1)+"}";}
 if(type(h)==vpiIndexedPartSelect){auto index=one(h,vpiBaseExpr);if(type(index)==vpiConstant)need(!(signed_expression(index)&&bits(index).front()=='1'),"negative constant indexed bases unsupported");auto actual=one(h,vpiActual);need(actual&&type(actual)!=vpiArrayVar&&type(actual)!=vpiBitSelect&&type(actual)!=vpiPartSelect&&type(actual)!=vpiIndexedPartSelect,"indexed select requires a declared packed word");auto n=selected_constant(one(h,vpiWidthExpr));need(n>=1&&n<=128,"indexed select width must be1..128");auto direction=vpi_get(vpiIndexedPartSelectType,h);need(direction==vpiPosIndexed||direction==vpiNegIndexed,"unsupported indexed select direction");return "{\"kind\":\"indexedselect\",\"value\":"+expr(actual,depth+1)+",\"base\":"+expr(one(h,vpiBaseExpr),depth+1)+",\"width\":"+std::to_string(n)+",\"descending\":"+(direction==vpiNegIndexed?"true":"false")+"}";}
 if(type(h)==vpiPartSelect){auto actual=one(h,vpiActual);need(actual&&type(actual)!=vpiArrayVar,"part select requires a packed word");auto left=selected_constant(one(h,vpiLeftRange)),right=selected_constant(one(h,vpiRightRange));need(left<=INT32_MAX&&right<=left&&left-right<128,"part select requires nonnegative descending constant bounds of 1..128 bits");return "{\"kind\":\"partselect\",\"value\":"+expr(actual,depth+1)+",\"left\":"+std::to_string(left)+",\"right\":"+std::to_string(right)+"}";}
 if(type(h)==vpiSysFuncCall){auto args=many(h,vpiArgument);need((name(h)=="$signed"||name(h)=="$unsigned")&&args.size()==1,"unsupported system function");return "{\"kind\":"+q(name(h)=="$signed"?"castsigned":"castunsigned")+",\"args\":["+expr(args[0],depth+1)+"]}";}
 if(type(h)==vpiOperation){
  auto operation=vpi_get(vpiOpType,h);auto concatOperands=many(h,vpiOperand);
  if(operation==vpiConcatOp||operation==vpiMultiConcatOp){std::vector<std::string> values;if(operation==vpiMultiConcatOp){need(concatOperands.size()==2,"replication requires count and expression");auto count=selected_constant(concatOperands[0]);need(count>=1&&count<=128&&count*uint64_t(width(concatOperands[1]))<=128,"replication must produce 1..128 bits");auto value=expr(concatOperands[1],depth+1);need(value.size()*count+count*32<=4*1024*1024,"replication exceeds normalized output budget");for(uint64_t i=0;i<count;i++)values.push_back(value);}else{need(!concatOperands.empty()&&concatOperands.size()<=128,"concat operand limit exceeded");int total=0;for(auto operand:concatOperands){total+=width(operand);need(total<=128,"concat exceeds128bits");values.push_back(expr(operand,depth+1));}}auto value=concat_tree(values,0,values.size());output_budget(value);return value;}
  static const std::map<int,std::string> ops={{vpiAddOp,"add"},{vpiSubOp,"sub"},{vpiLogAndOp,"and"},{vpiNotOp,"not"},{vpiEqOp,"eq"},{vpiConcatOp,"concat"},{vpiBitAndOp,"bitand"},{vpiBitOrOp,"bitor"},{vpiBitXorOp,"bitxor"},{vpiBitNegOp,"bitnot"},
   {vpiLogOrOp,"or"},{vpiNeqOp,"neq"},{vpiCaseEqOp,"caseeq"},{vpiCaseNeqOp,"caseneq"},{vpiLtOp,"lt"},{vpiLeOp,"le"},{vpiGtOp,"gt"},{vpiGeOp,"ge"},{vpiBitXNorOp,"bitxnor"},{vpiMinusOp,"neg"},
   {vpiLShiftOp,"shl"},{vpiRShiftOp,"shr"},{vpiArithRShiftOp,"ashr"},{vpiUnaryAndOp,"redand"},{vpiUnaryNandOp,"rednand"},{vpiUnaryOrOp,"redor"},{vpiUnaryNorOp,"rednor"},{vpiUnaryXorOp,"redxor"},{vpiUnaryXNorOp,"redxnor"},{vpiPlusOp,"pos"},
   {vpiMultOp,"mul"},{vpiDivOp,"div"},{vpiModOp,"mod"},{vpiConditionOp,"cond"}};
  auto it=ops.find(vpi_get(vpiOpType,h));need(it!=ops.end(),"unsupported expression operation "+std::to_string(vpi_get(vpiOpType,h)));auto operands=many(h,vpiOperand);
  bool unary=it->second=="not"||it->second=="bitnot"||it->second=="neg"||it->second=="pos"||it->second.rfind("red",0)==0;
  need(operands.size()==(unary?1:it->second=="cond"?3:2),"unsupported expression arity");
  std::string s="{\"kind\":"+q(it->second)+",\"args\":[";bool first=true;for(auto a:operands){if(!first)s+=",";first=false;s+=expr(a,depth+1);}return s+"]}";
 }
 need(!name(h).empty()&&(type(h)==vpiReg||type(h)==vpiNet||type(h)==vpiBitVar||type(h)==vpiRefObj||type(h)==vpiVariables),"unsupported reference type "+std::to_string(type(h)));
 return "{\"kind\":\"ref\",\"name\":"+q(signal_name(h))+"}";
}
void target_shape(H h,int depth=0){
 need(h&&depth<64,"assignment target depth exceeded");
 if(type(h)==vpiOperation){need(vpi_get(vpiOpType,h)==vpiConcatOp,"only concatenation operations supported in assignment targets");for(auto child:many(h,vpiOperand))target_shape(child,depth+1);}
}
std::string stmt(H h,int depth=0){
 need(h&&depth<64&&++nodes<=20000,"statement missing or resource limit exceeded");
 if(type(h)==vpiRepeat){auto count=selected_constant(one(h,vpiCondition));need(count<=256,"repeat count exceeds256");return "{\"kind\":\"repeat\",\"count\":"+std::to_string(count)+",\"body\":"+stmt(one(h,vpiStmt),depth+1)+"}";}
 if(type(h)==vpiBegin){std::string s="{\"kind\":\"block\",\"items\":[";bool first=true;for(auto a:many(h,vpiStmt)){if(!first)s+=",";first=false;s+=stmt(a,depth+1);}return s+"]}";}
 if(type(h)==vpiAssignment){need(!one(h,vpiDelayControl)&&!one(h,vpiEventControl),"assignment timing unsupported");target_shape(one(h,vpiLhs));return "{\"kind\":"+q(vpi_get(vpiBlocking,h)?"blocking":"nba")+",\"target\":"+expr(one(h,vpiLhs))+",\"value\":"+expr(one(h,vpiRhs))+"}";}
 if(type(h)==vpiIf||type(h)==vpiIfElse){auto e=one(h,vpiElseStmt);return "{\"kind\":\"if\",\"condition\":"+expr(one(h,vpiCondition))+",\"then\":"+stmt(one(h,vpiStmt),depth+1)+",\"else\":"+(e?stmt(e,depth+1):"{\"kind\":\"block\",\"items\":[]}")+"}";}
 if(type(h)==vpiCase){need(vpi_get(vpiCaseType,h)==vpiCaseExact,"only exact case supported");std::string s="{\"kind\":\"case\",\"value\":"+expr(one(h,vpiCondition))+",\"items\":[";bool first=true;for(auto item:many(h,vpiCaseItem)){if(!first)s+=",";first=false;auto conditions=many(item,vpiExpr);need(conditions.size()<=1,"multiple case labels unsupported");s+="{\"match\":"+(conditions.empty()?"null":expr(conditions[0]))+",\"body\":"+stmt(one(item,vpiStmt),depth+1)+"}";}return s+"]}";}
 throw std::runtime_error("unsupported statement type "+std::to_string(type(h)));
}
#include "rtl_flatten.h"
}
int main(int argc,char** argv){
 if(argc!=4){std::cerr<<"usage: velosim-uhdm-rtl <design.uhdm> <rtl.json> <top>\n";return 2;}
 try{
  UHDM::Serializer serializer;store=&serializer;auto designs=serializer.Restore(std::string(argv[1]));need(designs.size()==1,"one design required");auto modules=many(designs[0],UHDM::uhdmtopModules);need(modules.size()==1,"one top required");mod=modules[0];need(name(mod)==argv[3]||name(mod)==std::string("work@")+argv[3],"top mismatch");
  for(auto original:many(designs[0],UHDM::uhdmallModules))collect_original_expressions(original);
  auto out=flatten_design(mod);
  output_budget(out);std::ofstream file(argv[2],std::ios::binary|std::ios::trunc);file<<out;need(bool(file),"write failed");return 0;
 }catch(const std::exception& e){std::cerr<<"RTL lowering rejected: "<<e.what()<<"\n";return 1;}
}
