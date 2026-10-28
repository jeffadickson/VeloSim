#:property PublishAot=true
#:property TargetFramework=net10.0
// Independent mathematical oracle for the native SVInt qualification. Width
// reduction is explicitly modulo 2^width; signed division truncates toward zero.
using System.Globalization;
using System.Numerics;

if(args.Length!=1)throw new ArgumentException("Usage: GenerateNumericCorpus <new-output.tsv>");
string path=Path.GetFullPath(args[0]);
if(!Directory.Exists(Path.GetDirectoryName(path)))throw new DirectoryNotFoundException("Output parent must exist.");
using var output=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write));
const int seed=20261027;var random=new Random(seed);int rows=0;
output.WriteLine("# velosim.numeric.oracle.v1 seed="+seed+" runtime="+Environment.Version);
foreach(int width in new[]{1,2,8,31,32,63,64,65,127,128,129,255,256,257,512,1024}){
 var mask=(BigInteger.One<<width)-1;
 BigInteger Draw(){var bytes=new byte[(width+7)/8];random.NextBytes(bytes);return new BigInteger(bytes,true,false)&mask;}
 BigInteger Signed(BigInteger value)=>value.TestBit(width-1)?value-(BigInteger.One<<width):value;
 string Bits(BigInteger value){value&=mask;var chars=new char[width];for(int i=0;i<width;i++)chars[width-1-i]=((value>>i)&1).IsZero?'0':'1';return new string(chars);}
 foreach(bool signed in new[]{false,true})for(int iteration=0;iteration<100;iteration++){
  BigInteger a=iteration switch{0=>0,1=>mask,2=>BigInteger.One<<(width-1),_=>Draw()},b=iteration switch{0=>0,1=>1,2=>mask,_=>Draw()};
  var left=signed?Signed(a):a;var right=signed?Signed(b):b;
  foreach(string op in new[]{"add","sub","mul","div","mod","and","or","xor"}){
   string expected=op is "div" or "mod"&&b.IsZero?new string('x',width):Bits(op switch{"add"=>left+right,"sub"=>left-right,"mul"=>left*right,"div"=>left/right,"mod"=>left%right,"and"=>a&b,"or"=>a|b,_=>a^b});
   output.WriteLine(string.Join('\t',++rows,width,signed?1:0,op,Bits(a),Bits(b),expected));
  }
 }
 // Fixed truth tables are independent of either native arithmetic library.
 const string states="01xz";string[] and={"0000","01xx","0xxx","0xxx"},or={"01xx","1111","x1xx","x1xx"},xor={"01xx","10xx","xxxx","xxxx"};
 for(int a=0;a<4;a++)for(int b=0;b<4;b++)foreach(string op in new[]{"add","and","or","xor"}){
  char expected=op switch{"and"=>and[a][b],"or"=>or[a][b],"xor"=>xor[a][b],_=>a>1||b>1?'x':(a+b)%2==0?'0':'1'};
  // Addition of wide fill values needs a mathematical expected value.
  string result=op=="add"&&a<2&&b<2?Bits((a==1?mask:0)+(b==1?mask:0)):new string(expected,width);
  output.WriteLine(string.Join('\t',++rows,width,0,op,new string(states[a],width),new string(states[b],width),result));
 }
}
Console.WriteLine("numeric oracle rows="+rows.ToString(CultureInfo.InvariantCulture));

static class BigIntegerBits {
 internal static bool TestBit(this BigInteger value,int bit)=>!((value>>bit)&BigInteger.One).IsZero;
}
