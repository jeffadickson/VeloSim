#include "../../native/uhdm/rtl_audit.h"
#include <iostream>

namespace {
void check(bool value){if(!value)throw std::runtime_error("UHDM audit assertion failed");}
}
int main(){
 UHDM::Serializer serializer;
 auto module=serializer.MakeModule_inst();module->VpiFile("audit_fixture.sv");
 auto variable=serializer.MakeLogic_var();variable->VpiParent(module);
 module->Variables(serializer.MakeVariablesVec());module->Variables()->push_back(variable);
 velosim::RtlExecutableAudit valid;valid.listenAny(module);
 check(valid.count()>=2&&valid.counts().count(vpiModule));
 // Shared declarations are visited once by the upstream listener, not expanded
 // repeatedly by a bespoke relation loop.
 const auto count=valid.count();valid.listenAny(module);check(valid.count()==count);
 bool rejected=false;
 try{velosim::RtlExecutableAudit bounded(1);bounded.listenAny(module);}
 catch(const std::runtime_error& error){rejected=std::string(error.what()).find("budget exceeded")!=std::string::npos;}
 check(rejected);
 for(auto limits:{std::pair<size_t,size_t>{0,1},{1,0},{100001,1},{1,129}}){
  rejected=false;try{velosim::RtlExecutableAudit bad(limits.first,limits.second);}
  catch(const std::invalid_argument&){rejected=true;}check(rejected);
 }
 module->Assertions(serializer.MakeAnyVec());
 for(auto item:std::vector<UHDM::any*>{serializer.MakeAssert_stmt(),serializer.MakeImmediate_assert(),serializer.MakeImmediate_assume(),serializer.MakeImmediate_cover(),serializer.MakeAssume(),serializer.MakeCover(),serializer.MakeFinal_stmt(),serializer.MakeClocking_block(),serializer.MakeProgram(),serializer.MakeInterface_inst()}){
  item->VpiParent(module);item->VpiLineNo(7);item->VpiColumnNo(3);
  module->Assertions()->clear();module->Assertions()->push_back(item);rejected=false;
  try{velosim::RtlExecutableAudit audit;audit.listenAny(module);}
  catch(const std::runtime_error& error){const std::string message=error.what();rejected=message.find("unsupported executable UHDM construct")!=std::string::npos&&message.find("audit_fixture.sv:7:3")!=std::string::npos;}
  check(rejected);
 }
 std::cout<<"Generated UHDM traversal, shared graph, rejection, source and budget checks pass\n";
}
