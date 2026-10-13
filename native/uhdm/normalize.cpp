// VeloSim experimental UHDM -> normalized counter process lowerer.
// Supports only a single always_ff @(posedge clk) with if (!rst_n)
// count <= '0; else count <= count + 1'b1;
// Reject everything else. Requires pinned UHDM library to build.
#include <uhdm/uhdm.h>
#include <uhdm/vpi_user.h>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>
#include "json_escape.h"
namespace {
using Handle=vpiHandle;
struct Error:std::runtime_error {using std::runtime_error::runtime_error;};
std::string name(Handle h){const char* s=h?vpi_get_str(vpiName,h):nullptr;return s?s:"";}
int kind(Handle h){return h?vpi_get(vpiType,h):-1;}
void check(bool ok,const std::string& msg){if(!ok)throw Error(msg);}
Handle one(Handle h,int relation){return h?vpi_handle(relation,h):nullptr;}
std::vector<Handle> many(Handle h,int relation){
 std::vector<Handle> items;
 Handle iter=h?vpi_iterate(relation,h):nullptr;
 if(iter){for(Handle x=vpi_scan(iter);x;x=vpi_scan(iter))items.push_back(x);
   vpi_release_handle(iter);}
 return items;
}
Handle sole(const std::vector<Handle>& v,const char* what){
 check(v.size()==1,std::string("expected exactly one ")+what);return v[0];
}
Handle unbox(Handle stmt){
 // UHDM process bodies may use an explicit begin/end with one statement.
 for(int depth=0;depth<4;depth++){
   if(kind(stmt)!=vpiBegin && kind(stmt)!=vpiNamedBegin)break;
   stmt=sole(many(stmt,vpiStmt),"statement inside begin");
 }
 return stmt;
}
std::string reference(Handle expr){
 check(expr!=nullptr,"missing reference");
 const std::string s=name(expr);
 check(!s.empty(),"expression is not a named signal reference");
 check(kind(expr)!=vpiOperation && kind(expr)!=vpiConstant,
       "expression is not a simple reference");
 return s;
}
bool zero(Handle expr){
 check(kind(expr)==vpiConstant,"expected zero constant");
 s_vpi_value v{};v.format=vpiBinStrVal;vpi_get_value(expr,&v);
 check(v.value.str!=nullptr,"constant value unavailable");
 std::string bits=v.value.str;
 check(!bits.empty(),"empty constant");
 for(char c:bits)if(c!='0')return false;
 return true;
}
bool one_const(Handle expr){
 check(kind(expr)==vpiConstant,"expected increment constant");
 s_vpi_value v{};v.format=vpiBinStrVal;vpi_get_value(expr,&v);
 check(v.value.str!=nullptr,"constant value unavailable");
 std::string bits=v.value.str;
 check(!bits.empty(),"empty constant");
 if(bits.back()!='1')return false;
 for(size_t i=0;i+1<bits.size();i++)if(bits[i]!='0')return false;
 return true;
}
struct Assign {std::string target;Handle rhs;};
Assign nba(Handle stmt){
 stmt=unbox(stmt);
 check(kind(stmt)==vpiAssignment,"expected procedural assignment");
 check(vpi_get(vpiBlocking,stmt)==0,"blocking assignment unsupported");
 Handle lhs=one(stmt,vpiLhs),rhs=one(stmt,vpiRhs);
 check(lhs&&rhs,"assignment missing lhs/rhs");
 return {reference(lhs),rhs};
}
struct Proc {std::string clk,rst,dst;};
Proc process(Handle h){
 check(kind(h)==vpiAlways,"expected always process");
 check(vpi_get(vpiAlwaysType,h)==vpiAlwaysFF,"only always_ff supported");
 Handle event=unbox(one(h,vpiStmt));
 check(kind(event)==vpiEventControl,"expected event control");
 Handle edge=one(event,vpiCondition);
 check(kind(edge)==vpiOperation && vpi_get(vpiOpType,edge)==vpiPosedgeOp,
       "only posedge supported");
 std::string clk=reference(sole(many(edge,vpiOperand),"posedge operand"));
 Handle body=unbox(one(event,vpiStmt));
 check(kind(body)==vpiIfElse,"expected if/else");
 Handle cond=one(body,vpiCondition);
 check(kind(cond)==vpiOperation && vpi_get(vpiOpType,cond)==vpiNotOp,
       "expected !rst_n");
 std::string rst=reference(sole(many(cond,vpiOperand),"reset operand"));
 Assign yes=nba(one(body,vpiStmt)),no=nba(one(body,vpiElseStmt));
 check(yes.target==no.target,"branch destinations differ");
 check(zero(yes.rhs),"expected zero reset assignment");
 check(kind(no.rhs)==vpiOperation && vpi_get(vpiOpType,no.rhs)==vpiAddOp,
       "expected addition");
 auto operands=many(no.rhs,vpiOperand);
 check(operands.size()==2,"expected binary addition");
 check(reference(operands[0])==yes.target && one_const(operands[1]),
       "expected count + 1");
 return {clk,rst,yes.target};
}
void write(const std::string& output,const std::map<std::string,int>& widths,const Proc& p){
 std::ofstream out(output,std::ios::binary|std::ios::trunc);
 check(bool(out),"cannot open output");
 out<<"{\"format\":\"velosim.uhdm.normalized.v1\",\"signals\":[";
 bool first=true;
 for(const auto& [n,w]:widths){
  if(!first)out<<",";
  first=false;
  out<<"{\"name\":"<<velosim::json_escape(n)<<",\"width\":"<<w<<"}";
 }
 out<<"],\"processes\":[{\"kind\":\"always_ff\",\"event\":{\"edge\":\"posedge\",\"signal\":"<<velosim::json_escape(p.clk)
    <<"},\"body\":{\"kind\":\"if\",\"condition\":{\"kind\":\"not\",\"operand\":{\"kind\":\"ref\",\"name\":"<<velosim::json_escape(p.rst)
    <<"}},\"then\":{\"kind\":\"nba\",\"target\":"<<velosim::json_escape(p.dst)
    <<",\"value\":{\"kind\":\"const\",\"value\":0}},\"else\":{\"kind\":\"nba\",\"target\":"<<velosim::json_escape(p.dst)
    <<",\"value\":{\"kind\":\"add\",\"lhs\":{\"kind\":\"ref\",\"name\":"<<velosim::json_escape(p.dst)
    <<"},\"rhs\":{\"kind\":\"const\",\"value\":1}}}}}]}\n";
 check(bool(out),"output write failed");
}
}
int main(int argc,char** argv){
 if(argc!=4){std::cerr<<"usage: velosim-uhdm-normalize <surelog.uhdm> <normalized.json> <top>\n";return 2;}
 try{
  UHDM::Serializer serializer;
  auto designs=serializer.Restore(std::string(argv[1]));
  check(designs.size()==1,"expected one UHDM design");
  Handle design=designs[0];
  check(kind(design)==vpiDesign,"not a UHDM design");
  auto modules=many(design,UHDM::uhdmtopModules);
  Handle mod=nullptr;
  for(auto h:modules)if(name(h)==argv[3]){check(!mod,"duplicate top");mod=h;}
  check(mod!=nullptr,"top not found");
  check(many(mod,vpiModule).empty(),"nested modules unsupported");
  auto procs=many(mod,vpiProcess);
  check(procs.size()==1,"expected one process");
  check(many(mod,vpiContAssign).empty(),"continuous assignments unsupported");
  Proc p=process(procs[0]);
  std::map<std::string,int> widths;
  for(int relation:{vpiPort,vpiNet,vpiReg,vpiVariables}){
    for(auto h:many(mod,relation)){
      std::string n=name(h);int w=vpi_get(vpiSize,h);
      if(n.empty())continue;
      check(w>=1&&w<=64,"signal width outside 1..64");
      auto [it,inserted]=widths.emplace(n,w);
      check(inserted||it->second==w,"conflicting widths");
    }
  }
  for(const auto& n:{p.clk,p.rst,p.dst})check(widths.count(n)!=0,"process signal not declared: "+n);
  check(widths.at(p.clk)==1&&widths.at(p.rst)==1,"clock and reset must be scalar");
  check(widths.size()==3,"extra signals not yet supported");
  write(argv[2],widths,p);
  return 0;
 }catch(const std::exception& e){std::cerr<<"UHDM lowering rejected design: "<<e.what()<<"\n";return 1;}
}
