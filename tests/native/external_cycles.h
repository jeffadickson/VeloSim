#pragma once
// Each numbered scenario is one bounded implementation/qualification iteration.
template<class F> void cycle(int id,const char* label,F test){try{test();std::cout<<"CYCLE "<<id<<" PASS "<<label<<'\n';}catch(...){std::cerr<<"CYCLE "<<id<<" FAIL "<<label<<'\n';throw;}}
inline void identityCycles(){
 TestModel m,other;m.useVpi();auto a=named("test.data"),b=named("TOP.test.data"),q=named("test.q"),module=named("test");
 cycle(1,"same token",[&]{check(vpi_compare_objects(a,a)==1);});
 cycle(2,"lookup aliases",[&]{check(vpi_compare_objects(a,b)==1);});
 cycle(3,"different signals",[&]{check(!vpi_compare_objects(a,q)&&!vpi_chk_error(nullptr));});
 cycle(4,"module aliases",[&]{check(vpi_compare_objects(module,named("TOP.test")));});
 cycle(5,"cross-model identity",[&]{other.useVpi();check(!vpi_compare_objects(a,named("test.data")));m.useVpi();});
 cycle(6,"port versus word",[&]{auto it=vpi_iterate(vpiPort,module);auto p=vpi_scan(it);check(!vpi_compare_objects(p,named("test.clk")));vpi_free_object(it);});
 cycle(7,"distinct iterators",[&]{auto x=vpi_iterate(vpiReg,module),y=vpi_iterate(vpiReg,module);check(!vpi_compare_objects(x,y));});
 cycle(8,"null rejected",[&]{check(!vpi_compare_objects(nullptr,a)&&vpi_chk_error(nullptr));});
 cycle(9,"released rejected",[&]{auto h=named("test.q");vpi_free_object(h);check(!vpi_compare_objects(h,q)&&vpi_chk_error(nullptr));});
 cycle(10,"expired rejected",[&]{vpiHandle h;{TestModel gone;gone.useVpi();h=named("test.q");}check(!vpi_compare_objects(h,q)&&vpi_chk_error(nullptr));m.useVpi();});
}
inline void metadataCycles(){
 TestModel m;m.useVpi();auto clk=named("test.clk"),data=named("test.data"),module=named("test");
 cycle(11,"scalar clock",[&]{check(vpi_get(vpiScalar,clk)==1);});
 cycle(12,"clock not vector",[&]{check(vpi_get(vpiVector,clk)==0&&!vpi_chk_error(nullptr));});
 cycle(13,"packed vector",[&]{check(vpi_get(vpiVector,data)==1);});
 cycle(14,"packed not scalar",[&]{check(vpi_get(vpiScalar,data)==0&&!vpi_chk_error(nullptr));});
 cycle(15,"unsigned signal",[&]{check(vpi_get(vpiSigned,data)==0&&!vpi_chk_error(nullptr));});
 cycle(16,"top module",[&]{check(vpi_get(vpiTopModule,module)==1);});
 cycle(17,"word scope",[&]{check(vpi_compare_objects(module,vpi_handle(vpiScope,data)));});
 cycle(18,"word module",[&]{check(vpi_compare_objects(module,vpi_handle(vpiModule,data)));});
 cycle(19,"port scope",[&]{auto it=vpi_iterate(vpiPort,module);check(vpi_compare_objects(module,vpi_handle(vpiScope,vpi_scan(it))));});
 cycle(20,"top has no enclosing scope",[&]{check(!vpi_handle(vpiScope,module)&&!vpi_chk_error(nullptr));});
}
struct WidthModel:Model {WidthModel(int width):Model("width",{{"clk",1,vpiInput},{"data",width,vpiInput}},0){}void evaluate()override{}};
inline void readCase(int id,const char* label,int width,const char* bits,int format,const char* expected){cycle(id,label,[&]{WidthModel m(width);m.useVpi();m.put(1,parse(bits));s_vpi_value v{};v.format=format;vpi_get_value(named("width.data"),&v);check(!vpi_chk_error(nullptr)&&std::string(v.value.str)==expected);});}
inline void hexReadCycles(){
 readCase(21,"hex zero",4,"0000",vpiHexStrVal,"0");
 readCase(22,"hex known",8,"10101111",vpiHexStrVal,"af");
 readCase(23,"hex64 max",64,"1111111111111111111111111111111111111111111111111111111111111111",vpiHexStrVal,"ffffffffffffffff");
 readCase(24,"hex all X",4,"xxxx",vpiHexStrVal,"x");
 readCase(25,"hex all Z",4,"zzzz",vpiHexStrVal,"z");
 readCase(26,"hex mixed X known",4,"x001",vpiHexStrVal,"X");
 readCase(27,"hex mixed Z known",4,"z001",vpiHexStrVal,"Z");
 readCase(28,"hex mixed X Z",4,"xxzz",vpiHexStrVal,"X");
 readCase(29,"hex partial top Z",5,"zzzzz",vpiHexStrVal,"zz");
 readCase(30,"hex scalar Z",1,"z",vpiHexStrVal,"z");
}
inline void octReadCycles(){
 readCase(31,"oct zero",3,"000",vpiOctStrVal,"0");
 readCase(32,"oct known",6,"101111",vpiOctStrVal,"57");
 readCase(33,"oct64 max",64,"1111111111111111111111111111111111111111111111111111111111111111",vpiOctStrVal,"1777777777777777777777");
 readCase(34,"oct all X",3,"xxx",vpiOctStrVal,"x");
 readCase(35,"oct all Z",3,"zzz",vpiOctStrVal,"z");
 readCase(36,"oct mixed X known",3,"x01",vpiOctStrVal,"X");
 readCase(37,"oct mixed Z known",3,"z01",vpiOctStrVal,"Z");
 readCase(38,"oct mixed X Z",3,"xxz",vpiOctStrVal,"X");
 readCase(39,"oct partial top X",5,"xxxxx",vpiOctStrVal,"xx");
 readCase(40,"oct partial top known",4,"1001",vpiOctStrVal,"11");
}
inline void decReadCycles(){
 readCase(41,"decimal zero",4,"0000",vpiDecStrVal,"0");
 readCase(42,"decimal unsigned sign bit",8,"10000000",vpiDecStrVal,"128");
 readCase(43,"decimal64 max",64,"1111111111111111111111111111111111111111111111111111111111111111",vpiDecStrVal,"18446744073709551615");
 readCase(44,"decimal all X",4,"xxxx",vpiDecStrVal,"x");
 readCase(45,"decimal all Z",4,"zzzz",vpiDecStrVal,"z");
 readCase(46,"decimal mixed X known",4,"x001",vpiDecStrVal,"X");
 readCase(47,"decimal mixed Z known",4,"z001",vpiDecStrVal,"Z");
 readCase(48,"decimal mixed X Z",4,"xxzz",vpiDecStrVal,"X");
 readCase(49,"decimal32 boundary",33,"100000000000000000000000000000000",vpiDecStrVal,"4294967296");
 readCase(50,"decimal scalar one",1,"1",vpiDecStrVal,"1");
}
inline void putCase(int id,const char* label,int width,int format,const char* input,const char* expected){cycle(id,label,[&]{WidthModel m(width);m.useVpi();m.put(1,word(42,0,0,width));auto before=m.get(1);s_vpi_value value{};value.format=format;value.value.str=const_cast<char*>(input);vpi_put_value(named("width.data"),&value,nullptr,vpiNoDelay);if(expected)check(!vpi_chk_error(nullptr)&&text(m.get(1))==expected);else check(vpi_chk_error(nullptr)&&exact(before,m.get(1)));});}
inline void hexPutCycles(){
 putCase(51,"hex short zero extends",8,vpiHexStrVal,"a","00001010");
 putCase(52,"hex uppercase",8,vpiHexStrVal,"AF","10101111");
 putCase(53,"hex leading X extends",8,vpiHexStrVal,"x","xxxxxxxx");
 putCase(54,"hex leading Z extends",8,vpiHexStrVal,"z1","zzzz0001");
 putCase(55,"hex trailing X zero prefix",12,vpiHexStrVal,"1x","00000001xxxx");
 putCase(56,"hex truncates to port",5,vpiHexStrVal,"ff","11111");
 putCase(57,"hex invalid atomic",8,vpiHexStrVal,"1g",nullptr);
 putCase(58,"hex empty atomic",8,vpiHexStrVal,"",nullptr);
 putCase(59,"hex null atomic",8,vpiHexStrVal,nullptr,nullptr);
 putCase(60,"hex budget atomic",64,vpiHexStrVal,"100000000000000000000000000000000",nullptr);
}
inline void octPutCycles(){
 putCase(61,"oct short zero extends",8,vpiOctStrVal,"7","00000111");
 putCase(62,"oct known groups",8,vpiOctStrVal,"257","10101111");
 putCase(63,"oct leading X extends",8,vpiOctStrVal,"X1","xxxxx001");
 putCase(64,"oct leading Z extends",8,vpiOctStrVal,"Z1","zzzzz001");
 putCase(65,"oct trailing Z zero prefix",8,vpiOctStrVal,"1z","00001zzz");
 putCase(66,"oct truncates to port",5,vpiOctStrVal,"77","11111");
 putCase(67,"oct invalid digit atomic",8,vpiOctStrVal,"18",nullptr);
 putCase(68,"oct negative atomic",8,vpiOctStrVal,"-1",nullptr);
 putCase(69,"oct64 max",64,vpiOctStrVal,"1777777777777777777777","1111111111111111111111111111111111111111111111111111111111111111");
 putCase(70,"oct budget atomic",64,vpiOctStrVal,"10000000000000000000000000000000000000000000",nullptr);
}
inline void decPutCycles(){
 putCase(71,"decimal zero",8,vpiDecStrVal,"0","00000000");
 putCase(72,"decimal64 max",64,vpiDecStrVal,"18446744073709551615","1111111111111111111111111111111111111111111111111111111111111111");
 putCase(73,"decimal port truncation",8,vpiDecStrVal,"256","00000000");
 putCase(74,"decimal X extension",8,vpiDecStrVal,"X","xxxxxxxx");
 putCase(75,"decimal Z extension",8,vpiDecStrVal,"z","zzzzzzzz");
 putCase(76,"decimal overflow atomic",64,vpiDecStrVal,"340282366920938463463374607431768211456",nullptr);
 putCase(77,"decimal negative atomic",8,vpiDecStrVal,"-1",nullptr);
 putCase(78,"decimal suffix atomic",8,vpiDecStrVal,"12x",nullptr);
 putCase(79,"decimal empty atomic",8,vpiDecStrVal,"",nullptr);
 cycle(80,"decimal budget null and whitespace atomic",[]{WidthModel m(8);m.useVpi();m.put(1,word(42,0,0,8));auto h=named("width.data");for(auto input:{static_cast<const char*>(nullptr),"1000000000000000000000000000000000000000"," 1","+1","1_0"}){s_vpi_value v{};v.format=vpiDecStrVal;v.value.str=const_cast<char*>(input);vpi_put_value(h,&v,nullptr,vpiNoDelay);check(vpi_chk_error(nullptr)&&m.get(1).v==42);}});
}
template<class F> inline void mustThrow(F f){bool rejected=false;try{f();}catch(const std::runtime_error&){rejected=true;}check(rejected);}
inline TestModel* lifecycleModel=nullptr;
inline int lifecycleEnds=0;
inline PLI_INT32 lifecycleEnd(p_cb_data){++lifecycleEnds;return 0;}
inline PLI_INT32 requestAnotherEval(p_cb_data){lifecycleModel->requestEval();return 0;}
inline PLI_INT32 rejectCallbackTime(p_cb_data){auto before=lifecycleModel->time();mustThrow([]{lifecycleModel->timeInc(1);});check(lifecycleModel->time()==before);return 0;}
inline void lifecycleCycles(){
 cycle(81,"finish has no pending eval",[]{TestModel m;m.finish();check(m.gotFinish()&&!m.evalNeeded());});
 cycle(82,"finish rejects time advance",[]{TestModel m;m.finish();mustThrow([&]{m.timeInc(1);});check(m.time()==0);});
 cycle(83,"finish rejects plugin load",[]{TestModel m;m.finish();mustThrow([&]{m.loadVpi("/missing.so");});});
 cycle(84,"finish rejects eval request",[]{TestModel m;m.finish();mustThrow([&]{m.requestEval();});});
 cycle(85,"final has no pending eval",[]{TestModel m;m.data=1;m.final();check(!m.evalNeeded());});
 cycle(86,"final rejects time advance",[]{TestModel m;m.final();mustThrow([&]{m.timeInc(0);});});
 cycle(87,"final callback exactly once",[]{TestModel m;m.useVpi();s_cb_data cb{};cb.reason=cbEndOfSimulation;cb.cb_rtn=lifecycleEnd;check(vpi_register_cb(&cb));m.finish();m.final();m.final();check(lifecycleEnds==1);});
 cycle(88,"unknown clock recovery",[]{TestModel m;m.clk.bits("x");mustThrow([&]{m.eval();});check(m.evalNeeded());m.clk=0;m.eval();check(!m.evalNeeded());});
 cycle(89,"callback eval request survives",[]{TestModel m;lifecycleModel=&m;m.useVpi();s_cb_data cb{};cb.reason=cbValueChange;cb.obj=named("test.q");cb.cb_rtn=requestAnotherEval;check(vpi_register_cb(&cb));m.data=1;m.clk=1;m.eval();check(m.evalNeeded());m.eval();check(!m.evalNeeded());});
 cycle(90,"callback cannot advance time",[]{TestModel m;lifecycleModel=&m;m.useVpi();s_cb_data cb{};cb.reason=cbReadOnlySynch;cb.cb_rtn=rejectCallbackTime;check(vpi_register_cb(&cb));m.eval();});
}
inline std::string observedCallback;
inline s_vpi_vecval observedVector[2]{};
inline s_vpi_time observedTime{};
inline int observedScalar=-1;
inline PLI_INT32 captureFormat(p_cb_data cb){check(cb->value);switch(cb->value->format){case vpiBinStrVal:case vpiOctStrVal:case vpiDecStrVal:case vpiHexStrVal:observedCallback=cb->value->value.str;break;case vpiScalarVal:observedScalar=cb->value->value.scalar;break;case vpiVectorVal:observedVector[0]=cb->value->value.vector[0];observedVector[1]=cb->value->value.vector[1];break;default:break;}if(cb->time)observedTime=*cb->time;return 0;}
inline void callbackCase(int id,const char* label,int width,const char* bits,int format,const char* expected){cycle(id,label,[&]{WidthModel m(width);m.useVpi();s_vpi_value v{};v.format=format;s_cb_data cb{};cb.reason=cbValueChange;cb.obj=named("width.data");cb.cb_rtn=captureFormat;cb.value=&v;check(vpi_register_cb(&cb));observedCallback.clear();m.put(1,parse(bits));m.eval();check(observedCallback==expected);});}
inline void callbackCycles(){
 callbackCase(91,"hex callback payload",8,"10101111",vpiHexStrVal,"af");
 callbackCase(92,"oct callback payload",6,"101111",vpiOctStrVal,"57");
 callbackCase(93,"decimal64 callback",64,"1111111111111111111111111111111111111111111111111111111111111111",vpiDecStrVal,"18446744073709551615");
 callbackCase(94,"hex mixed callback",4,"zz01",vpiHexStrVal,"Z");
 callbackCase(95,"oct partial callback",5,"zzzzz",vpiOctStrVal,"zz");
 callbackCase(96,"decimal unknown callback",4,"x001",vpiDecStrVal,"X");
 cycle(97,"reject unusable scalar callback at registration",[]{WidthModel m(64);m.useVpi();s_vpi_value v{};v.format=vpiScalarVal;s_cb_data cb{};cb.reason=cbValueChange;cb.obj=named("width.data");cb.cb_rtn=captureFormat;cb.value=&v;check(!vpi_register_cb(&cb)&&vpi_chk_error(nullptr));m.put(1,word(1,0,0,64));m.eval();});
 cycle(98,"scalar Z callback",[]{WidthModel m(1);m.useVpi();s_vpi_value v{};v.format=vpiScalarVal;s_cb_data cb{};cb.reason=cbValueChange;cb.obj=named("width.data");cb.cb_rtn=captureFormat;cb.value=&v;check(vpi_register_cb(&cb));m.put(1,parse("z"));m.eval();check(observedScalar==vpiZ);});
 cycle(99,"33bit vector callback",[]{WidthModel m(33);m.useVpi();s_vpi_value v{};v.format=vpiVectorVal;s_cb_data cb{};cb.reason=cbValueChange;cb.obj=named("width.data");cb.cb_rtn=captureFormat;cb.value=&v;check(vpi_register_cb(&cb));m.put(1,word(1,1ull<<32,1ull<<31,33));m.eval();check(observedVector[0].aval==1&&observedVector[0].bval==0x80000000&&observedVector[1].aval==1&&observedVector[1].bval==1);});
 cycle(100,"64bit callback time",[]{WidthModel m(1);m.useVpi();s_vpi_value v{};v.format=vpiSuppressVal;s_vpi_time t{vpiSimTime,0,0,0};s_cb_data cb{};cb.reason=cbValueChange;cb.obj=named("width.data");cb.cb_rtn=captureFormat;cb.value=&v;cb.time=&t;check(vpi_register_cb(&cb));m.timeInc((1ull<<32)+17);m.put(1,word(1,0,0,1));m.eval();check(observedTime.high==1&&observedTime.low==17);});
}
inline void runCycles(){identityCycles();metadataCycles();hexReadCycles();octReadCycles();decReadCycles();hexPutCycles();octPutCycles();decPutCycles();lifecycleCycles();callbackCycles();}
