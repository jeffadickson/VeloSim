using NUnit.Framework;
namespace VeloSim.Tests;

/// <summary>Independent scalar type metadata and fixed array coordinate qualification.</summary>
[NonParallelizable]
public class RtlTypeArrayEvolutionTests
{
    [TestCase("byte",8,true,true),TestCase("byte unsigned",8,false,true),TestCase("shortint",16,true,true),TestCase("int",32,true,true),TestCase("longint",64,true,true),TestCase("integer",32,true,false),TestCase("time",64,false,false)]
    public Task IntegralBaseTypesPreserveSignAndState(string type,int width,bool signed,bool twoState)
    {
        string source=$"module dut(input logic[{width-1}:0]a,output logic signed[63:0]q);{type} v;always_comb begin v=a;q=v;end endmodule";
        var vectors=Enumerable.Range(0,12).Select(row=>new Dictionary<string,string>{["a"]=string.Concat(Enumerable.Range(0,width).Select(bit=>row<4?"01xz"[row]:"01xz"[(bit+row)%4]))}).ToList();
        return RtlIndexedConformanceTests.Qualify("atom-"+type.Replace(' ','-'),source,[new("a",width,"input"),new("q",64,"output",Signed:true),new("v",width,"internal",Signed:signed,TwoState:twoState)],vectors,sdk:true,selectedProbe:false);
    }

    [Test]
    public Task EnumBaseAndLiteralIdentityMatchOriginalHdl()
    {
        const string source="module dut(input logic[7:0]a,output logic[7:0]q);typedef enum logic[3:0]{IDLE=1,ACTIVE=9}state_t;state_t state;always_comb begin if(a[0])state=ACTIVE;else state=IDLE;q=state;end endmodule";
        var vectors=new[]{"00000000","00000001","xxxxxxxx","zzzzzzzz"}.Select(a=>new Dictionary<string,string>{["a"]=a}).ToList();
        return RtlIndexedConformanceTests.Qualify("enum-base",source,[new("a",8,"input"),new("q",8,"output"),new("state",4,"internal")],vectors,sdk:true,selectedProbe:false);
    }

    [TestCase(false),TestCase(true)]
    public Task SignedEnumBasesPreserveLiteralConversion(bool twoState)
    {
        string basis=twoState?"int":"logic signed[7:0]";int width=twoState?32:8;
        string source=$"module dut(input logic[7:0]a,output logic signed[63:0]q);typedef enum {basis}{{NEG=-2,POS=7}}state_t;state_t state;always_comb begin if(a[0])state=NEG;else state=POS;q=state;end endmodule";
        var vectors=new[]{"00000000","00000001","xxxxxxxx","zzzzzzzz"}.Select(a=>new Dictionary<string,string>{["a"]=a}).ToList();
        return RtlIndexedConformanceTests.Qualify("enum-signed-"+twoState,source,[new("a",8,"input"),new("q",64,"output",Signed:true),new("state",width,"internal",Signed:true,TwoState:twoState)],vectors,sdk:true,selectedProbe:false);
    }

    [TestCase(false,false),TestCase(true,false),TestCase(false,true),TestCase(true,true)]
    public Task MultidimensionalCellsPreserveBoundsAndState(bool descending,bool twoState)
    {
        string bounds=descending?"[2:1][5:3]":"[1:2][3:5]";
        string source=$"module dut(input logic clk,input logic[3:0]r,c,input logic[7:0]a,output logic[7:0]q);{(twoState?"bit":"logic")}[7:0]m{bounds};initial begin m[1][3]=8'h12;m[2][5]=8'h34;end always @(posedge clk)begin q<=m[r][c];m[r][c]<=a;end endmodule";
        var vectors=new List<Dictionary<string,string>>();
        foreach(var pair in new[]{("0001","0011"),("0010","0101"),("0001","0011"),("0001","0100"),("0000","0110"),("0010","0010"),("xxxx","0011"),("0001","zzzz"),("0010","0101")})vectors.Add(new(){["r"]=pair.Item1,["c"]=pair.Item2,["a"]=vectors.Count%2==0?"10100101":"xxzz0011"});
        RtlDimension[] dimensions=descending?[new(2,1),new(5,3)]:[new(1,2),new(3,5)];
        return RtlIndexedConformanceTests.Qualify("multidim-"+descending+"-"+twoState,source,[new("clk",1,"input"),new("r",4,"input"),new("c",4,"input"),new("a",8,"input"),new("q",8,"output")],vectors,sdk:true,selectedProbe:false,expectedMemories:[new("m",8,6,Dimensions:dimensions,TwoState:twoState)]);
    }

