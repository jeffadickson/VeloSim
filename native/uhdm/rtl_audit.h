#pragma once
#include <uhdm/UhdmListener.h>
#include <uhdm/uhdm.h>
#include <map>
#include <stdexcept>
#include <string>

namespace velosim {
// Discovery uses UHDM's generated traversal, including relations absent from
// the bounded RTL lowerer. Parsing a construct must never silently erase it.
// The existing lowerer remains responsible for statement/type/ownership checks.
class RtlExecutableAudit final : public UHDM::UhdmListener {
 std::map<uint32_t,size_t> counts_;
 size_t count_=0;
 size_t node_budget_,depth_budget_;
 static void unsupported(const UHDM::any* object,const char* construct) {
  std::string file;auto owner=object;
  for(unsigned depth=0;owner&&depth<128;++depth){
   file=owner->VpiFile();if(!file.empty())break;
   auto parent=owner->VpiParent();if(parent==owner)break;owner=parent;
  }
  if(file.empty())file="<unknown source>";
  throw std::runtime_error(std::string("unsupported executable UHDM construct: ")+construct+
   " at "+file+":"+std::to_string(object->VpiLineNo())+":"+std::to_string(object->VpiColumnNo()));
 }
public:
 explicit RtlExecutableAudit(size_t node_budget=100000,size_t depth_budget=128)
  :node_budget_(node_budget),depth_budget_(depth_budget) {
  if(!node_budget||node_budget>100000||!depth_budget||depth_budget>128)
   throw std::invalid_argument("invalid UHDM executable audit budget");
 }
 // Keep the audit bounded even on cyclic/shared or adversarial restored graphs.
 void enterAny(const UHDM::any* object) override {
  if(++count_>node_budget_||getCallstack().size()>depth_budget_)
   throw std::runtime_error("UHDM executable audit traversal budget exceeded");
  ++counts_[object->VpiType()];
 }
 void enterAssert_stmt(const UHDM::assert_stmt* object) override {unsupported(object,"concurrent assertion");}
 void enterAssume(const UHDM::assume* object) override {unsupported(object,"concurrent assumption");}
 void enterCover(const UHDM::cover* object) override {unsupported(object,"concurrent cover");}
 void enterImmediate_assert(const UHDM::immediate_assert* object) override {unsupported(object,"immediate assertion");}
 void enterImmediate_assume(const UHDM::immediate_assume* object) override {unsupported(object,"immediate assumption");}
 void enterImmediate_cover(const UHDM::immediate_cover* object) override {unsupported(object,"immediate cover");}
 void enterFinal_stmt(const UHDM::final_stmt* object) override {unsupported(object,"final process");}
 void enterClocking_block(const UHDM::clocking_block* object) override {unsupported(object,"clocking block");}
 void enterProgram(const UHDM::program* object) override {unsupported(object,"program");}
 void enterInterface_inst(const UHDM::interface_inst* object) override {unsupported(object,"interface instance");}
 void enterPrimitives(const UHDM::any*,const UHDM::VectorOfprimitive& items) override {
  if(!items.empty())unsupported(items.front(),"primitive instance");
 }
 void enterPrimitive_arrays(const UHDM::any*,const UHDM::VectorOfprimitive_array& items) override {
  if(!items.empty())unsupported(items.front(),"primitive array");
 }
 size_t count()const{return count_;}
 const std::map<uint32_t,size_t>& counts()const{return counts_;}
};
}
