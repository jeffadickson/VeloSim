// Exercise production traversal on a small shared UHDM graph, without a parser.
#define main velosim_exporter_program_main
#include "../../native/uhdm/rtl.cpp"
#undef main
int main(){
 UHDM::Serializer serializer;store=&serializer;union_tag_provenance=true;
 auto leaf=serializer.MakeLogic_typespec();
 auto layer=[&](UHDM::typespec* child){
  auto result=serializer.MakeUnion_typespec();result->VpiPacked(true);result->Members(serializer.MakeTypespec_memberVec());
  for(int i=0;i<8;++i){auto member=serializer.MakeTypespec_member();member->VpiName("m"+std::to_string(i));auto ref=serializer.MakeRef_typespec();ref->Actual_typespec(child);member->Typespec(ref);result->Members()->push_back(member);}
  return result;
 };
 auto small=layer(leaf);UHDM::typespec* large=small;for(int i=0;i<7;++i)large=layer(large);
 LoopHandles handles;auto smallHandle=handles.add(small),largeHandle=handles.add(large);
 auto verify=[&](H h,bool state,bool rejection){bool caught=false;try{if(state)need(!aggregate_state(h,0),"four-state layout expected");else need(aggregate_width(h,0)==1,"one-bit union expected");}catch(const std::runtime_error& error){if(std::string(error.what())!="packed type traversal budget exceeded")throw;caught=true;}need(caught==rejection,"type query budget regression");need(PackedTypeQuery::active==0,"query budget must unwind after failure");};
 verify(smallHandle,false,false);verify(largeHandle,false,true);verify(smallHandle,false,false);
 verify(largeHandle,true,true);verify(smallHandle,true,false);
 std::cout<<"Shared union DAG width/state guards and recovery pass\n";
}