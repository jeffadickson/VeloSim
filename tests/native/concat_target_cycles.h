#pragma once
inline void concatTargetCycles(){
 const int widths[][2]={{1,1},{1,7},{7,1},{7,8},{8,16},{16,16},{31,33},{32,32},{63,1},{1,63}};
 for(int scenario=0;scenario<10;++scenario){
  const int left=widths[scenario][0],right=widths[scenario][1],total=left+right;
  // Expected values use textual padding/truncation/substrings, independent of masks.
  for(int inputWidth:{1,7,32,64})for(int phase=0;phase<8;++phase){
   std::string input;for(int bit=0;bit<inputWidth;++bit)input+="01xz"[(bit+phase+(bit/3))%4];
   auto expected=inputWidth<total?std::string(total-inputWidth,'0')+input:input.substr(inputWidth-total);
   auto rhs=resize(parse(input),total);
   auto high=select_part(rhs,total-1,right),low=select_part(rhs,right-1,0);
   check(text(high)==expected.substr(0,left));check(text(low)==expected.substr(left));
   check(text(deposit_indexed(parse(std::string(left,'x')),word(0,0,0,64),left,false,high))==expected.substr(0,left));
   check(text(deposit_indexed(parse(std::string(right,'z')),word(0,0,0,64),right,false,low))==expected.substr(left));
  }
  std::cout<<"CONCAT_CYCLE "<<11+scenario<<" PASS independent split oracle "<<left<<'+'<<right<<'\n';
 }
}
