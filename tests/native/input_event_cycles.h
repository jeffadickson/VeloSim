void inputEventCycles(){
 const char levels[]="01xz";
 // Independent transition table; rows are old values, columns are new values.
 const bool positive[4][4]={{false,true,true,true},{false,false,false,false},{false,true,false,false},{false,true,false,false}};
 const bool negative[4][4]={{false,false,false,false},{true,false,true,true},{true,false,false,false},{true,false,false,false}};
 for(int iteration=11;iteration<=20;++iteration){
  for(int a=0;a<4;++a)for(int b=0;b<4;++b)if((a*4+b)%10==iteration-11){auto old=parse(std::string(1,levels[a])),now=parse(std::string(1,levels[b]));check(edge_event(old,now,true)==positive[a][b]);check(edge_event(old,now,false)==negative[a][b]);}
  if(iteration==20){bool rejected=false;try{edge_event(parse("00"),parse("1"),true);}catch(const std::invalid_argument&){rejected=true;}check(rejected);}
  std::cout<<"OPEN_ITEM_ITERATION "<<iteration<<" PASS four-state edge table\n";
 }
 check(text(two_state(parse("10xz")))=="1000");
 for(int lower:{0,4,2147483646}){
  check(memory_index(word(lower,0,0,32),lower).v==0);
  check(memory_index(word(Bits(lower)+1,0,0,32),lower).v==1);
  check(memory_index(parse("x"),lower).x!=0);check(memory_index(parse("z"),lower).x!=0);
  if(lower)check(memory_index(word(lower-1,0,0,32),lower).x!=0);
 }
 bool invalidLower=false;try{memory_index(parse("0"),-1);}catch(const std::invalid_argument&){invalidLower=true;}check(invalidLower);
 std::vector<W> one{parse("1010")};check(text(read(one,memory_index(word(Bits(1)<<127,0,0,128),4),4))=="xxxx");
}
