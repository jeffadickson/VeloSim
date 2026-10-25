using FsCheck;
using FsCheck.Fluent;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;
/// <summary>Seeded properties and minimized regressions for combinational safety boundaries.</summary>
public class RtlCombinationalRobustnessTests
{
    static object Ref(string name)=>new{kind="ref",name};
    static object Write(string name,object value,string kind="blocking")=>new{kind,target=Ref(name),value};
    static object Block(params object[] items)=>new{kind="block",items};
    static string Ir(object[] comb,object? body=null,object[]? continuous=null,int width=8)=>JsonSerializer.Serialize(new{format="velosim.rtl.v1",clock="clk",signals=new[]{new{name="clk",width=1,direction="input"},new{name="a",width,direction="input"},new{name="q",width,direction="output"},new{name="r",width,direction="output"}},memories=Array.Empty<object>(),continuous=continuous??[],combinational=comb,body=body??Block()});
    [Test] public void ContinuousSelfFeedbackRemainsRejected()=>Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(Ir([Write("r",Ref("a"))],continuous:[new{target=Ref("q"),value=Ref("q")}])));
    [TestCase(false),TestCase(true)] public void CombinationalReadersCannotObserveIsolatedBlockingState(bool condition){
        object reader=condition?new{kind="if",condition=Ref("q"),then=Write("r",Ref("a")),@else=Write("r",Ref("a"))}:Block(Write("r",Ref("q")));
        Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(Ir([reader],Write("q",Ref("a")))));
    }
    [Test] public void ContinuousInventoryExcludesProceduralBlocks(){
        Assert.That(RtlDesign.Parse(Ir([Write("q",Ref("a")),Write("r",Ref("q"))])).Continuous,Is.Empty);
        var design=RtlDesign.Parse(Ir([Write("r",Ref("q"))],continuous:[new{target=Ref("q"),value=Ref("a")}])) ;
        Assert.That(design.Continuous,Has.Count.EqualTo(1));Assert.That(design.Continuous[0].GetProperty("target").GetProperty("name").GetString(),Is.EqualTo("q"));
    }
    /// <summary>Individually bounded blocks must also respect the shared settle execution budget.</summary>
    [Test] public void AggregateCombinationalExpansionBudgetIsEnforced(){
        object Loop(string name)=>Block(Write(name,Ref("a")),new{kind="repeat",count=256,body=new{kind="repeat",count=48,body=Write(name,Ref("a"))}});
        var error=Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(Ir([Loop("q"),Loop("r")])));
        Assert.That(error!.Message,Does.Contain("Expanded combinational statement budget"));
    }
    /// <summary>Every path must assign a block-owned output before it can be read or returned.</summary>
    [Test] public void MissingBranchAssignmentProperty(){
        Prop.ForAll<byte,bool>((size,reverse)=>{
            int width=1+size%128;object assignment=Write("q",Ref("a"));object condition=new{kind="if",condition=Ref("a"),then=reverse?Block():assignment,@else=reverse?assignment:Block()};
            try{RtlDesign.Parse(Ir([condition,Write("r",Ref("a"))],width:width));return false;}catch(IrValidationException error){return error.Message.Contains("every path",StringComparison.Ordinal);}
        }).Label("COMB_DEFINITE_ASSIGNMENT").Check(Config.QuickThrowOnFailure.WithMaxTest(500).WithReplay(20261025,864013));
    }
    [Test] public void SelfReadBeforeWriteProperty(){
        Prop.ForAll<byte,bool>((size,reverse)=>{
            int width=1+size%128;string first=reverse?"q":"r",second=reverse?"r":"q";
            try{RtlDesign.Parse(Ir([Block(Write(first,Ref(second)),Write(second,Ref("a")))],width:width));return false;}catch(IrValidationException error){return error.Message.Contains("before definite",StringComparison.Ordinal);}
        }).Label("COMB_READ_BEFORE_WRITE").Check(Config.QuickThrowOnFailure.WithMaxTest(500).WithReplay(20261026,864013));
    }
    [Test] public void MalformedCombinationalFuzz(){
        const int seed=20261025;var random=new Random(seed);
        for(int trial=0;trial<500;trial++){
            var node=JsonNode.Parse(Ir([Write("q",Ref("a")),Write("r",Ref("a"))]))!;int shape=random.Next(6);
            switch(shape){case 0:node["combinational"]=random.Next();break;case 1:node["combinational"]![0]!["target"]=null;break;case 2:node["combinational"]![0]!["value"]!["name"]="absent_"+random.Next();break;case 3:node["combinational"]![0]!["kind"]="nba";break;case 4:node["combinational"]![0]!["bad"]=true;break;default:node["combinational"]![0]!["value"]=new JsonObject{["kind"]="convert",["value"]=new JsonObject{["kind"]="ref",["name"]="a"},["width"]=random.Next(2)==0?0:129,["signed"]=false,["two_state"]=false};break;}
            string json=node.ToJsonString();Assert.That(()=>RtlDesign.Parse(json),Throws.TypeOf<IrValidationException>(),$"seed {seed}, trial {trial}, shape {shape}, counterexample {json}");
        }
    }
}
