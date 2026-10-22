using NUnit.Framework;
namespace VeloSim.Tests;

public class RtlConcatConformanceTests
{
    private static string Bits(ulong n,int width=64)=>string.Concat(Enumerable.Range(0,width).Reverse().Select(i=>((n>>i)&1)==0?'0':'1'));
    private static RtlSignal[] Ports(int qWidth=8,int rWidth=8)=>[new("clk",1,"input"),new("rst_n",1,"input"),new("enable",1,"input"),new("a",64,"input"),new("i",64,"input"),new("q",qWidth,"output"),new("r",rWidth,"output"),new("snapshot",qWidth,"output")];
    private static List<Dictionary<string,string>> Vectors()=>new[]{0UL,1UL,7UL,8UL,63UL,64UL,2147483647UL,0UL,0UL,3UL}.Select((basis,row)=>new Dictionary<string,string>{["rst_n"]=row==0?"0":"1",["enable"]=row==4?"0":"1",["a"]=row==7?new('x',64):row==8?new('z',64):row==9?string.Concat(Enumerable.Range(0,64).Select(i=>"01xz"[i%4])):Bits(0x8123ABCD0045EF67UL^(ulong)(row*0x12345)),["i"]=row==7?new('x',64):row==8?new('z',64):Bits(basis)}).ToList();
    private static async Task Qualify(int first,string tag,string source,RtlSignal[]? ports=null,List<Dictionary<string,string>>? vectors=null,bool sdk=false)
    {
        await RtlIndexedConformanceTests.Qualify("concat-"+tag,source,ports??Ports(),vectors??Vectors(),sdk:sdk);
        for(int row=0;row<10;++row)TestContext.Progress.WriteLine($"CONCAT_CYCLE {first+row} PASS {tag} {(sdk?"matrix fixture":"source vector")} {row}");
    }
    [Test,Category("RealFifo")]
    public async Task WholeWordCycles21To30()
    {
        const string source="module dut(input logic clk,rst_n,enable,input logic[63:0]a,i,output logic[7:0]q,r,snapshot);always_ff @(posedge clk) begin if(!rst_n)begin q<='0;r<='0;snapshot<='0;end else begin snapshot<=q;if(enable)begin {q,r}<=a;if(i[0]){q,r}<={r,q};end end end endmodule";
        await Qualify(21,"whole-words",source);
    }
}
