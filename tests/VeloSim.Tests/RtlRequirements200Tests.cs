using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace VeloSim.Tests;

/// <summary>Numbered VS-RTL-BLOCK qualification: independent native oracles and same-source Icarus comparisons.</summary>
[NonParallelizable]
public class RtlRequirements200Tests
{
    /// <summary>Minimized regression: an internal logic declaration is the actual object behind a packed LHS selection.</summary>
    [Test,Category("RealFifo")]
    public async Task UhdmLogicVariableBlockingSelectionMatchesIcarus()
    {
        const string source="module dut(input logic clk,input logic[1:0]a,output logic[1:0]q);logic[1:0]t;always @(posedge clk)begin t=a;t[0]=1'b0;q<=t;end endmodule";
        var vectors=(from high in "01xz" from low in "01xz" select new Dictionary<string,string>{["a"]=$"{high}{low}"}).ToList();
        await RtlIndexedConformanceTests.Qualify("logic-var-blocking-minimized",source,[new("clk",1,"input"),new("a",2,"input"),new("q",2,"output"),new("t",2,"internal")],vectors,sdk:true,selectedProbe:false);
    }
    static bool configured;
    static readonly string EvidenceRun=Guid.NewGuid().ToString("N");
    static object Ref(string name) => new { kind="ref", name };
    static object Const(string bits) => new { kind="const", bits };
    static object Assign(object target, object value, string kind="blocking") => new { kind, target, value };
    static object Block(params object[] items) => new { kind="block", items };
    static object Bit(string name,object index) => new { kind="bitselect",value=Ref(name),index };
    static object Slice(string name,int width,object basis,bool descending=false) => new { kind="indexedselect",value=Ref(name),@base=basis,width,descending };
    static object Cat(object a,object b) => new { kind="concat",args=new[]{a,b} };
    static object Mem(object index) => new { kind="index",name="m",index };
    static object Process(object body,string clock="clk") => new { kind="process",events=new[]{new{signal=clock,edge="posedge"}},body };
    static readonly int[] Widths=[1,2,4,7,8,16,31,32,33,63,64,65,80,96,127,128,8,65,128,32];
    // Cross limb boundaries while retaining untouched bits in most parent words.
    static readonly int[] SliceWidths=[1,1,2,4,7,8,16,31,32,33,63,64,65,80,96,128,3,4,127,7];
    static string Root() { for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,"native/rtl/runtime.h")))return d.FullName;throw new IOException("Checkout missing"); }
    static string Pattern(int width,int phase) => string.Concat(Enumerable.Range(0,width).Select(bit=>"01xz"[(bit+phase)%4]));
    static string Coerce(string bits,bool twoState) => twoState?bits.Replace('x','0').Replace('z','0'):bits;
    // Strings are MSB-first; selection bases count from the LSB. Out-of-range bits are clipped independently.
    static string Deposit(string before,string value,long basis,int count,bool descending,bool unknown=false) {
        var result=before.ToCharArray();if(unknown)return before;
        long start=descending?basis-count+1:basis;
        for(int bit=0;bit<count;++bit){long index=start+bit;if(index>=0&&index<before.Length)result[before.Length-1-(int)index]=value[value.Length-1-bit];}
        return new(result);
    }
    static string Json(int width,object body,bool twoState=false,int lower=4) => JsonSerializer.Serialize(new {
        format="velosim.rtl.v1",clock="clk",
        signals=new object[]{new{name="clk",width=1,direction="input"},new{name="aux",width=1,direction="input"},new{name="a",width,direction="input"},new{name="b",width,direction="input"},new{name="i",width=64,direction="input"},new{name="q",width,direction="output",two_state=twoState},new{name="r",width,direction="output",two_state=twoState}},
        memories=new[]{new{name="m",width,depth=4,lower}},continuous=Array.Empty<object>(),body });
    /// <summary>Stable case names/categories support the ten-build/fifteen-review checkpoint ledger.</summary>
    public static IEnumerable<TestCaseData> Cases() {
        for(int id=1;id<=200;++id)yield return new TestCaseData(id).SetName($"RequirementIteration{id:000}").SetCategory($"Req200_{id:000}");
    }
    /// <summary>Qualifies one numbered execution, rejection or same-source frontend contract.</summary>
    [TestCaseSource(nameof(Cases))]
    public async Task QualifyRequirement(int id) {
        if(id<=120 || id>=181)await Execute(id);
        else if(id<=160)Reject(id);
        else await Frontend(id);
        TestContext.Progress.WriteLine($"REQUIREMENT_ITERATION {id:000} PASS; randomized seed {20261024+id}");
    }
    static async Task Execute(int id) {
        int variant=(id-1)%20,width=Widths[variant],group=(id-1)/20;
        bool twoState=id>=181; if(group==4)width=id==99?31:Math.Min(width,64);
        int slice=Math.Min(width,SliceWidths[variant]),fixedBase=Math.Max(0,width-slice);
        object target=group switch {
            0=>Bit("q",Ref("i")),
            1=>new{kind="partselect",value=Ref("q"),left=fixedBase+slice-1,right=fixedBase},
            2=>Slice("q",slice,Ref("i")),3=>Slice("q",slice,Ref("i"),true),
            _=>Bit("q",Ref("i"))};
        object body;
        if(group==4)body=Block(Assign(Ref("q"),Ref("a")),Assign(Ref("r"),Ref("b")),Assign(Cat(Ref("q"),Ref("r")),Cat(Ref("r"),Ref("q"))));
        else if(group==5)body=Block(Assign(Mem(Ref("i")),Ref("a")),Assign(Ref("q"),Mem(Ref("i")),"nba"),Assign(Ref("r"),Mem(Const("100")),"nba"));
        else body=Block(Assign(Ref("q"),Ref("a")),Assign(target,Ref("b")),Assign(Ref("r"),Ref("q"),"nba"));
        // q becomes one, but the second destination must still use its captured old q index (zero).
        if(id==100)body=Block(Assign(Ref("q"),Const(new string('0',width))),Assign(Ref("r"),Const(new string('0',width))),Assign(Cat(Ref("q"),Bit("r",Ref("q"))),Const(new string('0',width-1)+"11")));
        string json=Json(width,body,twoState),root=Root();
        if(id==99){
            // A nested 93-bit rotation crosses the native 64-bit limb without aliasing destinations.
            var node=JsonNode.Parse(json)!;node["signals"]!.AsArray().Add(new JsonObject{["name"]="t",["width"]=width,["direction"]="output"});
            node["body"]=JsonSerializer.SerializeToNode(Block(Assign(Ref("q"),Ref("a")),Assign(Ref("r"),Ref("b")),Assign(Ref("t"),Const(new string('1',width))),Assign(Cat(Ref("q"),Cat(Ref("r"),Ref("t"))),Cat(Ref("r"),Cat(Ref("t"),Ref("q"))))));json=node.ToJsonString();
        }
        if(id is 199 or 200){
            // Independent blocking destinations and their own NBA readers must coexist without false race rejection.
            var node=JsonNode.Parse(json)!;
            foreach(string name in new[]{"u","v"})node["signals"]!.AsArray().Add(new JsonObject{["name"]=name,["width"]=width,["direction"]="output",["two_state"]=true});
            object other=Block(Assign(Ref("u"),Ref("b")),Assign(Bit("u",Ref("i")),Ref("a")),Assign(Ref("v"),Ref("u"),"nba"));
            node["body"]=JsonSerializer.SerializeToNode(new{kind="parallel",items=new[]{body,other}});json=node.ToJsonString();
        }
        string evidenceRoot=Environment.GetEnvironmentVariable("VELOSIM_REQ200_EVIDENCE_ROOT")??Path.Combine(root,"artifacts/requirements-200/native",EvidenceRun);
        string path=Path.Combine(evidenceRoot,$"{id:000}");Directory.CreateDirectory(path);
        // Compile on the native filesystem; retain source, inputs and an identical executable in the checkout.
        string checkoutKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
        string shared=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VeloSim","requirements-200-native",checkoutKey);Directory.CreateDirectory(shared);
        // Protect per-case evidence as well as shared compiler inputs from competing processes.
        using var buildLock=new FileStream(Path.Combine(shared,"build.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        File.Delete(Path.Combine(path,"manifest.json"));
        File.WriteAllText(Path.Combine(path,"commands.log"),"");File.WriteAllText(Path.Combine(path,"status.json"),"{\"status\":\"running\"}");
        TestContext.Progress.WriteLine($"REQUIREMENT_EVIDENCE {id:000} {path}");
        try {
        var design=RtlDesign.Parse(json);File.WriteAllText(Path.Combine(path,"design.json"),json);File.WriteAllText(Path.Combine(path,"design.cpp"),design.EmitCpp());File.Copy(Path.Combine(root,"native/rtl/runtime.h"),Path.Combine(path,"runtime.h"),true);
        File.WriteAllText(Path.Combine(path,"CMakeLists.txt"),"cmake_minimum_required(VERSION 3.20)\nproject(requirement LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nadd_executable(requirement design.cpp)\n");
        var stimulus=new List<string>();var expected=new List<string>();string[] memory=Enumerable.Repeat(new string('x',width),4).ToArray();
        var random=new Random(20261024+id);
        for(int row=0;row<44;++row){
            string a=row<12?Pattern(width,row+variant):string.Concat(Enumerable.Range(0,width).Select(_=>"01xz"[random.Next(4)])),b=row<12?Pattern(width,row+variant+1):string.Concat(Enumerable.Range(0,width).Select(_=>"01xz"[random.Next(4)]));
            long basis=row switch{0=>0,1=>width-1,2=>width,3=>Math.Max(0,width-slice),4=>63,5=>64,6=>127,_=>row%4+4};bool unknown=row==10||row==11;
            if(group==5)basis=row switch{0=>4,1=>7,2=>3,3=>8,_=>row%4+4};
            if(row>=12){basis=group==5?random.Next(3,9):random.Next(width+8);unknown=random.Next(8)==0;}
            string index=unknown?new string(row==10?'x':'z',64):Convert.ToString(basis,2).PadLeft(64,'0');
            string q,r;
            if(group==4){q=b;r=a;}
            else if(group==5){if(!unknown&&basis>=4&&basis<8)memory[basis-4]=a;q=!unknown&&basis>=4&&basis<8?memory[basis-4]:new string('x',width);r=memory[0];}
            else {int count=group==0||twoState?1:slice;string rhs=b[^count..];q=Coerce(Deposit(Coerce(a,twoState),rhs,group==1?fixedBase:basis,count,group==3,group!=1&&unknown),twoState);r=q;}
            if(id==100)q=r=new string('0',width-1)+"1";
            string extra="";if(id==99){q=b;r=new string('1',width);extra=$" {a}";}
            if(id is 199 or 200){string other=Coerce(Deposit(Coerce(b,true),a[^1..],basis,1,false,unknown),true);extra=$" {other} {other}";}
            stimulus.Add($"0 {a} {b} {index}");expected.Add($"VS_RTL {row+1} {q} {r}{extra}");
        }
        File.WriteAllLines(Path.Combine(path,"stimulus.txt"),stimulus);
        File.WriteAllLines(Path.Combine(path,"expected.txt"),expected);
        var runner=new ProcessRunner();async Task<ProcessResult> Run(string exe,params string[] args){var result=await runner.RunAsync(exe,args,path,TimeSpan.FromMinutes(3));File.AppendAllText(Path.Combine(path,"commands.log"),JsonSerializer.Serialize(new{exe,args,result.ExitCode,result.StandardOutput,result.StandardError})+"\n");Assert.That(result.ExitCode,Is.Zero,result.StandardOutput+result.StandardError);return result;}
        File.Copy(Path.Combine(path,"design.cpp"),Path.Combine(shared,"design.cpp"),true);File.Copy(Path.Combine(path,"runtime.h"),Path.Combine(shared,"runtime.h"),true);
        if(!configured){File.Copy(Path.Combine(path,"CMakeLists.txt"),Path.Combine(shared,"CMakeLists.txt"),true);await Run("cmake","-S",shared,"-B",Path.Combine(shared,"build"),"-G","Ninja","-DCMAKE_BUILD_TYPE=Release");configured=true;}
        // Cross-filesystem copies can retain timestamps that Ninja considers unchanged.
        await Run("cmake","--build",Path.Combine(shared,"build"),"--clean-first","--parallel","4");
        string builtExecutable=Path.Combine(shared,"build/requirement"),executable=Path.Combine(path,"requirement");File.Copy(builtExecutable,executable,true);
        Assert.That(File.ReadAllBytes(executable),Is.EqualTo(File.ReadAllBytes(builtExecutable)),"Retained executable must match the actual compiled binary.");
        foreach(int workers in new[]{1,4,8}){var result=await Run(builtExecutable,Path.Combine(path,"stimulus.txt"),workers.ToString());Assert.That(result.StandardOutput.Replace("\r","").Trim().Split('\n'),Is.EqualTo(expected),$"SEMANTIC_MISMATCH iteration {id:000}, workers {workers}");}
        File.WriteAllText(Path.Combine(path,"manifest.json"),JsonSerializer.Serialize(new{iteration=id,seed=20261024+id,vectors=44,inputs=new[]{"design.json","design.cpp","runtime.h","stimulus.txt","expected.txt"}.Select(file=>ArtifactMetadata.Fingerprint(Path.Combine(path,file))),executable=ArtifactMetadata.Fingerprint(executable),workers=new[]{1,4,8}}));
        File.WriteAllText(Path.Combine(path,"status.json"),"{\"status\":\"passed\"}");
        } catch(Exception error) { File.WriteAllText(Path.Combine(path,"status.json"),JsonSerializer.Serialize(new{status="failed",error=error.Message}));throw; }
    }
    static void Reject(int id) {
        int width=Widths[(id-1)%20];if(id%4==2)width=Math.Min(width,64);object target=(id%4) switch{0=>Ref("q"),1=>Bit("q",Const("0")),2=>Cat(Ref("q"),Ref("r")),_=>Mem(Const("100"))};
        object writer=Block(Assign(Ref("q"),Ref("a")),Assign(target,Ref("a")),Assign(Ref("r"),Ref("b")));
        string expected;object body;
        if(id<=140){object read=id%4==3?Mem(Const("100")):Ref("q");writer=id%4==2?Block(Assign(target,Cat(Ref("a"),Ref("b")))):writer;body=new{kind="parallel",items=new[]{Process(writer),Process(Assign(Ref("r"),read,"nba"),"aux")}};expected=id%4==2?"same word":"same word";
            // Separate destination for the second process, avoiding writer-conflict masking isolation.
            var node=JsonNode.Parse(Json(width,body))!;node["signals"]!.AsArray().Add(new JsonObject{["name"]="seen",["width"]=width,["direction"]="output"});node["body"]!["items"]![1]!["body"]!["target"]!["name"]="seen";
            if(id>=137){node["body"]=node["body"]!["items"]![0]!.DeepClone();node["continuous"]=JsonSerializer.SerializeToNode(new[]{new{target=Ref("seen"),value=read}});}
            else if(id%2==0){var processes=node["body"]!["items"]!.AsArray();var first=processes[0]!.DeepClone();processes[0]=processes[1]!.DeepClone();processes[1]=first;}
            Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()),$"ISOLATION_GUARD iteration {id:000}")!.Message,Does.Contain("isolated"));return;
        }
        body=Block(Assign(target,Ref("a")),Assign(target,Ref("b"),"nba"),Assign(Ref("q"),Ref("a")),Assign(Ref("r"),Ref("b")));expected="mix blocking";
        if(id%4==2)width=Math.Min(width,64);
        Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(Json(width,body)))!.Message,Does.Contain(expected));
    }
    static async Task Frontend(int id) {
        int v=id-161,width=new[]{8,16,65,128}[v%4];string update;bool memory=v>=12&&v<16,concat=v>=8&&v<12;
        if(concat)width=Math.Min(width,64);
        update=(v/4) switch{0=>"temp[0]=b[0];",1=>$"temp[{Math.Min(width,4)-1}:0]=b;",2=>"{temp,other}={other,temp};",3=>"m[i]=a;temp=m[i];",_=>"temp[i +: 4]=b;"};
        string declarations=concat?$"logic[{width-1}:0]other;":"";
        if(memory)declarations+=$"logic[{width-1}:0]m[4:7];";
        string source=$"module dut(input logic clk,input logic[{width-1}:0]a,b,input logic[63:0]i,output logic[{width-1}:0]q,r);logic[{width-1}:0]temp;{declarations}always @(posedge clk)begin temp=a;{(concat?"other=b;":"")}{update}q<=temp;r<=q;end endmodule";
        var signals=new List<RtlSignal>{new("clk",1,"input"),new("a",width,"input"),new("b",width,"input"),new("i",64,"input"),new("q",width,"output"),new("r",width,"output"),new("temp",width,"internal")};if(concat)signals.Add(new("other",width,"internal"));
        var random=new Random(20261024+id);
        var vectors=Enumerable.Range(0,44).Select(row=>new Dictionary<string,string>{["a"]=row<12?Pattern(width,row):string.Concat(Enumerable.Range(0,width).Select(_=>"01xz"[random.Next(4)])),["b"]=row<12?Pattern(width,row+1):string.Concat(Enumerable.Range(0,width).Select(_=>"01xz"[random.Next(4)])),["i"]=row==10?new string('x',64):Convert.ToString(row<12?(memory?row%6+3:row%(width+1)):(memory?random.Next(3,9):random.Next(width+4)),2).PadLeft(64,'0')}).ToList();
        await RtlIndexedConformanceTests.Qualify($"requirements-{id:000}",source,signals.ToArray(),vectors,sdk:true,selectedProbe:false);
    }
}
