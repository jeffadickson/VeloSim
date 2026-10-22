using NUnit.Framework;
namespace VeloSim.Tests;

public class RtlSignedWideTests
{
    [TestCase("bitselect"),TestCase("indexedselect"),TestCase("index")]
    public void SignedIndicesFailClosed(string kind)
    {
        var node=System.Text.Json.Nodes.JsonNode.Parse(RtlConcatTargetTests.Fixture(new{kind="ref",name="q"}).ToJsonString())!;
        node["signals"]![2]!["signed"]=true;
        node["memories"]=System.Text.Json.Nodes.JsonNode.Parse("[{\"name\":\"m\",\"width\":8,\"depth\":16}]");
        string expression=kind=="index"?"{\"kind\":\"index\",\"name\":\"m\",\"index\":{\"kind\":\"ref\",\"name\":\"i\"}}":kind=="bitselect"?"{\"kind\":\"bitselect\",\"value\":{\"kind\":\"ref\",\"name\":\"a\"},\"index\":{\"kind\":\"ref\",\"name\":\"i\"}}":"{\"kind\":\"indexedselect\",\"value\":{\"kind\":\"ref\",\"name\":\"a\"},\"base\":{\"kind\":\"ref\",\"name\":\"i\"},\"width\":4,\"descending\":false}";
        node["body"]!["items"]![1]!["value"]=System.Text.Json.Nodes.JsonNode.Parse(expression);
        Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()))!.Message,Does.Contain("Signed indices"));
    }
    [TestCase(8),TestCase(65),TestCase(128),Category("RealFifo")]
    public async Task SignedArithmeticMatchesSameSourceIcarus(int width)
    {
        var maximum=(System.Numerics.BigInteger.One<<width)-1;string decimalValue=maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),hexValue=maximum.ToString("x");string octalValue="";for(var n=maximum;n>0;n>>=3)octalValue=(char)('0'+(int)(n&7))+octalValue;
        string source=$"module dut(input logic clk,input logic signed[{width-1}:0]a,b,input logic[7:0]i,input logic mode,output logic signed[{width-1}:0]sum,difference,multiply,divide,remainder,shifted,choice,casted,history,output logic[{width-1}:0]decimal_value,hex_value,octal_value,literal_x,literal_z,literal_shift,output logic lt,mixed,eq,parameter_negative,literal_sizing,decimal_compare);localparam int NEG=-1;logic signed[{width-1}:0]mem[0:1];always_ff @(posedge clk)begin sum<=a+b;difference<=a-b;multiply<=a*b;divide<=a/b;remainder<=a%b;shifted<=a>>>i;choice<=mode?a:b;casted<=$signed(i);lt<=a<b;mixed<=a<i;eq<=a==8'shff;parameter_negative<=NEG<0;literal_sizing<=((1<<40)==0);literal_shift<=(1<<40);decimal_compare<=a<{width}'d{decimalValue};decimal_value<={width}'d{decimalValue};hex_value<={width}'h{hexValue};octal_value<={width}'o{octalValue};literal_x<={width}'hx;literal_z<={width}'bz;mem[0]<=a;mem[1]<=mem[0];history<=mem[1];end endmodule";
        var expected=new List<RtlSignal>{new("clk",1,"input"),new("a",width,"input",Signed:true),new("b",width,"input",Signed:true),new("i",8,"input"),new("mode",1,"input")};
        foreach(string name in new[]{"sum","difference","multiply","divide","remainder","shifted","choice","casted","history"})expected.Add(new(name,width,"output",Signed:true));
        foreach(string name in new[]{"decimal_value","hex_value","octal_value","literal_x","literal_z","literal_shift"})expected.Add(new(name,width,"output"));
        foreach(string name in new[]{"lt","mixed","eq","parameter_negative","literal_sizing","decimal_compare"})expected.Add(new(name,1,"output"));
        uint seed=0x84321;string RandomBits(int n,bool xz){return string.Concat(Enumerable.Range(0,n).Select(_=>{seed^=seed<<13;seed^=seed>>17;seed^=seed<<5;return "01xz"[(int)(seed%(xz?4u:2u))];}));}
        var vectors=new List<Dictionary<string,string>>();
        for(int row=0;row<96;++row){string a=row<4?new string('1',width):row==4?"1"+new string('0',width-1):RandomBits(width,row>=64);string b=row==0?new('0',width):row is 1 or 4?new('1',width):row==2?new string('0',width-1)+"1":RandomBits(width,row>=64);vectors.Add(new(){["a"]=a,["b"]=b,["i"]=Convert.ToString(row%7==0?255:row%7==1?0:row%7==2?width:row%7,2).PadLeft(8,'0'),["mode"]=row%9==0?"x":row%2==0?"0":"1"});}
        await RtlIndexedConformanceTests.Qualify("signed-wide-"+width,source,expected.ToArray(),vectors,sdk:true,selectedProbe:false);
    }
    [Test,Category("RealFifo")]
    public async Task MixedSignedWidthsAndCaseMatchIcarus()
    {
        const string source="module dut(input logic clk,input logic signed[7:0]a,input logic signed[15:0]b,input logic[7:0]u,output logic signed[15:0]add,ext,casted,shifted,output logic cmp,eq,mixed,case_result);always_ff @(posedge clk)begin add<=a+b;ext<=a;casted<=$signed(u);shifted<=a>>>u;cmp<=a<b;eq<=a==b;mixed<=(a+b)<u;case(a)16'shffff:case_result<=1;8'h80:case_result<=0;default:case_result<=1'bx;endcase end endmodule";
        RtlSignal[] expected=[new("clk",1,"input"),new("a",8,"input",Signed:true),new("b",16,"input",Signed:true),new("u",8,"input"),new("add",16,"output",Signed:true),new("ext",16,"output",Signed:true),new("casted",16,"output",Signed:true),new("shifted",16,"output",Signed:true),new("cmp",1,"output"),new("eq",1,"output"),new("mixed",1,"output"),new("case_result",1,"output")];
        var vectors=new List<Dictionary<string,string>>();foreach(string a in new[]{"11111111","10000000","00000001","x0000001","z0000001"})foreach(string b in new[]{"1111111111111111","0000000011111111","1000000000000000","0000000000000001"})foreach(string u in new[]{"00000000","00000001","11111111"})vectors.Add(new(){["a"]=a,["b"]=b,["u"]=u});
        await RtlIndexedConformanceTests.Qualify("signed-mixed",source,expected,vectors,sdk:true,selectedProbe:false);
    }
}
