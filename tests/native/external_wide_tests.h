struct WideModel:Model {
 Port data{*this,1};
 WideModel(bool sign,bool two=false):Model("wide",{{"clk",1,vpiInput},{"data",128,vpiInput,sign,two}},0){}
 void evaluate() override{}
};
inline void externalWideTests(){
 {
  WideModel bit(false,true);check(bit.data.bits()==std::string(128,'0'));check(bit.signals()[1].two_state);
  bit.data.bits(std::string(64,'x')+std::string(64,'z'));check(bit.data.bits()==std::string(128,'0'));
  bit.useVpi();auto h=named("wide.data");s_vpi_value value{};value.format=vpiBinStrVal;std::string pattern=std::string(124,'1')+"10xz";value.value.str=pattern.data();vpi_put_value(h,&value,nullptr,vpiNoDelay);check(!vpi_chk_error(nullptr));check(bit.data.bits()==std::string(124,'1')+"1000");bit.eval();bit.final();
 }
 for(bool sign:{false,true}){
  WideModel m(sign);m.useVpi();auto h=named("wide.data");check(vpi_get(vpiSigned,h)==sign);check(vpi_get(vpiSize,h)==128);
  s_vpi_value value{};value.format=vpiVectorVal;s_vpi_vecval bits[4]{{1,0},{0,0},{0x80000000,0},{0x80000000,0}};value.value.vector=bits;
  vpi_put_value(h,&value,nullptr,vpiNoDelay);check(!vpi_chk_error(nullptr));vpi_get_value(h,&value);check(!vpi_chk_error(nullptr));for(int i=0;i<4;++i)check(value.value.vector[i].aval==bits[i].aval&&value.value.vector[i].bval==bits[i].bval);
  bool overflow=false;try{(void)uint64_t(m.data);}catch(const std::overflow_error&){overflow=true;}check(overflow);
  m.data.bits(std::string(128,'1'));value.format=vpiDecStrVal;vpi_get_value(h,&value);check(!vpi_chk_error(nullptr));check(std::string(value.value.str)==(sign?"-1":"340282366920938463463374607431768211455"));
  m.data.bits("1"+std::string(127,'0'));vpi_get_value(h,&value);check(!vpi_chk_error(nullptr));check(std::string(value.value.str)==(sign?"-170141183460469231731687303715884105728":"170141183460469231731687303715884105728"));
  value.value.str=const_cast<char*>("340282366920938463463374607431768211455");vpi_put_value(h,&value,nullptr,vpiNoDelay);check(!vpi_chk_error(nullptr));check(m.data.bits()==std::string(128,'1'));
  value.value.str=const_cast<char*>("340282366920938463463374607431768211456");vpi_put_value(h,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));check(m.data.bits()==std::string(128,'1'));
  value.format=vpiHexStrVal;value.value.str=const_cast<char*>("80000000000000010000000000000002");vpi_put_value(h,&value,nullptr,vpiNoDelay);check(!vpi_chk_error(nullptr));vpi_get_value(h,&value);check(std::string(value.value.str)=="80000000000000010000000000000002");
  m.data.bits(std::string(64,'x')+std::string(64,'z'));value.format=vpiVectorVal;vpi_get_value(h,&value);check(!vpi_chk_error(nullptr));for(int i=0;i<4;++i)check(value.value.vector[i].bval==UINT32_MAX&&value.value.vector[i].aval==(i<2?0:UINT32_MAX));
  m.final();
 }
}
