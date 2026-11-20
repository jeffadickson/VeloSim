// Independent string expectations exercise every newly exposed limb boundary.
inline void evolutionRuntimeTests(){
 for(int width:{129,191,192,255,256}){
  auto maximum=parse(std::string(width,'1')),one=word(1,0,0,width),zero=word(0,0,0,width);
  check(text(arithmetic(maximum,one,width,false))==std::string(width,'0'));
  check(text(arithmetic(zero,one,width,true))==std::string(width,'1'));
  check(text(product(maximum,maximum,width,0))==std::string(width-1,'0')+"1");
  auto divisor=parse("1"+std::string(width-2,'0')+"1");
  check(text(product(maximum,divisor,width,1))==std::string(width-1,'0')+"1");
  check(text(product(maximum,divisor,width,2))=="0"+std::string(width-2,'1')+"0");
  auto minimum=parse("1"+std::string(width-1,'0'));
  check(text(signed_product(minimum,maximum,width,1))==text(minimum));
  check(text(word(Bits(-1),0,0,width))==std::string(width,'1'));
  std::string pattern;for(int bit=0;bit<width;++bit)pattern+="01xz"[(bit+width)%4];auto value=parse(pattern);
  for(int bit=0;bit<=width;++bit)check(text(select_bit(value,word(bit,0,0,32)))==(bit==width?"x":std::string(1,pattern[width-1-bit])));
  for(int base:{63,64,127,128,191,192,255}){auto expected=pattern;for(int bit=0;bit<5&&base+bit<width;++bit)expected[width-1-base-bit]='1';check(text(deposit_indexed(value,word(base,0,0,32),5,false,parse("11111")))==expected);}
  check(text(shift(value,word(Bits(1)<<200,0,0,256),width,false))==std::string(width,'0'));
 }
 bool zeroRejected=false;try{(void)Bits::divide(Bits(1),Bits(0));}catch(const std::domain_error&){zeroRejected=true;}check(zeroRejected);
 size_t remaining=2;execution_step(remaining);execution_step(remaining);bool exhausted=false;try{execution_step(remaining);}catch(const std::runtime_error&){exhausted=true;}check(exhausted&&remaining==0);
 check(repeat_count(parse("x"),false)==0&&repeat_count(parse("z"),false)==0&&repeat_count(parse("11111111"),true)==0&&repeat_count(parse("11111111"),false)==255);
 bool excessive=false;try{repeat_count(word(Bits(1)<<128,0,0,256),false);}catch(const std::runtime_error&){excessive=true;}check(excessive);
 for(int rank:{2,3,4}){
  std::vector<std::pair<int,int>> shape(rank,{2,1});unsigned cells=1u<<rank;
  for(unsigned cell=0;cell<cells;++cell){std::vector<W> indices;for(int d=0;d<rank;++d)indices.push_back(word(1+((cell>>(rank-1-d))&1),0,0,32));check(array_index(indices,shape).v==Bits(cell));}
  std::vector<W> indices(rank,word(1,0,0,32));for(int d=0;d<rank;++d){auto invalid=indices;invalid[d]=word(0,0,0,32);check(bool(array_index(invalid,shape).x));invalid[d]=word(3,0,0,32);check(bool(array_index(invalid,shape).x));invalid[d]=word(Bits(1)<<200,0,0,256);check(bool(array_index(invalid,shape).x));invalid[d]=parse("x");check(bool(array_index(invalid,shape).x));invalid[d]=parse("z");check(bool(array_index(invalid,shape).x));}
 }
 check(bool(array_index({word(0,0,0,32),word(6,0,0,32)},{{1,2},{3,5}}).x));
 check(array_index({word(INT32_MAX,0,0,32),word(1,0,0,32)},{{INT32_MAX,INT32_MAX-1},{0,1}}).v==3);
 for(int shape=0;shape<6;++shape){bool rejected=false;try{if(shape==0)array_index({},{});else if(shape==1)array_index({word(0,0,0,1)},{});else if(shape==2)array_index(std::vector<W>(5,word(0,0,0,1)),std::vector<std::pair<int,int>>(5,{0,0}));else if(shape==3)array_index({word(0,0,0,1)},{{-1,0}});else if(shape==4)array_index({word(0,0,0,1)},{{0,-1}});else array_index({word(0,0,0,1)},{{0,256}});}catch(const std::invalid_argument&){rejected=true;}check(rejected);}
}
