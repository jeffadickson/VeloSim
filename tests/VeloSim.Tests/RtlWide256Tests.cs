using NUnit.Framework;
using System.Numerics;
namespace VeloSim.Tests;

/// <summary>Independent BigInteger/string oracles at and beyond every new limb boundary.</summary>
[NonParallelizable]
public class RtlWide256Tests
{
    static string Bits(BigInteger value,int width){value&=(BigInteger.One<<width)-1;var chars=new char[width];for(int i=width-1;i>=0;--i){chars[i]=value.IsEven?'0':'1';value>>=1;}return new(chars);}
    static BigInteger Number(string bits){BigInteger value=0;foreach(char bit in bits)value=(value<<1)+(bit=='1'?1:0);return value;}
    [TestCase(129),TestCase(191),TestCase(192),TestCase(255),TestCase(256)]
    public Task WideArithmeticAndSelectionsMatchTwoIndependentOracles(int width)
    {
        var names=new[]{"qadd","qsub","qmul","qdiv","qmod","qx","qleft","qright","qpack"};
        string source=$"module dut(input logic[{width-1}:0]a,b,input logic[8:0]amount,output logic[{width-1}:0]{string.Join(',',names)},output logic parity);assign qadd=a+b;assign qsub=a-b;assign qmul=a*b;assign qdiv=a/b;assign qmod=a%b;assign qx=a^b;assign qleft=a<<amount;assign qright=a>>amount;assign qpack={{a[127:0],b[{width-129}:0]}};assign parity=^a;endmodule";
        var random=new Random(20261028+width);var vectors=new List<Dictionary<string,string>>();var expected=new List<Dictionary<string,string>>();
        int[] shifts=[0,1,63,64,127,128,191,192,255,256,257];
        for(int row=0;row<24;++row){
            var bytes=new byte[(width+7)/8];random.NextBytes(bytes);string a=Bits(new BigInteger(bytes,true),width);random.NextBytes(bytes);string b=Bits(new BigInteger(bytes,true),width);
            if(row==0){a=new('1',width);b=new('0',width);}if(row==1){a=Bits(BigInteger.One<<128,width);b=Bits(1,width);}if(row==2){a=new('1',width);b=Bits((BigInteger.One<<(width-1))+1,width);}if(row==22)a=new('x',width);if(row==23)b=new('z',width);
            int shift=shifts[row%shifts.Length];vectors.Add(new(){["a"]=a,["b"]=b,["amount"]=Bits(shift,9)});
            bool known=a.All(c=>c is '0' or '1')&&b.All(c=>c is '0' or '1');var av=Number(a);var bv=Number(b);string Unknown()=>new('x',width);string Arithmetic(BigInteger value)=>known?Bits(value,width):Unknown();
            expected.Add(new(){["qadd"]=Arithmetic(av+bv),["qsub"]=Arithmetic(av-bv),["qmul"]=Arithmetic(av*bv),["qdiv"]=known&&bv!=0?Bits(av/bv,width):Unknown(),["qmod"]=known&&bv!=0?Bits(av%bv,width):Unknown(),["qx"]=string.Concat(a.Zip(b,(l,r)=>l is 'x' or 'z'||r is 'x' or 'z'?'x':l==r?'0':'1')),["qleft"]=shift>=width?new('0',width):a[shift..]+new string('0',shift),["qright"]=shift>=width?new('0',width):new string('0',shift)+a[..(width-shift)],["qpack"]=a[^128..]+b[^(width-128)..],["parity"]=a.Any(c=>c is 'x' or 'z')?"x":a.Count(c=>c=='1')%2==0?"0":"1"});
        }
        var signals=new List<RtlSignal>{new("a",width,"input"),new("b",width,"input"),new("amount",9,"input"),new("parity",1,"output")};signals.AddRange(names.Select(name=>new RtlSignal(name,width,"output")));
        string high=Bits(BigInteger.One<<128,width);string probe=$"probe.a.bits(\"{high}\");probe.b=0;probe.amount=0;probe.eval();bool overflow=false;try{{(void)static_cast<uint64_t>(probe.qadd);}}catch(const std::overflow_error&){{overflow=true;}}if(!overflow)return 41;";
        return RtlIndexedConformanceTests.Qualify("wide256-"+width,source,signals.ToArray(),vectors,sdk:true,selectedProbe:false,sdkProbe:probe,expectedOutputs:expected);
    }

    [TestCase(129),TestCase(256)]
    public Task SizedDecimalAndHexLiteralsPreserveHighLimbs(int width)
    {
        var maximum=(BigInteger.One<<width)-1;var minimum=BigInteger.One<<(width-1);
        string source=$"module dut(input logic a,output logic[{width-1}:0]q,r);assign q={width}'d{maximum};assign r={width}'h{minimum:x};endmodule";
        var vectors=new List<Dictionary<string,string>>{new(){["a"]="0"},new(){["a"]="1"}};
        var expected=vectors.Select(_=>new Dictionary<string,string>{["q"]=Bits(maximum,width),["r"]=Bits(minimum,width)}).ToList();
        return RtlIndexedConformanceTests.Qualify("wide-literal-"+width,source,[new("a",1,"input"),new("q",width,"output"),new("r",width,"output")],vectors,sdk:true,selectedProbe:false,expectedOutputs:expected);
    }

    [TestCase(129),TestCase(256)]
    public Task SignedWideDivisionAndShiftMatchBigInteger(int width)
    {
        string source=$"module dut(input logic signed[{width-1}:0]a,b,input logic[8:0]amount,output logic signed[{width-1}:0]qdiv,qmod,qshift);assign qdiv=a/b;assign qmod=a%b;assign qshift=a>>>amount;endmodule";
        var minimum=-(BigInteger.One<<(width-1));var pairs=new(BigInteger a,BigInteger b)[]{(minimum,-1),(minimum,1),(-7,3),(7,-3),(-7,-3),(-1,0),(minimum,minimum+1),(-1,1),(minimum+1,-3),(0,-1)};
        var vectors=new List<Dictionary<string,string>>();var expected=new List<Dictionary<string,string>>();int[] shifts=[0,1,63,64,127,128,255,256,257,191];
        for(int row=0;row<pairs.Length;row++){var (a,b)=pairs[row];vectors.Add(new(){["a"]=Bits(a,width),["b"]=Bits(b,width),["amount"]=Bits(shifts[row],9)});expected.Add(new(){["qdiv"]=b==0?new('x',width):Bits(a/b,width),["qmod"]=b==0?new('x',width):Bits(a%b,width),["qshift"]=Bits(a>>shifts[row],width)});}
        return RtlIndexedConformanceTests.Qualify("signed256-"+width,source,[new("a",width,"input",Signed:true),new("b",width,"input",Signed:true),new("amount",9,"input"),new("qdiv",width,"output",Signed:true),new("qmod",width,"output",Signed:true),new("qshift",width,"output",Signed:true)],vectors,sdk:true,selectedProbe:false,expectedOutputs:expected);
    }
}
