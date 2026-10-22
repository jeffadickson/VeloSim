inline void signedWideTests(){
 for(int a=-128;a<128;++a)for(int b=-128;b<128;++b){
  auto x=word(unsigned(a),0,0,8),y=word(unsigned(b),0,0,8);
  check(signed_compare(x,y,0).v==unsigned(a<b));
  check(signed_compare(x,y,1).v==unsigned(a<=b));
  check(signed_compare(x,y,2).v==unsigned(a>b));
  check(signed_compare(x,y,3).v==unsigned(a>=b));
  for(int op=0;op<3;++op){auto out=signed_product(x,y,8,op);if(op&&b==0)check(out.x==255);else check(out.v==unsigned(op==0?a*b:op==1?a/b:a%b)%256);}
 }
 for(int width:{65,96,127,128}){
  std::string input;for(int bit=0;bit<width;++bit)input+="01xz"[bit%4];auto w=parse(input);check(text(w)==input);
  for(int start:{0,1,63,64,width-1,width})for(int count:{1,64,65,128})for(bool down:{false,true}){
   std::string expected;for(int bit=count-1;bit>=0;--bit){int pos=down?start-(count-1-bit):start+bit;expected+=pos>=0&&pos<width?input[width-1-pos]:'x';}
   check(text(select_indexed(w,word(start,0,0,8),count,down))==expected);
   std::string rhs(count,'1'),deposited=input;for(int bit=0;bit<count;++bit){int pos=down?start-(count-1-bit):start+bit;if(pos>=0&&pos<width)deposited[width-1-pos]='1';}
   check(text(deposit_indexed(w,word(start,0,0,8),count,down,parse(rhs)))==deposited);
  }
  for(char sign:std::string("01xz")){auto a=parse(std::string(1,sign)+"001");check(text(signed_resize(a,width))==std::string(width-4,sign)+text(a));check(text(signed_shift(a,word(width,0,0,8),width))==std::string(width,sign));}
 }
 auto min=word(Bits(1)<<127,0,0,128),minus=word(~Bits(0),0,0,128);
 check(exact(signed_product(min,minus,128,1),min));check(signed_product(min,minus,128,2).v==0);
 auto a=word((Bits(1)<<64)+1,0,0,128);check(product(a,a,128,0).v==(Bits(1)<<65)+1);
 check(reduce(word(Bits(1)<<127,0,0,128),4).v==1);check(reduce(a,4).v==0);
 check(text(arithmetic(min,min,128,false))==std::string(128,'0'));
 check(text(concat(parse(std::string(64,'z')),parse(std::string(64,'x'))))==std::string(64,'z')+std::string(64,'x'));
 check(text(signed_compare(unknown(128),minus,0))=="x");
 check(text(signed_shift(min,unknown(8),128))==std::string(128,'x'));
}
