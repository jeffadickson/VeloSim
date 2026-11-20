struct LimbModel:Model {
 Port data{*this,0},other{*this,1};
 explicit LimbModel(int width,int abi=2):Model("limbs",{{"data",width,vpiInput},{"other",64,vpiInput}},-1,abi){}
 void evaluate()override{}
};
struct LimbCallbackCheck{int width;std::string pattern;vpiHandle other;bool seen=false;};
inline PLI_INT32 limbCallback(p_cb_data data){
 auto& expected=*reinterpret_cast<LimbCallbackCheck*>(data->user_data);expected.seen=true;
 auto vector=data->value->value.vector;
 // A nested VPI value read overwrites the TLS result buffer, but must not
 // overwrite this callback's separately owned eight-word vector buffer.
 s_vpi_value nested{};nested.format=vpiVectorVal;vpi_get_value(expected.other,&nested);check(!vpi_chk_error(nullptr));
 for(int bit=0;bit<expected.width;++bit){char c=expected.pattern[expected.width-1-bit];auto mask=uint32_t(1)<<(bit%32);check(bool(vector[bit/32].aval&mask)==(c=='1'||c=='x'));check(bool(vector[bit/32].bval&mask)==(c=='x'||c=='z'));}
 return 0;
}
inline void external256Tests(){
 bool abiRejected=false;try{LimbModel invalid(256,1);}catch(const std::invalid_argument&){abiRejected=true;}check(abiRejected);
 for(int width:{129,191,192,255,256}){
  LimbModel model(width);model.useVpi();auto h=named("limbs.data"),other=named("limbs.other");check(vpi_get(vpiSize,h)==width);model.other=0;
  std::string pattern;for(int bit=0;bit<width;++bit)pattern+="01xz"[(bit+width)%4];
  LimbCallbackCheck expected{width,pattern,other};s_vpi_value callbackValue{};callbackValue.format=vpiVectorVal;s_cb_data callback{};callback.reason=cbValueChange;callback.cb_rtn=limbCallback;callback.obj=h;callback.value=&callbackValue;callback.user_data=reinterpret_cast<char*>(&expected);auto token=vpi_register_cb(&callback);check(token&&!vpi_chk_error(nullptr));
  s_vpi_vecval vector[8]{};for(int bit=0;bit<width;++bit){char c=pattern[width-1-bit];if(c=='1'||c=='x')vector[bit/32].aval|=uint32_t(1)<<(bit%32);if(c=='x'||c=='z')vector[bit/32].bval|=uint32_t(1)<<(bit%32);}
  s_vpi_value value{};value.format=vpiVectorVal;value.value.vector=vector;vpi_put_value(h,&value,nullptr,vpiNoDelay);check(!vpi_chk_error(nullptr));check(model.data.bits()==pattern);model.eval();check(expected.seen);check(vpi_remove_cb(token));
  value.format=vpiBinStrVal;vpi_get_value(h,&value);check(std::string(value.value.str)==pattern);
  if(width==256){model.data.bits(std::string(256,'1'));value.format=vpiDecStrVal;vpi_get_value(h,&value);check(std::string(value.value.str)=="115792089237316195423570985008687907853269984665640564039457584007913129639935");value.value.str=const_cast<char*>("115792089237316195423570985008687907853269984665640564039457584007913129639936");vpi_put_value(h,&value,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr));check(model.data.bits()==std::string(256,'1'));}
  model.final();
 }
}
