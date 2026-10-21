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
inline void runCycles(){identityCycles();metadataCycles();hexReadCycles();octReadCycles();}



