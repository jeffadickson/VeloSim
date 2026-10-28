#include "../../native/rtl/runtime.h"
#include <slang/numeric/SVInt.h>
#include <chrono>
#include <fstream>
#include <iostream>
#include <sstream>
#include <vector>

namespace {
using slang::SVInt;
SVInt parse(const std::string& bits,bool sign){return SVInt::fromString(std::to_string(bits.size())+(sign?"'sb":"'b")+bits);}
std::string binary(const SVInt& value){
 std::string result(value.getBitWidth(),'0');
 for(unsigned i=0;i<value.getBitWidth();++i)result[value.getBitWidth()-1-i]=value[int(i)].toChar();
 return result;
}
SVInt candidate(const SVInt& a,const SVInt& b,const std::string& op){
 if(op=="add")return a+b;if(op=="sub")return a-b;if(op=="mul")return a*b;
 if(op=="div")return a/b;if(op=="mod")return a%b;if(op=="and")return a&b;if(op=="or")return a|b;
 if(op=="xor")return a^b;throw std::invalid_argument("unknown numeric operation");
}
vsrtl::W current(vsrtl::W a,vsrtl::W b,const std::string& op,bool sign){
 if(op=="add"||op=="sub")return vsrtl::arithmetic(a,b,a.w,op=="sub");
 if(op=="mul"||op=="div"||op=="mod"){
  int operation=op=="mul"?0:op=="div"?1:2;
  return sign?vsrtl::signed_product(a,b,a.w,operation):vsrtl::product(a,b,a.w,operation);
 }
 return vsrtl::bit_binary(a,b,a.w,op=="and"?0:op=="or"?1:2);
}
template<class F> long long elapsed(F action){auto start=std::chrono::steady_clock::now();action();return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now()-start).count();}
}
int main(int argc,char** argv){try{
 if(argc!=2)throw std::invalid_argument("Usage: svint_qualification <numeric-corpus.tsv>");
 std::ifstream file(argv[1]);if(!file)throw std::runtime_error("numeric corpus missing");
 std::string line;size_t rows=0,current_rows=0;
 while(std::getline(file,line)){
  if(line.empty()||line[0]=='#')continue;
  std::istringstream fields(line);unsigned id,width,sign;std::string op,a,b,expected;
  if(!(fields>>id>>width>>sign>>op>>a>>b>>expected)||!width||width>1024||sign>1||width!=a.size()||width!=b.size()||width!=expected.size())throw std::runtime_error("malformed numeric fixture");
  auto actual=binary(candidate(parse(a,sign),parse(b,sign),op));
  if(actual!=expected)throw std::runtime_error("SVInt oracle mismatch row "+std::to_string(id));
  if(width<=256){if(vsrtl::text(current(vsrtl::parse(a),vsrtl::parse(b),op,sign))!=expected)throw std::runtime_error("current runtime oracle mismatch row "+std::to_string(id));++current_rows;}
  ++rows;
 }
 if(rows<20000)throw std::runtime_error("numeric corpus incomplete");
 // Measure known 256-bit arithmetic cores separately from string conversion,
 // parsing, four-state workloads and generated model execution. Compare final
 // full-width checksums so dead-code removal or differing results cannot count.
 const std::string seed="1"+std::string(240,'0')+"101001101010101";
 auto initial=vsrtl::parse(seed).v;auto initial_sv=parse(seed,false);
 constexpr unsigned iterations=50000;
 vsrtl::Bits reference;SVInt result;std::vector<long long> native_ns,svint_ns;
 for(unsigned trial=0;trial<4;++trial){
  auto native_time=elapsed([&]{auto value=initial;for(unsigned i=0;i<iterations;++i)value=value*vsrtl::Bits(6364136223846793005ULL)+vsrtl::Bits(i+1442695040888963407ULL);reference=value;});
  auto library_time=elapsed([&]{auto value=initial_sv;SVInt multiplier(256,6364136223846793005ULL,false);for(unsigned i=0;i<iterations;++i)value=value*multiplier+SVInt(256,i+1442695040888963407ULL,false);result=std::move(value);});
  if(binary(result)!=vsrtl::text(vsrtl::word(reference,0,0,256)))throw std::runtime_error("benchmark checksum mismatch");
  if(trial){native_ns.push_back(native_time);svint_ns.push_back(library_time);}
 }
 std::cout<<"{\"status\":\"pass\",\"slang_revision\":\"42710c788ce76cd08ce2080773e0b8286c428a05\",\"oracle_rows\":"<<rows<<",\"current_runtime_rows\":"<<current_rows<<",\"seed\":20261027,\"scope\":\"known256 arithmetic core; conversion and whole-model execution excluded\",\"iterations\":"<<iterations<<",\"native_ns\":[";
 for(size_t i=0;i<native_ns.size();++i){if(i)std::cout<<',';std::cout<<native_ns[i];}std::cout<<"],\"svint_ns\":[";
 for(size_t i=0;i<svint_ns.size();++i){if(i)std::cout<<',';std::cout<<svint_ns[i];}std::cout<<"],\"checksum\":\""<<binary(result)<<"\"}\n";
 return 0;
}catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}}
