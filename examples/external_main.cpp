#include "Vexternal_counter.h"
#include <fstream>
#include <iostream>
#include <sstream>
int main(int argc,char** argv){try{
 if(argc!=3)throw std::runtime_error("usage: host plugin.so stimulus.txt");
 Vexternal_counter top;top.useVpi();top.loadVpi(argv[1]);
 std::ifstream file(argv[2]);if(!file)throw std::runtime_error("stimulus missing");
 std::string line;int cycle=0;
 while(std::getline(file,line)){
  std::istringstream row(line);std::string rst,enable,data,extra;
  if(!(row>>data>>enable>>rst)||(row>>extra))throw std::runtime_error("invalid stimulus");
  top.clk=0;top.data.bits(data);top.enable.bits(enable);top.rst_n.bits(rst);top.eval();
  top.timeInc(1);top.clk=1;top.eval();
  // A second high-clock eval must not clock the design twice.
  top.eval();
  std::cout<<"VS_RTL "<<++cycle<<' '<<top.mirrored.bits()<<' '<<top.q.bits()<<' '<<top.sampled.bits()<<'\n';
 }
 top.final();return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
