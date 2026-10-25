// Decode the serialized constant payload without ExprEval's 64-bit numeric path.
std::string constant_bits(const UHDM::constant* object,bool plain_decimal=false){
 using U=vsrtl::Bits;
 int width=plain_decimal?32:object->VpiSize();auto literal=object->VpiDecompile();if(width<1&&(literal=="'0"||literal=="'1"||literal=="'x"||literal=="'z"))width=1;need(width>=1&&width<=256,"constant width must be1..256");
 std::string raw(object->VpiValue());need(raw.size()<=512,"constant payload budget exceeded");
 auto colon=raw.find(':');need(colon!=std::string::npos,"constant payload tag required");auto tag=raw.substr(0,colon),value=raw.substr(colon+1);
 value.erase(std::remove(value.begin(),value.end(),'_'),value.end());need(!value.empty(),"empty constant payload");
 for(char& c:value)if(c>='A'&&c<='Z')c+=32;
 std::string out;
 if(tag=="INT"||tag=="UINT"||tag=="DEC"){
  if(value=="x"||value=="z")return std::string(width,value[0]);
  bool negative=value[0]=='-';size_t start=negative?1:0;need(start<value.size(),"invalid decimal constant");U number=0;
  for(size_t i=start;i<value.size();++i){need(value[i]>='0'&&value[i]<='9',"invalid decimal digit");unsigned digit=value[i]-'0';need(number<=(~U(0)-digit)/10,"decimal constant exceeds256bits");number=number*10+digit;}
  if(plain_decimal)need(!negative&&number<=2147483647,"large unsized decimal requires an explicit sized literal");
  if(negative)number=~number+1;
  for(int bit=width-1;bit>=0;--bit)out+=(number&(U(1)<<bit))?'1':'0';
  return out;
 }
 int group=tag=="BIN"||tag=="SCAL"?1:tag=="OCT"?3:tag=="HEX"?4:0;need(group,"unsupported constant payload tag");
 for(char c:value){if(c=='x'||c=='z')out+=std::string(group,c);else{int digit=c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:-1;need(digit>=0&&digit<(1<<group),"invalid based constant digit");for(int bit=group-1;bit>=0;--bit)out+=digit&(1<<bit)?'1':'0';}}
 if(out.size()<size_t(width))out=std::string(width-out.size(),out[0]=='x'||out[0]=='z'?out[0]:'0')+out;
 if(out.size()>size_t(width))out=out.substr(out.size()-width);
 return out;
}
std::string bits(H h){need(type(h)==vpiConstant,"constant required");return constant_bits(static_cast<const UHDM::constant*>(identity(h)),unsized_decimal(h));}
