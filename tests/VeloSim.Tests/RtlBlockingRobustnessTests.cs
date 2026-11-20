using FsCheck;
using FsCheck.Fluent;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VeloSim.Tests;

/// <summary>Seeded guard properties and malformed-input probes for VS-RTL-BLOCK-004.</summary>
public class RtlBlockingRobustnessTests
{
    // Stable replay seeds are kept in source and printed in test evidence.
    const ulong Seed=20261024, Gamma=864013;
    static object Ref(string name)=>new{kind="ref",name};
    static object Zero()=>new{kind="const",bits="0"};
    static object Memory(string bits="0")=>new{kind="index",name="m",index=new{kind="const",bits}};
    static object Target(int shape)=>shape switch{0=>Ref("q"),1=>new{kind="bitselect",value=Ref("q"),index=Zero()},2=>new{kind="concat",args=new[]{Ref("q"),Ref("r")}},_=>Memory()};
    static object Assign(object target,object value,string kind="blocking")=>new{kind,target,value};
    static object Block(params object[] items)=>new{kind="block",items};
    static object Process(object body,string signal="clk")=>new{kind="process",events=new[]{new{signal,edge="posedge"}},body};
    static string Fixture(int width,object body,object[]? continuous=null)=>JsonSerializer.Serialize(new{
        format="velosim.rtl.v1",clock="clk",signals=new object[]{new{name="clk",width=1,direction="input"},new{name="aux",width=1,direction="input"},new{name="a",width,direction="input"},new{name="q",width,direction="output"},new{name="r",width,direction="output"},new{name="seen",width,direction="output"}},memories=new[]{new{name="m",width,depth=2}},continuous=continuous??[],body});
    static bool Rejects(string json,string message){try{RtlDesign.Parse(json);return false;}catch(IrValidationException error){return error.Message.Contains(message,StringComparison.Ordinal);}}

    /// <summary>Parent ownership must reject mixing even for disjoint selections/elements and reversed order.</summary>
    [Test]
    public void MixedAssignmentKindsAreRejectedByProperty()
    {
        Prop.ForAll<byte,byte,bool>((size,shape,reverse)=>{
            int width=1+size%64;object target=Target(shape%4);
            object secondTarget=shape%4==3?Memory("1"):shape%4==1&&width>1?new{kind="bitselect",value=Ref("q"),index=new{kind="const",bits="1"}}:target;
            object first=Assign(target,Ref("a"),reverse?"nba":"blocking"),second=Assign(secondTarget,Ref("a"),reverse?"blocking":"nba");
            return Rejects(Fixture(width,Block(first,second,Assign(Ref("q"),Ref("a")),Assign(Ref("r"),Ref("a")),Assign(Ref("seen"),Ref("a")))),"mix blocking");
        }).Label("MIXED_KIND_GUARD").Check(Config.QuickThrowOnFailure.WithMaxTest(500).WithReplay(Seed,Gamma));
        TestContext.Progress.WriteLine($"BLOCKING_PROPERTY mixed: 500 trials; replay ({Seed},{Gamma})");
    }

    /// <summary>Both process orders and continuous readers must preserve blocking isolation.</summary>
    [Test]
    public void StorageReadersAreRejectedByProperty()
    {
        Prop.ForAll<byte,byte,bool>((size,shape,reverse)=>{
            int width=1+size%64;object target=Target(shape%4),read=shape%4==3?Memory():Ref("q");
            object writer=Process(Block(Assign(target,Ref("a")),Assign(Ref("q"),Ref("a")),Assign(Ref("r"),Ref("a"))));
            object reader=Process(Assign(Ref("seen"),read,"nba"),"aux");
            object body=new{kind="parallel",items=reverse?new[]{reader,writer}:new[]{writer,reader}};
            if(!Rejects(Fixture(width,body),"isolated"))return false;
            return Rejects(Fixture(width,writer,[new{target=Ref("seen"),value=read}]),"isolated");
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(500).WithReplay(Seed+1,Gamma));
        TestContext.Progress.WriteLine($"BLOCKING_PROPERTY isolation: 500 trials; replay ({Seed+1},{Gamma})");
    }

    /// <summary>Seeded malformed JSON shapes must fail through the validator's public error boundary.</summary>
    [Test]
    public void MalformedAssignmentFuzzRejectsThroughValidationBoundary()
    {
        const int seed=20261024;var random=new Random(seed);
        for(int trial=0;trial<500;++trial){
            var node=JsonNode.Parse(Fixture(1+random.Next(128),Block(Assign(Ref("q"),Ref("a")),Assign(Ref("r"),Ref("a")),Assign(Ref("seen"),Ref("a")))))!;
            var statement=node["body"]!["items"]![0]!.AsObject();
            int mutation=random.Next(8);
            switch(mutation){
                case 0: statement.Remove("target");break;
                case 1: statement.Remove("value");break;
                case 2: statement["target"]=random.Next();break;
                case 3: statement["kind"]="unsupported_"+random.Next();break;
                case 4: statement["extra"]=true;break;
                case 5: statement["target"]!["name"]="a";break;
                case 6: statement["target"]!["name"]="missing_"+random.Next();break;
                default: statement["value"]=new JsonObject{["kind"]="index",["name"]="m",["index"]=new JsonObject{["kind"]="const",["bits"]="not_binary"}};break;
            }
            string json=node.ToJsonString();
            Assert.That(()=>RtlDesign.Parse(json),Throws.TypeOf<IrValidationException>(),$"seed {seed}, trial {trial}, mutation {mutation}, counterexample {json}");
        }
        TestContext.Progress.WriteLine($"BLOCKING_FUZZ 500 malformed trials; seed {seed}; no failing counterexample");
    }
}
