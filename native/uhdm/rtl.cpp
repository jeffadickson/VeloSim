// Bounded structural UHDM lowering for edge-controlled RTL, combinational blocks and pure calls.
// No source-text pattern matching and no FIFO-specific state transition logic.
#include <uhdm/uhdm.h>

#include <uhdm/vpi_user.h>
#include <uhdm/vpi_uhdm.h>
#include "json_escape.h"
#include "../rtl/runtime.h"
#include <fstream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>
#include <algorithm>
#include <set>
#include <cstdlib>
#include <optional>
#include <functional>
#ifdef VELOSIM_EXPORTER_COVERAGE
extern "C" void __llvm_profile_set_filename(const char*);
#endif
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
// Integral aliases and enums retain their base width/state; no arithmetic is
// inferred from a variable's spelling or the host C++ integer width.
H base_spec(H spec,int depth=0){need(depth<16,"integral base type depth exceeded");if(type(spec)==vpiRefTypespec)return base_spec(one(spec,vpiActual),depth+1);if(type(spec)==vpiEnumTypespec)return base_spec(one(spec,vpiBaseTypespec),depth+1);return spec;}
H word_spec(H h){auto reference=one(h,type(h)==vpiIODecl?vpiTypedef:vpiTypespec);return base_spec(one(reference,vpiActual));}
int atom_width(H spec){switch(type(spec)){case vpiByteTypespec:return 8;case vpiShortIntTypespec:return 16;case vpiIntTypespec:case vpiIntegerTypespec:return 32;case vpiLongIntTypespec:case vpiTimeTypespec:return 64;default:return 0;}}
bool aggregate_spec(H spec){return type(spec)==vpiStructTypespec||type(spec)==vpiUnionTypespec;}
int aggregate_width(H,int);bool aggregate_state(H,int);
struct PackedField{std::string root;int offset,width;bool sign,two_state;};
PackedField packed_field(H);std::string packed_field_expression(H,bool);
// Recognize a method only through its typed receiver, never its dotted spelling.
bool dynamic_array(H h){return type(h)==vpiArrayVar&&vpi_get(vpiArrayType,h)==vpiDynamicArray;}
bool dynamic_size(H h);
std::string dynamic_size_expression(H h);
bool two_state_word(H declaration){auto spec=word_spec(declaration);int t=type(spec);if(aggregate_spec(spec))return aggregate_state(spec,0);return type(declaration)==vpiBitVar||t==vpiBitTypespec||t==vpiByteTypespec||t==vpiShortIntTypespec||t==vpiIntTypespec||t==vpiLongIntTypespec;}
uint64_t integer(H h,int depth=0);
int parameter_reference_width(H);
bool parameter_reference_signed(H);
bool is_parameter_reference(H);
bool signed_expression(H h,int depth);
uint64_t selected_constant(H h){if(auto original=original_expression(h))h=original;need(type(h)==vpiConstant,"select bounds and replication counts require literal constants");return integer(h);}
int width(H h,int depth=0){need(h&&depth<64,"word width depth exceeded");if(auto original=original_expression(h))return width(original,depth+1);if(unsized_decimal(h))return int(bits(h).size());if(type(h)==vpiHierPath)return dynamic_size(h)?32:packed_field(h).width;if(type(h)==vpiVarSelect){auto actual=one(h,vpiActual);need(type(actual)==vpiArrayVar,"variable selection requires a fixed array");auto elements=many(actual,vpiReg);if(elements.empty())elements=many(actual,vpiVariables);need(elements.size()==1,"one array element declaration required");return width(elements[0],depth+1);}if(type(h)==vpiBitSelect){auto actual=one(h,vpiActual);if(type(actual)!=vpiArrayVar)return 1;auto elements=many(actual,vpiReg);need(elements.size()==1,"one memory element declaration required");return width(elements[0],depth+1);}if(type(h)==vpiIndexedPartSelect){auto n=selected_constant(one(h,vpiWidthExpr));need(n>=1&&n<=256,"indexed select width must be1..256");return int(n);}if(type(h)==vpiPartSelect){auto l=selected_constant(one(h,vpiLeftRange)),r=selected_constant(one(h,vpiRightRange));need(l>=r&&l-r<256,"unsupported constant part-select width");return int(l-r+1);}if(type(h)==vpiOperation){int op=vpi_get(vpiOpType,h);auto args=many(h,vpiOperand);if(op==vpiConcatOp){need(!args.empty()&&args.size()<=256,"concat operand limit exceeded");int n=0;for(auto a:args){n+=width(a,depth+1);need(n<=256,"concat exceeds256bits");}return n;}if(op==vpiMultiConcatOp){need(args.size()==2,"replication requires count and expression");auto count=selected_constant(args[0]);int n=width(args[1],depth+1);need(count>=1&&count<=256&&count*uint64_t(n)<=256,"replication must produce1..256bits");return int(count*n);}if(op==vpiNotOp||op==vpiLogAndOp||op==vpiLogOrOp||(op>=vpiUnaryAndOp&&op<=vpiUnaryXNorOp)||(op>=vpiEqOp&&op<=vpiLeOp))return 1;need(!args.empty(),"expression width requires operands");if(op==vpiLShiftOp||op==vpiRShiftOp||op==vpiArithRShiftOp)return width(args[0],depth+1);size_t start=op==vpiConditionOp?1:0;need(start<args.size(),"conditional width requires branches");int n=0;for(size_t i=start;i<args.size();i++)n=std::max(n,width(args[i],depth+1));return n;}if(type(h)==vpiFuncCall)return width(one(one(h,vpiFunction),vpiReturn),depth+1);if(type(h)==vpiConstant)return int(bits(h).size());if(type(h)==vpiSysFuncCall){auto args=many(h,vpiArgument);need((name(h)=="$signed"||name(h)=="$unsigned")&&args.size()==1,"unsupported system function width");return width(args[0],depth+1);}auto actual=one(h,vpiActual);if(is_parameter_reference(h))return parameter_reference_width(h);if(actual&&identity(actual)!=identity(h))return width(actual,depth+1);auto spec=word_spec(h);if(aggregate_spec(spec))return aggregate_width(spec,depth+1);if(auto w=atom_width(spec))return w;need((type(spec)==vpiLogicTypespec||type(spec)==vpiBitTypespec),"only logic word widths supported: "+name(h));auto ranges=many(spec,vpiRange);need(ranges.size()<=1,"multidimensional packed words unsupported");if(ranges.empty())return 1;auto l=integer(one(ranges[0],vpiLeftRange)),r=integer(one(ranges[0],vpiRightRange));need(l>=r&&l-r<256,"word width must be1..256");return int(l-r+1);}
bool declared_signed(H h){auto spec=word_spec(h);return vpi_get(vpiSigned,h)||(spec&&vpi_get(vpiSigned,spec));}
void logic_word(H h){auto spec=word_spec(h);if(atom_width(spec))return;if(aggregate_spec(spec)){aggregate_width(spec,0);aggregate_state(spec,0);return;}need((type(spec)==vpiLogicTypespec||type(spec)==vpiBitTypespec),"only packed logic/bit words supported");auto ranges=many(spec,vpiRange);need(ranges.size()<=1,"multidimensional packed words unsupported");if(!ranges.empty())need(integer(one(ranges[0],vpiRightRange))==0&&integer(one(ranges[0],vpiLeftRange))==uint64_t(width(h)-1),"packed words require descending zero-based bounds [width-1:0]");}

