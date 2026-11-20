#include "../../native/rtl/runtime.h"
#include <cstdlib>
#include <iostream>
using namespace vsrtl;
void check(bool value){if(!value){std::cerr<<"associative assertion failed\n";std::abort();}}
int main(){
 AssociativeStorage cells;W key=word(42,0,0,32);
 check(assoc_traverse(cells,key,32,true,0).v==0&&key.v==42);
 check(text(assoc_read(cells,key,32,true,8,false))=="xxxxxxxx");
 check(text(assoc_read(cells,key,32,true,8,true))=="00000000");
 check(cells.empty()); // Missing reads must not allocate default entries.
 assoc_write(cells,word(3,0,0,32),word(13,0,0,8),32,true,8,false);
 assoc_write(cells,word(0xfffffffe,0,0,32),word(12,0,0,8),32,true,8,false);
 assoc_write(cells,word(0,0,0,32),word(10,0,0,8),32,true,8,false);
 check(cells.size()==3&&assoc_exists(cells,word(3,0,0,32),32,true).v==1);
 check(assoc_traverse(cells,key,32,true,0).v==1&&key.v==0xfffffffe);
 check(assoc_traverse(cells,key,32,true,2).v==1&&key.v==0);
 check(assoc_traverse(cells,key,32,true,2).v==1&&key.v==3);
 check(assoc_traverse(cells,key,32,true,2).v==0&&key.v==3);
 check(assoc_traverse(cells,key,32,true,3).v==1&&key.v==0);
 check(assoc_traverse(cells,key,32,true,1).v==1&&key.v==3);
 key=word(1,0,0,32);check(assoc_traverse(cells,key,32,true,2).v==1&&key.v==3);
 key=word(1,0,0,32);check(assoc_traverse(cells,key,32,true,3).v==1&&key.v==0);
 assoc_write(cells,word(3,0,0,32),parse("xz10"),32,true,8,true);
 check(cells.size()==3&&text(assoc_read(cells,word(3,0,0,32),32,true,8,true))=="00000010");
 assoc_write(cells,unknown(32),word(7,0,0,8),32,true,8,false);
 check(cells.size()==3&&assoc_exists(cells,unknown(32),32,true).v==0);
 assoc_delete(cells,unknown(32),32,true);check(cells.size()==3);
 assoc_delete(cells,word(3,0,0,32),32,true);check(cells.size()==2);
 key=unknown(32);check(assoc_traverse(cells,key,32,true,2).v==0&&bool(key.x));
 bool rejected=false;try{assoc_traverse(cells,key,32,true,4);}catch(const std::invalid_argument&){rejected=true;}check(rejected);
 cells.clear();auto wide=word(Bits(1)<<100,0,0,128),payload=parse(std::string(252,'1')+"01xz");
 assoc_write(cells,wide,payload,128,false,256,false);check(text(assoc_read(cells,wide,128,false,256,false))==text(payload));
 check(assoc_exists(cells,word(0,0,0,128),128,false).v==0);
 cells.clear();for(unsigned i=0;i<256;++i)assoc_write(cells,word(i,0,0,32),word(i,0,0,8),32,false,8,false);
 assoc_write(cells,word(0,0,0,32),word(255,0,0,8),32,false,8,false);check(cells.size()==256);
 rejected=false;try{assoc_write(cells,word(256,0,0,32),word(0,0,0,8),32,false,8,false);}catch(const std::length_error&){rejected=true;}
 check(rejected&&cells.size()==256&&assoc_read(cells,word(0,0,0,32),32,false,8,false).v==255);
 std::cout<<"associative checks passed\n";
}