    [TestCase(0),TestCase(1),TestCase(2),TestCase(3),TestCase(4),TestCase(5)]
    public Task PackedMemberLayoutsPreserveOffsetsSignednessAndOverlays(int shape)
    {
        bool signed=shape==1,twoState=shape==4,clocked=shape==5;int width=shape is 0 or 4?12:16,outputWidth=signed?64:8;
        string declaration=shape switch{
            0=>"typedef struct packed{logic[3:0]hi;logic[7:0]lo;}packet_t;",
            1=>"typedef struct packed{logic signed[7:0]hi;logic[7:0]lo;}packet_t;",
            2=>"typedef struct packed{logic[2:0]x;logic[4:0]y;}inner_t;typedef struct packed{logic[7:0]top;inner_t low;}packet_t;",
            3=>"typedef union packed{logic[15:0]raw;struct packed{logic[7:0]hi;logic[7:0]lo;}parts;}packet_t;",
            4=>"typedef struct packed{bit[3:0]hi;bit[7:0]lo;}packet_t;",
            _=>"typedef struct packed{logic[7:0]hi;logic[7:0]lo;}packet_t;"};
        string hi=shape==2?"p.low.x":shape==3?"p.parts.hi":"p.hi",lo=shape==2?"p.low.y":shape==3?"p.parts.lo":"p.lo";
        string process=clocked?"initial p=16'h1234;always @(posedge clk)begin p.hi<=a[15:8];p.lo<=a[7:0];q<=p.hi;r<=p.lo;end ":$"always_comb begin p=a;q={hi};r={lo};end ";
        string source=$"module dut({(clocked?"input logic clk,":"")}input logic[{width-1}:0]a,output logic {(signed?"signed":"")}[{outputWidth-1}:0]q,r);{declaration}packet_t p;{process}endmodule";
        var expected=new List<RtlSignal>{new("a",width,"input"),new("q",outputWidth,"output",Signed:signed),new("r",outputWidth,"output",Signed:signed),new("p",width,"internal",TwoState:twoState)};if(clocked)expected.Add(new("clk",1,"input"));
        var vectors=Enumerable.Range(0,12).Select(row=>new Dictionary<string,string>{["a"]=string.Concat(Enumerable.Range(0,width).Select(bit=>row<4?"01xz"[row]:"01xz"[(bit+row)%4]))}).ToList();
        return RtlIndexedConformanceTests.Qualify("packed-"+shape,source,expected.ToArray(),vectors,sdk:true,selectedProbe:false);
    }