uint64_t integer(H h,int depth){need(h&&depth<64,"constant integer depth exceeded");if(type(h)==vpiOperation){auto a=many(h,vpiOperand);need(a.size()==2,"range expression unsupported");auto l=integer(a[0],depth+1),r=integer(a[1],depth+1);int op=vpi_get(vpiOpType,h);need(op==vpiAddOp||op==vpiSubOp,"range operation unsupported");return op==vpiAddOp?l+r:l-r;}need(type(h)==vpiConstant,"constant range required");auto s=bits(h);need(s.find_first_not_of("01")==std::string::npos,"unknown array bound");need(!(signed_expression(h,0)&&s.front()=='1'),"negative signed bounds and replication counts unsupported");return std::stoull(s,nullptr,2);}
std::string concat_tree(const std::vector<std::string>& values,size_t begin,size_t end){if(end-begin==1)return values[begin];auto mid=begin+(end-begin)/2;return "{\"kind\":\"concat\",\"args\":["+concat_tree(values,begin,mid)+","+concat_tree(values,mid,end)+"]}";}
void output_budget(const std::string& value){need(value.size()<=4*1024*1024,"normalized RTL exceeds4MiB");int nesting=0;size_t objects=0;bool quoted=false,escaped=false;for(char c:value){if(quoted){if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c=='\"')quoted=false;continue;}if(c=='\"')quoted=true;else if(c=='{'||c=='['){need(++nesting<=64,"normalized RTL JSON depth exceeds64");if(c=='{')need(++objects<=20000,"normalized RTL node budget exceeded");}else if(c=='}'||c==']')--nesting;}}
bool signed_expression(H h,int depth=0){
 need(h&&depth<64,"signedness depth exceeded");
 if(type(h)==vpiConstant){auto spec=word_spec(h);const char* p=vpi_get_str(vpiDecompile,h);std::string literal=p?p:"";return (!literal.empty()&&literal.find_first_not_of("0123456789_")==std::string::npos)||vpi_get(vpiSigned,h)||(spec&&vpi_get(vpiSigned,spec))||vpi_get(vpiConstType,h)==vpiIntConst||literal.find("'s")!=std::string::npos||literal.find("'S")!=std::string::npos;}
 if(is_parameter_reference(h))return parameter_reference_signed(h);
 if(type(h)!=vpiOperation)return false; // Structural expression signedness is inferred from normalized declarations.
 int op=vpi_get(vpiOpType,h);auto a=many(h,vpiOperand);
 if(op==vpiConcatOp||op==vpiNotOp||op==vpiLogAndOp||op==vpiLogOrOp||(op>=vpiUnaryAndOp&&op<=vpiUnaryXNorOp)||(op>=vpiEqOp&&op<=vpiLeOp))return false;
 if(op==vpiConditionOp)return a.size()==3&&signed_expression(a[1],depth+1)&&signed_expression(a[2],depth+1);
 if(op==vpiLShiftOp||op==vpiRShiftOp||op==vpiArithLShiftOp||op==vpiArithRShiftOp)return !a.empty()&&signed_expression(a[0],depth+1);
 for(auto operand:a)if(!signed_expression(operand,depth+1))return false;
 return !a.empty();
}
std::string expr(H h,int depth=0);
#include "rtl_subroutines.h"
#include "rtl_types.h"
bool dynamic_size(H h){
 if(type(h)!=vpiHierPath)return false;
 auto path=static_cast<const UHDM::hier_path*>(identity(h));auto elements=path->Path_elems();
 if(!elements||elements->empty())return false;
 LoopHandles owned;auto receiver=owned.add(elements->front());if(!dynamic_array(one(receiver,vpiActual)))return false;
 need(!inside_call&&elements->size()==2,"dynamic array methods require a whole module-scope receiver");
 auto method=owned.add(elements->back());need(type(method)==vpiMethodFuncCall&&name(method)=="size"&&many(method,vpiArgument).empty(),"only zero-argument dynamic size supported in expressions");
 need(flat_symbols.count(identity(one(receiver,vpiActual))),"dynamic method receiver must bind a collected declaration");return true;
}
std::string dynamic_size_expression(H h){LoopHandles owned;auto path=static_cast<const UHDM::hier_path*>(identity(h));return "{\"kind\":\"size\",\"name\":"+q(signal_name(owned.add(path->Path_elems()->front())))+"}";}
std::string expr_core(H h,int depth){
 need(h&&depth<64&&++nodes<=20000,"expression missing or resource limit exceeded");
 // Selection indices belong to the source declaration range. Whole-parameter
 // captures are supported, but selections must not silently become zero-based.
 need(!syntax_parameter_selections.count(lexical_key(h)),"parameter selections unsupported; pass the whole parameter");if(auto original=original_expression(h))return expr(original,depth+1);
 // Enumerators are immutable values of their declared base type, not storage.
 auto enumerator=type(h)==vpiEnumConst?h:one(h,vpiActual);
 if(type(enumerator)==vpiEnumConst){auto object=static_cast<const UHDM::enum_const*>(identity(enumerator));need(object->VpiSize()>=1&&object->VpiSize()<=256,"enumerator width unsupported");auto constant=store->MakeConstant();constant->VpiSize(object->VpiSize());constant->VpiValue(object->VpiValue());auto spec=base_spec(one(enumerator,vpiParent));return "{\"kind\":\"const\",\"bits\":"+q(constant_bits(constant))+(vpi_get(vpiSigned,spec)?",\"signed\":true":"")+"}";}
 if(is_parameter_reference(h))return parameter_expr(h);
 if(type(h)==vpiConstant){auto b=bits(h);const char* d=vpi_get_str(vpiDecompile,h);std::string literal=d?d:"";need(literal!="'1"&&literal!="'x"&&literal!="'z"&&literal!="'X"&&literal!="'Z","unbased fill literal unsupported except zero");return "{\"kind\":\"const\",\"bits\":"+q(b)+(signed_expression(h)?",\"signed\":true":"")+"}";}
 if(type(h)==vpiVarSelect){need(!inside_call,"captured global memory reads unsupported");auto actual=one(h,vpiActual);need(type(actual)==vpiArrayVar,"variable selection requires a fixed array");auto indices=many(h,vpiIndex),ranges=many(actual,vpiRange);need(indices.size()==ranges.size()&&indices.size()>=2&&indices.size()<=4,"array index rank mismatch");std::string value="{\"kind\":\"index\",\"name\":"+q(signal_name(h))+",\"indices\":[";bool first=true;for(auto index:indices){if(!first)value+=",";first=false;value+=expr(index,depth+1);}return value+"]}";}
 if(type(h)==vpiBitSelect){auto index=one(h,vpiIndex);if(type(index)==vpiConstant)need(!(signed_expression(index)&&bits(index).front()=='1'),"negative constant packed indices unsupported");auto actual=one(h,vpiActual);need(type(actual)!=vpiParameter&&!is_parameter_reference(actual),"parameter selections unsupported; pass the whole parameter");if(type(actual)==vpiArrayVar){need(!inside_call,"captured global memory reads unsupported");return "{\"kind\":\"index\",\"name\":"+q(signal_name(h))+",\"index\":"+expr(one(h,vpiIndex),depth+1)+"}";}need(actual&&type(actual)!=vpiBitSelect&&type(actual)!=vpiPartSelect,"packed select requires a declared word");return "{\"kind\":\"bitselect\",\"value\":"+expr(actual,depth+1)+",\"index\":"+expr(one(h,vpiIndex),depth+1)+"}";}
 if(type(h)==vpiIndexedPartSelect){auto index=one(h,vpiBaseExpr);if(type(index)==vpiConstant)need(!(signed_expression(index)&&bits(index).front()=='1'),"negative constant indexed bases unsupported");auto actual=one(h,vpiActual);need(type(actual)!=vpiParameter&&!is_parameter_reference(actual),"parameter selections unsupported; pass the whole parameter");need(actual&&type(actual)!=vpiArrayVar&&type(actual)!=vpiBitSelect&&type(actual)!=vpiPartSelect&&type(actual)!=vpiIndexedPartSelect,"indexed select requires a declared packed word");auto n=selected_constant(one(h,vpiWidthExpr));need(n>=1&&n<=256,"indexed select width must be1..256");auto direction=vpi_get(vpiIndexedPartSelectType,h);need(direction==vpiPosIndexed||direction==vpiNegIndexed,"unsupported indexed select direction");return "{\"kind\":\"indexedselect\",\"value\":"+expr(actual,depth+1)+",\"base\":"+expr(one(h,vpiBaseExpr),depth+1)+",\"width\":"+std::to_string(n)+",\"descending\":"+(direction==vpiNegIndexed?"true":"false")+"}";}
 if(type(h)==vpiPartSelect){auto actual=one(h,vpiActual);need(type(actual)!=vpiParameter&&!is_parameter_reference(actual),"parameter selections unsupported; pass the whole parameter");need(actual&&type(actual)!=vpiArrayVar,"part select requires a packed word");auto left=selected_constant(one(h,vpiLeftRange)),right=selected_constant(one(h,vpiRightRange));need(left<=INT32_MAX&&right<=left&&left-right<256,"part select requires nonnegative descending constant bounds of 1..256 bits");return "{\"kind\":\"partselect\",\"value\":"+expr(actual,depth+1)+",\"left\":"+std::to_string(left)+",\"right\":"+std::to_string(right)+"}";}
 if(type(h)==vpiHierPath)return dynamic_size(h)?dynamic_size_expression(h):packed_field_expression(h,false);
 if(type(h)==vpiFuncCall)return lower_function(h,depth+1);
 if(type(h)==vpiSysFuncCall){auto args=many(h,vpiArgument);need((name(h)=="$signed"||name(h)=="$unsigned")&&args.size()==1,"unsupported system function");return "{\"kind\":"+q(name(h)=="$signed"?"castsigned":"castunsigned")+",\"args\":["+expr(args[0],depth+1)+"]}";}
 if(type(h)==vpiOperation){
  auto operation=vpi_get(vpiOpType,h);auto concatOperands=many(h,vpiOperand);
  if(operation==vpiConcatOp||operation==vpiMultiConcatOp){std::vector<std::string> values;if(operation==vpiMultiConcatOp){need(concatOperands.size()==2,"replication requires count and expression");auto count=selected_constant(concatOperands[0]);need(count>=1&&count<=256&&count*uint64_t(width(concatOperands[1]))<=256,"replication must produce 1..256 bits");auto value=expr(concatOperands[1],depth+1);need(value.size()*count+count*32<=4*1024*1024,"replication exceeds normalized output budget");for(uint64_t i=0;i<count;i++)values.push_back(value);}else{need(!concatOperands.empty()&&concatOperands.size()<=256,"concat operand limit exceeded");int total=0;size_t expanded=0;for(auto operand:concatOperands){total+=width(operand);need(total<=256,"concat exceeds256bits");auto value=expr(operand,depth+1);expanded+=value.size();need(expanded<=4*1024*1024,"concat expanded byte budget exceeded");values.push_back(std::move(value));}}auto value=concat_tree(values,0,values.size());output_budget(value);return value;}
  static const std::map<int,std::string> ops={{vpiAddOp,"add"},{vpiSubOp,"sub"},{vpiLogAndOp,"and"},{vpiNotOp,"not"},{vpiEqOp,"eq"},{vpiConcatOp,"concat"},{vpiBitAndOp,"bitand"},{vpiBitOrOp,"bitor"},{vpiBitXorOp,"bitxor"},{vpiBitNegOp,"bitnot"},
   {vpiLogOrOp,"or"},{vpiNeqOp,"neq"},{vpiCaseEqOp,"caseeq"},{vpiCaseNeqOp,"caseneq"},{vpiLtOp,"lt"},{vpiLeOp,"le"},{vpiGtOp,"gt"},{vpiGeOp,"ge"},{vpiBitXNorOp,"bitxnor"},{vpiMinusOp,"neg"},
   {vpiLShiftOp,"shl"},{vpiRShiftOp,"shr"},{vpiArithRShiftOp,"ashr"},{vpiUnaryAndOp,"redand"},{vpiUnaryNandOp,"rednand"},{vpiUnaryOrOp,"redor"},{vpiUnaryNorOp,"rednor"},{vpiUnaryXorOp,"redxor"},{vpiUnaryXNorOp,"redxnor"},{vpiPlusOp,"pos"},
   {vpiMultOp,"mul"},{vpiDivOp,"div"},{vpiModOp,"mod"},{vpiConditionOp,"cond"}};
  auto it=ops.find(vpi_get(vpiOpType,h));need(it!=ops.end(),"unsupported expression operation "+std::to_string(vpi_get(vpiOpType,h)));auto operands=many(h,vpiOperand);
  bool unary=it->second=="not"||it->second=="bitnot"||it->second=="neg"||it->second=="pos"||it->second.rfind("red",0)==0;
  need(operands.size()==(unary?1:it->second=="cond"?3:2),"unsupported expression arity");
  std::string s="{\"kind\":"+q(it->second)+",\"args\":[";bool first=true;for(auto a:operands){if(!first)s+=",";first=false;s+=expr(a,depth+1);}return s+"]}";
 }
 // Packed selections may point directly to a collected UHDM logic_var declaration.
 need(!name(h).empty()&&(type(h)==vpiReg||type(h)==vpiNet||type(h)==vpiBitVar||type(h)==vpiLogicVar||type(h)==vpiRefObj||type(h)==vpiVariables||type(h)==vpiIntVar||type(h)==vpiByteVar||type(h)==vpiShortIntVar||type(h)==vpiLongIntVar||type(h)==vpiIntegerVar||type(h)==vpiTimeVar||type(h)==vpiEnumVar||type(h)==vpiRefVar||(inside_call&&type(h)==vpiIODecl)||(!inside_call&&flat_symbols.count(identity(h)))),"unsupported reference type "+std::to_string(type(h)));
 if(inside_call){auto key=local_key(h);need(!key.empty(),"captured global subroutine reads unsupported");auto& local=call_locals.at(key);need(local.assigned,"subroutine local read before assignment");return local.value;}
 return "{\"kind\":\"ref\",\"name\":"+q(signal_name(h))+"}";
}
// Check every normalized subtree before a parent can duplicate its text.
// This also bounds expansion when many references reuse a symbolic local value.
std::string expr(H h,int depth){auto value=expr_core(h,depth);output_budget(value);return value;}
void target_shape(H h,int depth=0){
 need(h&&depth<64,"assignment target depth exceeded");
 if(type(h)==vpiOperation){need(vpi_get(vpiOpType,h)==vpiConcatOp,"only concatenation operations supported in assignment targets");for(auto child:many(h,vpiOperand))target_shape(child,depth+1);}
}
std::string stmt(H h,int depth=0,bool control=false){
 need(h&&depth<64&&++nodes<=20000,"statement missing or resource limit exceeded");
 if(type(h)==vpiMethodFuncCall||type(h)==vpiMethodTaskCall){auto receiver=one(h,vpiPrefix),actual=one(receiver,vpiActual);need(!inside_call&&dynamic_array(actual)&&flat_symbols.count(identity(actual))&&name(h)=="delete"&&many(h,vpiArgument).empty(),"only zero-argument dynamic delete supported as a method statement");return "{\"kind\":\"delete\",\"name\":"+q(signal_name(receiver))+"}";}
 if(type(h)==vpiTaskCall)return lower_task(h,depth+1);
 if(type(h)==vpiRepeat){auto count=one(h,vpiCondition);if(type(count)==vpiConstant){auto n=selected_constant(count);need(n<=256,"repeat count exceeds256");return "{\"kind\":\"repeat\",\"count\":"+std::to_string(n)+",\"body\":"+stmt(one(h,vpiStmt),depth+1)+"}";}return "{\"kind\":\"runtime_repeat\",\"count\":"+expr(count)+",\"body\":"+stmt(one(h,vpiStmt),depth+1)+"}";}
 if(type(h)==vpiWhile||type(h)==vpiDoWhile)return "{\"kind\":"+q(type(h)==vpiWhile?"while":"do")+",\"condition\":"+expr(one(h,vpiCondition))+",\"body\":"+stmt(one(h,vpiStmt),depth+1)+"}";
 if(type(h)==vpiFor){
  need(many(h,vpiVariables).empty(),"runtime for declarations require module-scope counters");
  auto object=static_cast<const UHDM::for_stmt*>(identity(h));LoopHandles owned;std::vector<H> init,inc;
  auto append=[&](std::vector<H>& list,const UHDM::BaseClass* value){if(value&&std::none_of(list.begin(),list.end(),[&](H old){return identity(old)==value;}))list.push_back(owned.add(value));};
  if(auto list=object->VpiForInitStmts())for(auto item:*list)append(init,item);append(init,object->VpiForInitStmt());if(auto list=object->VpiForIncStmts())for(auto item:*list)append(inc,item);append(inc,object->VpiForIncStmt());
  need(init.size()<=16&&inc.size()<=16&&one(h,vpiCondition),"bounded for control list and condition required");
  std::string out="{\"kind\":\"block\",\"items\":[";for(auto item:init)out+=stmt(item,depth+1,true)+",";
  out+="{\"kind\":\"while\",\"condition\":"+expr(one(h,vpiCondition))+",\"body\":{\"kind\":\"block\",\"items\":["+stmt(one(h,vpiStmt),depth+1);
  for(auto item:inc)out+=","+stmt(item,depth+1,true);return out+"]}}]}";
 }
 if(type(h)==vpiOperation){int op=vpi_get(vpiOpType,h);need(op==vpiPostIncOp||op==vpiPreIncOp||op==vpiPostDecOp||op==vpiPreDecOp,"only standalone increment/decrement statements supported");auto args=many(h,vpiOperand);need(args.size()==1,"increment requires one destination");auto target=expr(args[0]);return "{\"kind\":\"blocking\",\"target\":"+target+",\"value\":{\"kind\":"+q(op==vpiPostDecOp||op==vpiPreDecOp?"sub":"add")+",\"args\":["+target+",{\"kind\":\"const\",\"bits\":\"00000000000000000000000000000001\",\"signed\":true}]}}";}
 if(type(h)==vpiBegin){std::string s="{\"kind\":\"block\",\"items\":[";bool first=true;for(auto a:many(h,vpiStmt)){if(!first)s+=",";first=false;s+=stmt(a,depth+1);}return s+"]}";}
 if(type(h)==vpiAssignment){need(!one(h,vpiDelayControl)&&!one(h,vpiEventControl),"assignment timing unsupported");
  auto lhs=one(h,vpiLhs),rhs=one(h,vpiRhs),array=one(lhs,vpiActual);
  if(type(rhs)==vpiMethodFuncCall&&name(rhs)=="new"){
   need(!inside_call&&vpi_get(vpiBlocking,h)&&type(lhs)==vpiRefObj&&dynamic_array(array)&&flat_symbols.count(identity(array)),"dynamic allocation requires a blocking whole-array destination");
   auto args=many(rhs,vpiArgument);need(args.size()==1||args.size()==2,"dynamic new requires count and optional self initializer");
   if(args.size()==2)need(type(args[1])==vpiRefObj&&identity(one(args[1],vpiActual))==identity(array),"dynamic resize initializer must be the same array declaration");
   return "{\"kind\":\"allocate\",\"name\":"+q(signal_name(lhs))+",\"count\":"+expr(args[0])+",\"preserve\":"+(args.size()==2?"true":"false")+"}";
  }target_shape(one(h,vpiLhs));return "{\"kind\":"+q(control||vpi_get(vpiBlocking,h)?"blocking":"nba")+",\"target\":"+(type(one(h,vpiLhs))==vpiHierPath?packed_field_expression(one(h,vpiLhs),true):expr(one(h,vpiLhs)))+",\"value\":"+expr(one(h,vpiRhs))+"}";}
 if(type(h)==vpiIf||type(h)==vpiIfElse){auto e=one(h,vpiElseStmt);return "{\"kind\":\"if\",\"condition\":"+expr(one(h,vpiCondition))+",\"then\":"+stmt(one(h,vpiStmt),depth+1)+",\"else\":"+(e?stmt(e,depth+1):"{\"kind\":\"block\",\"items\":[]}")+"}";}
 if(type(h)==vpiCase){need(vpi_get(vpiCaseType,h)==vpiCaseExact,"only exact case supported");std::string s="{\"kind\":\"case\",\"value\":"+expr(one(h,vpiCondition))+",\"items\":[";bool first=true;for(auto item:many(h,vpiCaseItem)){if(!first)s+=",";first=false;auto conditions=many(item,vpiExpr);need(conditions.size()<=1,"multiple case labels unsupported");s+="{\"match\":"+(conditions.empty()?"null":expr(conditions[0]))+",\"body\":"+stmt(one(item,vpiStmt),depth+1)+"}";}return s+"]}";}
 throw std::runtime_error("unsupported statement type "+std::to_string(type(h)));
}
#include "rtl_flatten.h"
}
int main(int argc,char** argv){
#ifdef VELOSIM_EXPORTER_COVERAGE
 // Keep exporter measurements separate from instrumented simulator libraries in the same test run.
 if(const char* destination=std::getenv("VELOSIM_UHDM_PROFILE_FILE");destination&&*destination)__llvm_profile_set_filename(destination);
#endif
 if(argc!=4){std::cerr<<"usage: velosim-uhdm-rtl <design.uhdm> <rtl.json> <top>\n";return 2;}
 try{
  UHDM::Serializer serializer;store=&serializer;auto designs=serializer.Restore(std::string(argv[1]));need(designs.size()==1,"one design required");auto modules=many(designs[0],UHDM::uhdmtopModules);need(modules.size()==1,"one top required");mod=modules[0];need(name(mod)==argv[3]||name(mod)==std::string("work@")+argv[3],"top mismatch");
  for(auto spec:many(designs[0],vpiTypedef))if(type(spec)==vpiVoidTypespec&&name(spec)=="velosim frontend capability: union tags v1")union_tag_provenance=true;
  for(auto original:many(designs[0],UHDM::uhdmallModules)){collect_original_expressions(original);collect_parameter_syntax(original);collect_port_initializers(original);}
  auto out=flatten_design(mod);
  output_budget(out);std::ofstream file(argv[2],std::ios::binary|std::ios::trunc);file<<out;need(bool(file),"write failed");return 0;
 }catch(const std::exception& e){std::cerr<<"RTL lowering rejected: "<<e.what()<<"\n";return 1;}
}
