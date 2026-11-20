using System.Text.Json;
using System.Text.Json.Nodes;
using System.Numerics;
using FsCheck;
using FsCheck.Fluent;
using NUnit.Framework;

namespace VeloSim.Tests;

/// <summary>Sparse typed keys, traversal ownership and strict associative IR shape.</summary>
[NonParallelizable]
public class RtlAssociativeTests
{
    private static object Ref(string name)=>new{kind="ref",name};
    private static object Constant(string bits)=>new{kind="const",bits};
    internal static JsonNode Fixture()=>JsonSerializer.SerializeToNode(new{
        format="velosim.rtl.v1",clock="clk",signals=new object[]{new{name="clk",width=1,direction="input"},new{name="i",width=32,direction="input",signed=true},new{name="k",width=32,direction="output",signed=true},new{name="q",width=8,direction="output"},new{name="ok",width=32,direction="output"}},
        memories=new[]{new{name="m",width=8,depth=1,associative=true,key_width=32,key_signed=true}},continuous=Array.Empty<object>(),
        body=new{kind="block",items=new object[]{new{kind="blocking",target=new{kind="index",name="m",index=Ref("i")},value=Constant("10101010")},new{kind="assoc_traverse",name="m",operation="first",index=Ref("k"),target=Ref("ok")},new{kind="nba",target=Ref("q"),value=new{kind="index",name="m",index=Ref("k")}}}}
    })!;

    [Test]
    public void SparseStorageAndTypedTraversalUseBothEmitters()
    {
        var model=RtlDesign.Parse(Fixture().ToJsonString());
        Assert.That(model.Memories.Single().Associative,Is.True);
        foreach(var code in new[]{model.EmitCpp(),model.EmitModelHeader("dut")}){
            Assert.That(code,Does.Contain("std::vector<W>(0,"));Assert.That(code,Does.Contain("assoc_write("));Assert.That(code,Does.Contain("assoc_read("));Assert.That(code,Does.Contain("assoc_traverse("));
        }
    }

