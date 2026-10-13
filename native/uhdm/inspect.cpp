// Experimental UHDM inventory reader. Not a SystemVerilog executor.
#include <uhdm/uhdm.h>
#include <uhdm/vpi_user.h>
#include "json_escape.h"
#include <iostream>
#include <stdexcept>
#include <string>
namespace {
std::string prop(int key,vpiHandle h){const char* s=vpi_get_str(key,h);return s?s:"";}
void emit_collection(vpiHandle parent,int kind,const char* label,bool& first){
  vpiHandle iter=vpi_iterate(kind,parent);
  if(!iter)return;
  for(vpiHandle obj=vpi_scan(iter);obj;obj=vpi_scan(iter)){
    if(!first)std::cout<<",";
    first=false;
    std::cout<<"{\"kind\":"<<vpi_get(vpiType,obj)
             <<",\"name\":"<<velosim::json_escape(prop(vpiName,obj))
             <<",\"file\":"<<velosim::json_escape(prop(vpiFile,obj))
             <<",\"line\":"<<vpi_get(vpiLineNo,obj)
             <<",\"width\":"<<vpi_get(vpiSize,obj)
             <<",\"collection\":"<<velosim::json_escape(label)<<"}";
    vpi_release_handle(obj);
  }
  vpi_release_handle(iter);
}
void emit_module(vpiHandle mod,unsigned depth){
  if(depth>128)throw std::runtime_error("module hierarchy exceeds 128 levels");
  std::cout<<"{\"name\":"<<velosim::json_escape(prop(vpiName,mod))
           <<",\"definition\":"<<velosim::json_escape(prop(vpiDefName,mod))
           <<",\"objects\":[";
  bool first=true;
  emit_collection(mod,vpiNet,"nets",first);
  emit_collection(mod,vpiReg,"registers",first);
  emit_collection(mod,vpiVariables,"variables",first);
  emit_collection(mod,vpiProcess,"processes",first);
  emit_collection(mod,vpiContAssign,"continuous_assignments",first);
  std::cout<<"],\"children\":[";
  vpiHandle iter=vpi_iterate(vpiModule,mod);
  bool first_child=true;
  if(iter){
    for(vpiHandle child=vpi_scan(iter);child;child=vpi_scan(iter)){
      if(!first_child)std::cout<<",";
      first_child=false;
      emit_module(child,depth+1);
      vpi_release_handle(child);
    }
    vpi_release_handle(iter);
  }
  std::cout<<"]}";
}
}
int main(int argc,char** argv){
  if(argc!=2){std::cerr<<"usage: velosim-uhdm-inspect <surelog.uhdm>\n";return 2;}
  try{
    UHDM::Serializer serializer;
    auto designs=serializer.Restore(std::string(argv[1]));
    if(designs.empty()){std::cerr<<"empty UHDM design\n";return 1;}
    std::cout<<"{\"format\":\"velosim.uhdm.inventory.v1\",\"designs\":[";
    bool first_design=true;
    for(vpiHandle design:designs){
      if(vpi_get(vpiType,design)!=vpiDesign)continue;
      if(!first_design)std::cout<<",";
      first_design=false;
      std::cout<<"{\"name\":"<<velosim::json_escape(prop(vpiName,design))
               <<",\"top_modules\":[";
      vpiHandle iter=vpi_iterate(UHDM::uhdmtopModules,design);
      bool first_mod=true;
      if(iter){
        for(vpiHandle mod=vpi_scan(iter);mod;mod=vpi_scan(iter)){
          if(!first_mod)std::cout<<",";
          first_mod=false;
          emit_module(mod,0);
          vpi_release_handle(mod);
        }
        vpi_release_handle(iter);
      }
      std::cout<<"]}";
    }
    std::cout<<"]}\n";
    return first_design?1:0;
  }catch(const std::exception& e){
    std::cerr<<"UHDM inspection failed: "<<e.what()<<"\n";return 1;
  }
}
