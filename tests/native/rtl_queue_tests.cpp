#include "../../native/rtl/runtime.h"
#include <cstdlib>
#include <iostream>
using namespace vsrtl;
void check(bool value){if(!value){std::cerr<<"queue assertion failed\n";std::abort();}}
int main(){
 std::vector<W> cells;
 check(text(queue_pop(cells,8,false,true))=="xxxxxxxx");
 check(text(queue_pop(cells,8,true,false))=="00000000");
 queue_push(cells,word(1,0,0,8),8,false,false);
 queue_push(cells,word(2,0,0,8),8,false,true);
 check(queue_pop(cells,8,false,false).v==1);
 check(queue_pop(cells,8,false,true).v==2);
 queue_write(cells,word(0,0,0,9),parse("xz10"),8,true);
 check(cells.size()==1);
 check(text(cells.at(0))=="00000010");
 queue_write(cells,unknown(9),word(7,0,0,8),8,false);
 queue_write(cells,word(9,0,0,9),word(7,0,0,8),8,false);
 check(cells.size()==1&&cells[0].v==2);
 queue_delete(cells,unknown(9));queue_delete(cells,word(9,0,0,9));check(cells.size()==1);
 queue_delete(cells,word(0,0,0,9));check(cells.empty());
 // A multi-limb four-state payload survives both ends without host-integer truncation.
 auto wide=parse(std::string(252,'1')+"01xz");queue_push(cells,wide,256,false,true);
 check(text(queue_pop(cells,256,false,false))==text(wide));
 for(unsigned i=0;i<256;++i)queue_push(cells,word(i,0,0,8),8,false,false);
 auto before=cells;
 for(bool front:{false,true}){bool rejected=false;try{queue_push(cells,word(0,0,0,8),8,false,front);}catch(const std::length_error&){rejected=true;}check(rejected&&cells.size()==before.size()&&text(cells.front())==text(before.front())&&text(cells.back())==text(before.back()));}
 bool rejected=false;try{queue_write(cells,word(256,0,0,9),word(0,0,0,8),8,false);}catch(const std::length_error&){rejected=true;}check(rejected&&cells.size()==256);
 std::cout<<"queue checks passed\n";
}