    [TestCase(0),TestCase(1),TestCase(2),TestCase(3),TestCase(4),TestCase(5),TestCase(6),TestCase(7),TestCase(8)]
    public void MalformedAssociativeContractsAreRejected(int fault)
    {
        var node=Fixture();var memory=node["memories"]![0]!;var traversal=node["body"]!["items"]![1]!;
        switch(fault){case 0:memory["key_width"]=0;break;case 1:memory["key_width"]=257;break;case 2:memory["queue"]=true;break;case 3:memory["depth"]=2;break;case 4:memory["dimensions"]=new JsonArray();break;case 5:traversal["operation"]="find";break;case 6:traversal["index"]=JsonSerializer.SerializeToNode(Ref("i"));break;case 7:traversal["target"]=JsonSerializer.SerializeToNode(Ref("k"));break;case 8:memory["associative"]=false;break;}
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>());
    }

    [Test]
    public void AssociativeKeyWidthsAreBoundedByTheDeclaredWordContract()
    {
        Prop.ForAll<int>(width=>{var node=Fixture();node["memories"]![0]!["key_width"]=width;node["signals"]![2]!["width"]=width;try{RtlDesign.Parse(node.ToJsonString());return width is >=1 and <=256;}catch(IrValidationException){return width is <1 or >256;}})
            .Check(Config.QuickThrowOnFailure.WithMaxTest(100).WithReplay(20261103,864013));
    }

    [Test]
    public void TraversalReadersCannotRaceStorageWriters()
    {
        var node=Fixture();var items=node["body"]!["items"]!.AsArray();
        node["body"]=JsonSerializer.SerializeToNode(new{kind="parallel",items=new object[]{items[0]!.DeepClone(),new{kind="block",items=new[]{items[1]!.DeepClone(),items[2]!.DeepClone()}}}});
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>().With.Message.Contains("isolated"));
    }

    [TestCase(true,32),TestCase(false,128)]
    public Task AssociativeMethodsMatchVerilatorAndSortedDictionary(bool signed,int keyWidth)
    {
        // Verilator 5.020 orders signed associative keys as unsigned. Keep this comparison
        // nonnegative; native tests independently check negative-key numeric ordering.
        // Guard missing reads: this Verilator version inserts a default cell on access;
        // native tests check that missing reads leave the associative inventory unchanged.
        string keyType=signed?"int":$"bit[{keyWidth-1}:0]";
        string initialKey=signed?"2":$"{keyWidth}'h{(BigInteger.One<<100):x}";
        string source=$"module dut(input logic clk,input logic[3:0]op,input logic {(signed?"signed":"")}[{keyWidth-1}:0]i,input logic[7:0]a,output bit[7:0]q,output {keyType} k,output int ok,n);bit[7:0]m[{keyType}];logic[31:0]j;initial begin k=0;ok=0;m[{initialKey}]=99;end always @(posedge clk)begin ok=0;case(op)0:begin m[i]=a;q=m[i];end 1:q=m.exists(i)?m[i]:0;2:begin m.delete(i);q=0;end 3:begin m.delete();q=0;end 4:begin ok=m.exists(i);q=0;end 5:begin ok=m.first(k);q=0;end 6:begin ok=m.next(k);q=0;end 7:begin ok=m.prev(k);q=0;end 8:begin ok=m.last(k);q=0;end 9:begin k=i;q=0;end 10:begin q=99;m.delete();for(j=0;j<257;j++)m[j]=a;end default:q=0;endcase n<=m.num();end endmodule";
        // The reference uses the BCL ordered dictionary with mathematical keys;
        // it does not reuse the native sign-bit encoding or map adapter logic.
        var dictionary=new SortedDictionary<BigInteger,byte>();BigInteger index=0;byte output=0;
        var vectors=new List<Dictionary<string,string>>();var expected=new List<Dictionary<string,string>>();
        string Bits(BigInteger value,int width)=>string.Concat(Enumerable.Range(0,width).Reverse().Select(bit=>((value>>bit)&BigInteger.One).IsZero?'0':'1'));
        void Step(int operation,BigInteger key,byte payload=0){
            vectors.Add(new(){["op"]=Bits(operation,4),["i"]=Bits(key,keyWidth),["a"]=Bits(payload,8)});int ok=0;
            switch(operation){
                case 0:dictionary[key]=payload;output=dictionary[key];break;
                case 1:output=dictionary.GetValueOrDefault(key);break;
                case 2:dictionary.Remove(key);output=0;break;
                case 3:dictionary.Clear();output=0;break;
                case 4:ok=dictionary.ContainsKey(key)?1:0;output=0;break;
                case 9:index=key;output=0;break;
                default:
                    BigInteger? candidate=operation switch{
                        5=>dictionary.Keys.Select(k=>(BigInteger?)k).FirstOrDefault(),
                        8=>dictionary.Keys.Reverse().Select(k=>(BigInteger?)k).FirstOrDefault(),
                        6=>dictionary.Keys.Where(k=>k>index).Select(k=>(BigInteger?)k).FirstOrDefault(),
                        _=>dictionary.Keys.Reverse().Where(k=>k<index).Select(k=>(BigInteger?)k).FirstOrDefault()};
                    if(candidate is {} next){index=next;ok=1;}output=0;break;
            }
            expected.Add(new(){["q"]=Bits(output,8),["k"]=Bits(index,keyWidth),["ok"]=Bits(ok,32),["n"]=Bits(dictionary.Count,32)});
        }
        BigInteger special=signed?2:BigInteger.One<<100;
        dictionary[special]=99;Step(1,special);Step(3,0);Step(0,special,12);Step(0,3,13);Step(0,0,10);Step(4,3);Step(4,77);Step(5,0);Step(6,0);Step(6,0);Step(6,0);Step(7,0);Step(8,0);Step(2,0);Step(5,0);Step(6,0);Step(1,0);Step(0,3,99);Step(9,3);Step(8,0);Step(2,3);Step(8,0);Step(3,0);Step(5,0);Step(8,0);Step(1,5);
        const string rollback="probe.op=3;probe.clk=0;probe.eval();probe.clk=1;probe.eval();probe.op=0;probe.i=42;probe.a=7;probe.clk=0;probe.eval();probe.clk=1;probe.eval();auto saved=probe.q.bits();auto saved_n=probe.n.bits();probe.op=10;probe.clk=0;probe.eval();probe.clk=1;bool rejected=false;try{probe.eval();}catch(const std::length_error&){rejected=true;}if(!rejected||probe.q.bits()!=saved||probe.n.bits()!=saved_n)return 101;probe.op=1;probe.eval();if(probe.q.bits()!=saved||probe.n.bits()!=saved_n)return 102;";
        return RtlIndexedConformanceTests.Qualify($"associative-{signed}-{keyWidth}",source,[new("clk",1,"input"),new("op",4,"input"),new("i",keyWidth,"input",Signed:signed),new("a",8,"input"),new("q",8,"output",TwoState:true),new("k",keyWidth,"output",Signed:signed,TwoState:true),new("ok",32,"output",Signed:true,TwoState:true),new("n",32,"output",Signed:true,TwoState:true),new("j",32,"internal")],vectors,sdk:true,selectedProbe:false,sdkProbe:rollback,expectedOutputs:expected,expectedMemories:[new("m",8,1,TwoState:true,Associative:true,KeyWidth:keyWidth,KeySigned:signed)],useVerilator:true);
    }
}