    [Test]
    public Task OneDimensionalTwoStateMemoryCoercesWritesAndStartsAtZero()
    {
        const string source="module dut(input logic clk,input logic[1:0]i,input logic[7:0]a,output logic[7:0]q);bit[7:0]m[2:3];always @(posedge clk)begin m[i]<=a;q<=m[i];end endmodule";
        string[] payloads=["xz1001xz","11001100","xxxxxxxx","zzzzzzzz","00110011","11110000"];
        string[] observed=["00000000","00000000","00100100","11001100","00000000","00000000"];
        var vectors=payloads.Select((a,row)=>new Dictionary<string,string>{["a"]=a,["i"]=row%2==0?"10":"11"}).ToList();
        var expected=observed.Select(q=>new Dictionary<string,string>{["q"]=q}).ToList();
        return RtlIndexedConformanceTests.Qualify("bit-memory-rank1",source,[new("clk",1,"input"),new("i",2,"input"),new("a",8,"input"),new("q",8,"output")],vectors,sdk:true,selectedProbe:false,expectedOutputs:expected,expectedMemories:[new("m",8,2,LowerBound:2,TwoState:true)]);
    }
    [TestCase(3),TestCase(4)]
    public Task HigherRanksInitializeAndAddressEveryCell(int rank)
    {
        bool four=rank==4;string tail=four?"[4:5]":"",selection=four?"[l]":"",read=four?"[u]":"";
        string source=$"module dut(input logic clk,input logic[3:0]r,c,t{(four?",u":"")},output logic[7:0]q);logic[31:0]i,j,k{(four?",l":"")};logic[7:0]m[1:2][2:3][0:1]{tail};initial begin for(i=1;i<=2;i++)for(j=2;j<=3;j++)for(k=0;k<=1;k++){(four?"for(l=4;l<=5;l++)":"")}m[i][j][k]{selection}=(i<<4)+(j<<2)+(k<<1){(four?"+l":"")};end always @(posedge clk)q<=m[r][c][t]{read};endmodule";
        var signals=new List<RtlSignal>{new("clk",1,"input"),new("r",4,"input"),new("c",4,"input"),new("t",4,"input"),new("q",8,"output"),new("i",32,"internal"),new("j",32,"internal"),new("k",32,"internal")};if(four){signals.Add(new("u",4,"input"));signals.Add(new("l",32,"internal"));}
        var vectors=new List<Dictionary<string,string>>();var expected=new List<Dictionary<string,string>>();
        void Add(int r,int c,int t,int u){vectors.Add(new(){["r"]=Convert.ToString(r,2).PadLeft(4,'0'),["c"]=Convert.ToString(c,2).PadLeft(4,'0'),["t"]=Convert.ToString(t,2).PadLeft(4,'0'),["u"]=Convert.ToString(u,2).PadLeft(4,'0')});bool valid=r is 1 or 2&&c is 2 or 3&&t is 0 or 1&&(!four||u is 4 or 5);expected.Add(new(){["q"]=valid?Convert.ToString((r<<4)+(c<<2)+(t<<1)+(four?u:0),2).PadLeft(8,'0'):"xxxxxxxx"});}
        for(int r=1;r<=2;r++)for(int c=2;c<=3;c++)for(int t=0;t<=1;t++)for(int u=4;u<=(four?5:4);u++)Add(r,c,t,u);
        // Icarus aliases some invalid inner coordinates at rank 3/4; the native guard suite independently checks those cases.
        var dimensions=new List<RtlDimension>{new(1,2),new(2,3),new(0,1)};if(four)dimensions.Add(new(4,5));
        // Check invalid coordinates independently: the reference tool aliases some
        // inner out-of-range coordinates at these ranks instead of returning X.
        string probe="";
        for(int dimension=0;dimension<rank;++dimension){
            int[] coordinates=[1,2,0,4];coordinates[dimension]=new[]{3,4,2,6}[dimension];
            probe+=$"probe.r={coordinates[0]};probe.c={coordinates[1]};probe.t={coordinates[2]};"+(four?$"probe.u={coordinates[3]};":"")+"probe.clk=0;probe.eval();probe.clk=1;probe.eval();if(probe.q.bits()!=\"xxxxxxxx\")return 75;";
        }
        return RtlIndexedConformanceTests.Qualify("rank-"+rank,source,signals.ToArray(),vectors,sdk:true,selectedProbe:false,sdkProbe:probe,expectedOutputs:expected,expectedMemories:[new("m",8,1<<rank,Dimensions:dimensions)]);
    }
}
