#include "../../native/rtl/runtime.h"
#include <cstdlib>
#include <iostream>
using namespace vsrtl;
void check(bool b){if(!b){std::cerr<<"RTL runtime assertion failed\n";std::abort();}}
int main(){
 for(auto a:{parse("0"),parse("1"),parse("x"),parse("z")})for(auto b:{parse("0"),parse("1"),parse("x"),parse("z")}){
  auto and_value=logical_and(a,b);check(text(and_value)==(text(a)=="0"||text(b)=="0"?"0":text(a)=="1"&&text(b)=="1"?"1":"x"));
  check(text(logical_not(a))==(text(a)=="0"?"1":text(a)=="1"?"0":"x"));
  auto l=text(a),r=text(b);
  check(text(bit_not(a,1))==(l=="0"?"1":l=="1"?"0":"x"));
  check(text(bit_binary(a,b,1,0))==(l=="0"||r=="0"?"0":l=="1"&&r=="1"?"1":"x"));
  check(text(bit_binary(a,b,1,1))==(l=="1"||r=="1"?"1":l=="0"&&r=="0"?"0":"x"));
  check(text(bit_binary(a,b,1,2))==((l=="x"||l=="z"||r=="x"||r=="z")?"x":l==r?"0":"1"));
  check(exact(a,b)==(text(a)==text(b)));check(text(concat(a,b))==text(a)+text(b));
 }
 check(text(resize(parse("xz"),4))=="00xz");check(text(equal(parse("x0"),parse("11")))=="0");check(text(equal(parse("x0"),parse("10")))=="x");
 check(text(bit_not(parse("z1"),4))=="11x0");check(text(bit_binary(parse("z"),parse("1010"),4,0))=="0000");
 check(text(bit_not(parse(std::string(64,'0')),64))==std::string(64,'1'));
 check(text(arithmetic(parse("1111"),parse("1"),4,false))=="0000");check(text(arithmetic(parse("0000"),parse("1"),4,true))=="1111");
 check(text(arithmetic(parse("x000"),parse("1"),2,false))=="xx");
 check(text(arithmetic(parse(std::string(64,'1')),parse("1"),64,false))==std::string(64,'0'));
 std::vector<W> m(4,unknown(8));check(text(read(m,parse("xx"),8))=="xxxxxxxx");check(text(read(m,parse("100"),8))=="xxxxxxxx");
 write(m,parse("x"),parse("1"),8);check(text(m[0])=="xxxxxxxx");write(m,parse("100"),parse("1"),8);check(text(m[0])=="xxxxxxxx");
 write(m,parse("01"),parse("xz"),8);check(text(read(m,parse("01"),8))=="000000xz");
 bool invalid=false;try{parse("bad");}catch(const std::exception&){invalid=true;}check(invalid);
}
