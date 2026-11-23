// Auto-extracted from pinned chipsalliance/UHDM commit 7c42de4057d0a7e2ef14048b8ee570c7ac3db94b.
// All Cap'n Proto struct layouts are computed using Cap'n Proto's exact HoleSet packing algorithm.
using System.Globalization;

namespace VeloSim.Uhdm;

internal enum UhdmFieldKind : byte
{
    Symbol = (byte)'S',
    Bool = (byte)'B',
    Int64 = (byte)'I',
    UInt64 = (byte)'U',
    UInt32 = (byte)'W',
    UInt16 = (byte)'H',
    ObjRef = (byte)'O',
    FactoryRef = (byte)'F',
    ObjList = (byte)'L',
    FactoryList = (byte)'M',
}

internal readonly record struct UhdmFieldSpec(string Name, UhdmFieldKind Kind, int Offset, uint TargetUhdmType);

internal sealed class UhdmClassSpec
{
    public required int ClassId { get; init; }
    public required string Name { get; init; }
    public required int BaseClassId { get; init; }
    public required uint UhdmType { get; init; }
    public required int VpiType { get; init; }
    public required int RootFactoryPointerSlot { get; init; }
    public required int DataWords { get; init; }
    public required int PointerCount { get; init; }
    public required UhdmFieldSpec[] Fields { get; init; }
    public required Dictionary<int, int> VpiPropertyFieldIndex { get; init; }
    public required Dictionary<int, (int SingleFieldIndex, int VectorFieldIndex)> VpiRelationFieldIndex { get; init; }
    public required Dictionary<string, int> FieldIndexByName { get; init; }
    public UhdmClassSpec? BaseClass { get; internal set; }
}

internal static class UhdmSchema
{
    public const int RootDataWords = 1;
    public const int RootPointerCount = 210;
    public const int RootDesignsPointerSlot = 0;
    public const int RootSymbolsPointerSlot = 1;

    // Any (BaseClass) Cap'n Proto layout: uhdmId@0:UInt64, vpiParent@1:ObjIndexType, vpiFile@2:UInt64,
    // vpiLineNo@3:UInt32, vpiEndLineNo@4:UInt32, vpiColumnNo@5:UInt16, vpiEndColumnNo@6:UInt16
    public const int AnyDataWords = 4;
    public const int AnyPointerCount = 1;
    public const int AnyUhdmIdByteOffset = 0;
    public const int AnyVpiParentPtrSlot = 0;
    public const int AnyVpiFileByteOffset = 8;
    public const int AnyVpiLineNoByteOffset = 16;
    public const int AnyVpiEndLineNoByteOffset = 20;
    public const int AnyVpiColumnNoByteOffset = 24;
    public const int AnyVpiEndColumnNoByteOffset = 26;

    // ObjIndexType Cap'n Proto layout: index@0:UInt64, type@1:UInt32
    public const int ObjIndexDataWords = 2;
    public const int ObjIndexPointerCount = 0;
    public const int ObjIndexByteOffset = 0;
    public const int ObjTypeByteOffset = 8;

    public static IReadOnlyList<UhdmClassSpec> AllClasses => Classes;
    public static IReadOnlyList<UhdmClassSpec> ConcreteClasses => Concrete;
    private static readonly UhdmClassSpec[] Classes;
    private static readonly UhdmClassSpec[] Concrete;
    private static readonly Dictionary<uint, UhdmClassSpec> ByUhdmType;

    static UhdmSchema()
    {
        var lines = SchemaData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Classes = new UhdmClassSpec[lines.Length + 1];
        ByUhdmType = new Dictionary<uint, UhdmClassSpec>(lines.Length);
        var concreteList = new List<UhdmClassSpec>(lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            var parts = lines[i].Split('|');
            int id = i + 1;
            int baseId = int.Parse(parts[1], CultureInfo.InvariantCulture);
            uint uhdmType = uint.Parse(parts[2], CultureInfo.InvariantCulture);
            int vpiType = int.Parse(parts[3], CultureInfo.InvariantCulture);
            int factorySlot = int.Parse(parts[4], CultureInfo.InvariantCulture);
            int dataWords = int.Parse(parts[5], CultureInfo.InvariantCulture);
            int ptrCount = int.Parse(parts[6], CultureInfo.InvariantCulture);

            var fields = ParseFields(parts[7]);
            var fieldByName = new Dictionary<string, int>(fields.Length, StringComparer.OrdinalIgnoreCase);
            for (int f = 0; f < fields.Length; f++)
                fieldByName[fields[f].Name] = f;

            var props = ParseProps(parts[8]);
            var rels = ParseRels(parts[9]);

            var spec = new UhdmClassSpec
            {
                ClassId = id,
                Name = parts[0],
                BaseClassId = baseId,
                UhdmType = uhdmType,
                VpiType = vpiType,
                RootFactoryPointerSlot = factorySlot,
                DataWords = dataWords,
                PointerCount = ptrCount,
                Fields = fields,
                VpiPropertyFieldIndex = props,
                VpiRelationFieldIndex = rels,
                FieldIndexByName = fieldByName,
            };
            Classes[id] = spec;
            if (uhdmType != 0)
            {
                ByUhdmType[uhdmType] = spec;
                concreteList.Add(spec);
            }
        }

        for (int i = 1; i < Classes.Length; i++)
        {
            if (Classes[i].BaseClassId > 0)
                Classes[i].BaseClass = Classes[Classes[i].BaseClassId];
        }
        Concrete = concreteList.ToArray();
    }

    public static UhdmClassSpec? TryGetByUhdmType(uint uhdmType) =>
        ByUhdmType.TryGetValue(uhdmType, out var spec) ? spec : null;

    public static UhdmClassSpec GetByUhdmType(UhdmObjectType uhdmType) =>
        ByUhdmType.TryGetValue((uint)uhdmType, out var spec)
            ? spec
            : throw new ArgumentException($"Unknown UHDM object type: {uhdmType}", nameof(uhdmType));

    private static UhdmFieldSpec[] ParseFields(string text)
    {
        if (text.Length == 0) return [];
        var items = text.Split(',');
        var result = new UhdmFieldSpec[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var p = items[i].Split(':');
            result[i] = new UhdmFieldSpec(
                p[0],
                (UhdmFieldKind)(byte)p[1][0],
                int.Parse(p[2], CultureInfo.InvariantCulture),
                uint.Parse(p[3], CultureInfo.InvariantCulture));
        }
        return result;
    }

    private static Dictionary<int, int> ParseProps(string text)
    {
        var map = new Dictionary<int, int>();
        if (text.Length == 0) return map;
        foreach (var item in text.Split(','))
        {
            int eq = item.IndexOf('=');
            int key = int.Parse(item.AsSpan(0, eq), CultureInfo.InvariantCulture);
            int idx = int.Parse(item.AsSpan(eq + 1), CultureInfo.InvariantCulture);
            map[key] = idx;
        }
        return map;
    }

    private static Dictionary<int, (int, int)> ParseRels(string text)
    {
        var map = new Dictionary<int, (int, int)>();
        if (text.Length == 0) return map;
        foreach (var item in text.Split(','))
        {
            int eq = item.IndexOf('=');
            int colon = item.IndexOf(':', eq + 1);
            int key = int.Parse(item.AsSpan(0, eq), CultureInfo.InvariantCulture);
            int singleIdx = int.Parse(item.AsSpan(eq + 1, colon - eq - 1), CultureInfo.InvariantCulture);
            int vecIdx = int.Parse(item.AsSpan(colon + 1), CultureInfo.InvariantCulture);
            map[key] = (singleIdx, vecIdx);
        }
        return map;
    }

    private const string SchemaData = """
attribute|0|2030|105|13|5|1|VpiName:S:0:0,VpiValue:S:16:0,VpiDefFile:S:24:0,VpiDefAttribute:B:64:0,VpiDefLineNo:I:32:0|55=3,15=2,16=4,2=0|
virtual_interface_var|0|2406|728|205|0|1|||
let_decl|0|2204|903|107|1|3|VpiName:S:0:0,Expressions:L:1:0,Seq_formal_decls:M:2:2322|2=0|102=-1:1,679=-1:2
concurrent_assertions|0|0|0|-1|3|5|VpiName:S:0:0,VpiFullName:S:8:0,VpiIsClockInferred:B:128:0,Clocking_event:O:1:0,Stmt:O:2:0,VpiProperty:O:3:0,Attributes:M:4:2030|3=1,649=2,2=0|105=-1:6,690=3:-1,718=5:-1,104=4:-1
process_stmt|0|0|0|-1|1|3|Stmt:O:1:0,Module_inst:F:0:2229,Attributes:M:2:2030||105=-1:2,32=1:-1,104=0:-1
always|5|2013|1|3|1|1|VpiAlwaysType:I:0:0|624=0|
final_stmt|5|2139|676|68|0|1|||
initial|5|2178|24|96|0|1|||
atomic_stmt|0|0|0|-1|1|2|VpiName:S:0:0,Attributes:M:1:2030|2=0|105=-1:1
delay_control|9|2094|11|51|1|3|VpiDelay:S:0:0,Stmt:O:1:0,Delay:O:2:0||72=2:-1,104=1:-1
delay_term|0|2095|108|52|0|1|||
event_control|9|2119|13|63|0|3|VpiCondition:O:1:0,Stmt:O:2:0||71=0:-1,104=1:-1
repeat_control|9|2311|52|159|0|1|||
scope|0|0|0|-1|2|18|VpiName:S:0:0,VpiFullName:S:8:0,Property_decls:M:1:2287,Sequence_decls:M:2:2325,Concurrent_assertions:L:3:0,Named_events:M:4:2234,Named_event_arrays:M:5:2235,Variables:L:6:0,Virtual_interface_vars:M:7:2406,Logic_vars:M:8:2213,Array_vars:M:9:2020,Array_var_mems:M:10:2020,Parameters:L:11:0,Param_assigns:M:12:2258,Scopes:L:13:0,Typespecs:L:14:0,Instance_items:L:15:0,Let_decls:M:16:2204,Attributes:M:17:2030|3=1,2=0|105=-1:18,740=-1:4,726=-1:16,92=-1:14,903=-1:17,29=-1:11,34=-1:5,129=-1:6,40=-1:13,41=-1:12,655=-1:2,48=-1:9,116=-1:10,661=-1:3,725=-1:15,100=-1:7,728=-1:8
begin|14|2034|4|14|0|2|Stmts:L:1:0||104=-1:0
named_begin|14|2233|33|122|1|2|VpiEndLabel:S:0:0,Stmts:L:1:0|771=0|104=-1:1
named_fork|14|2240|35|125|2|2|VpiEndLabel:S:8:0,VpiJoinType:I:0:0,Stmts:L:1:0|771=0,603=1|104=-1:2
fork_stmt|14|2144|18|72|1|2|VpiJoinType:I:0:0,Stmts:L:1:0|603=0|104=-1:1
for_stmt|14|2140|15|73|1|7|VpiLocalVarDecls:I:0:0,VpiForInitStmt:O:3:0,VpiCondition:O:4:0,VpiForIncStmt:O:5:0,VpiStmt:O:6:0,VpiForInitStmts:L:1:0,VpiForIncStmts:L:2:0|609=0|71=2:-1,74=3:6,75=1:5,104=4:-1
if_stmt|9|2166|22|88|1|3|VpiQualifier:I:0:0,VpiCondition:O:1:0,VpiStmt:O:2:0|650=0|71=1:-1,104=2:-1
event_stmt|9|2120|14|64|2|1|VpiBlocking:B:0:0,Named_event:F:8:2234|41=0|34=1:-1
thread_obj|0|2373|647|188|0|1|||
forever_stmt|9|2143|17|71|0|2|VpiStmt:O:1:0||104=0:-1
waits|9|0|0|-1|0|2|VpiStmt:O:1:0||104=0:-1
wait_stmt|24|2430|69|208|0|2|VpiCondition:O:1:0||71=0:-1
wait_fork|24|2429|672|207|0|1|||
ordered_wait|24|2252|671|129|0|3|VpiElseStmt:O:2:0,VpiConditions:L:1:0||71=-1:1,73=0:-1
disables|9|0|0|-1|0|1|||
disable|28|2098|12|54|0|2|VpiExpr:O:1:0||102=0:-1
disable_fork|28|2099|673|55|0|1|||
continue_stmt|9|2086|685|47|0|1|||
break_stmt|9|2039|684|18|0|1|||
return_stmt|9|2315|666|161|0|2|VpiCondition:O:1:0||71=0:-1
while_stmt|9|2433|70|209|0|3|VpiCondition:O:1:0,VpiStmt:O:2:0||71=0:-1,104=1:-1
repeat|9|2310|51|158|0|3|VpiCondition:O:1:0,VpiStmt:O:2:0||71=0:-1,104=1:-1
do_while|9|2104|670|58|0|3|VpiCondition:O:1:0,VpiStmt:O:2:0||71=0:-1,104=1:-1
if_else|9|2165|23|87|1|4|VpiQualifier:I:0:0,VpiCondition:O:1:0,VpiStmt:O:2:0,VpiElseStmt:O:3:0|650=0|71=1:-1,73=3:-1,104=2:-1
case_stmt|9|2047|5|24|3|3|VpiRandType:U:0:0,VpiCaseType:I:8:0,VpiQualifier:I:16:0,VpiCondition:O:1:0,Case_items:M:2:2042|42=1,650=2,610=0|6=-1:4,71=3:-1
force|9|2141|16|69|0|3|Rhs:O:1:0,Lhs:O:2:0||77=1:-1,82=0:-1
assign_stmt|9|2026|2|11|0|3|Rhs:O:1:0,Lhs:O:2:0||77=1:-1,82=0:-1
deassign|9|2088|9|49|0|2|Lhs:O:1:0||77=0:-1
release|9|2309|50|157|0|2|Lhs:O:1:0||77=0:-1
null_stmt|9|2248|38|127|0|1|||
expect_stmt|9|2122|674|66|1|3|Stmt:O:1:0,Else_stmt:O:2:0,Property_spec:F:0:2294||73=1:-1,656=2:-1,104=0:-1
foreach_stmt|14|2142|675|70|0|4|Variable:O:1:0,VpiStmt:O:3:0,VpiLoopVars:L:2:0||737=-1:2,104=1:-1,100=0:-1
gen_scope|14|2155|134|83|1|21|VpiArrayMember:B:0:0,VpiProtected:B:1:0,VpiImplicitDecl:B:2:0,VpiIndex:O:1:0,Nets:L:2:0,Array_nets:M:3:2017,Process:L:4:0,Cont_assigns:M:5:2082,Modules:M:6:2229,Module_arrays:M:7:2227,Primitives:L:8:0,Primitive_arrays:L:9:0,Def_params:M:10:2089,Gen_scope_arrays:M:11:2156,Programs:M:12:2280,Program_arrays:M:13:2280,Assertions:L:14:0,Interfaces:M:15:2194,Interface_arrays:M:16:2191,Alias_stmts:M:17:2005,Clocking_blocks:M:18:2064,Task_funcs:L:19:0,Elab_tasks:L:20:0|607=0,26=2,10=1|646=-1:19,114=-1:5,744=-1:16,650=-1:20,8=-1:7,10=-1:12,133=-1:13,78=3:-1,601=-1:17,603=-1:18,32=-1:8,112=-1:9,36=-1:4,103=-1:10,113=-1:11,99=-1:6,602=-1:14,604=-1:15,57=-1:22,127=-1:21
gen_var|0|2161|135|85|2|2|VpiName:S:0:0,VpiFullName:S:8:0,Gen_scope_arrays:M:1:2156|3=1,2=0|133=-1:2
gen_scope_array|0|2156|133|84|4|3|VpiName:S:8:0,VpiFullName:S:16:0,VpiSize:I:0:0,VpiInstance:O:2:0,Gen_var:F:24:2161,Gen_scopes:M:1:2155|3=1,2=0,4=2|134=-1:5,135=4:-1,745=3:-1
assert_stmt|4|2023|686|9|1|2|Else_stmt:O:1:0,Clocking_block:F:0:2064||650=1:-1,73=0:-1
cover|4|2087|688|48|2|1|VpiIsCoverSequence:B:0:0,Clocking_block:F:8:2064|659=0|650=1:-1
assume|4|2028|687|12|1|1|Clocking_block:F:0:2064||650=0:-1
restrict|4|2313|901|160|1|1|Clocking_block:F:0:2064||650=0:-1
immediate_assert|9|2167|665|89|3|4|VpiIsDeferred:I:0:0,VpiIsFinal:I:8:0,Expr:O:1:0,Stmt:O:2:0,Else_stmt:O:3:0,Clocking_block:F:16:2064|657=0,658=1|650=5:-1,73=4:-1,102=2:-1,104=3:-1
immediate_assume|9|2168|694|90|3|4|VpiIsDeferred:I:0:0,VpiIsFinal:I:8:0,Expr:O:1:0,Stmt:O:2:0,Else_stmt:O:3:0,Clocking_block:F:16:2064|657=0,658=1|650=5:-1,73=4:-1,102=2:-1,104=3:-1
immediate_cover|9|2169|695|91|3|3|VpiIsDeferred:I:0:0,VpiIsFinal:I:8:0,Expr:O:1:0,Stmt:O:2:0,Clocking_block:F:16:2064|657=0,658=1|650=4:-1,102=2:-1,104=3:-1
expr|0|0|0|-1|3|2|VpiDecompile:S:0:0,VpiValue:S:16:0,VpiSize:I:8:0,Typespec:O:1:0|54=0,4=2|605=3:-1
case_item|0|2042|6|21|0|3|Stmt:O:2:0,VpiExprs:L:1:0||102=-1:1,104=0:-1
assignment|9|2027|3|10|5|3|VpiOpType:I:0:0,VpiBlocking:B:64:0,Rhs:O:1:0,Lhs:O:2:0,Delay_control:F:16:2094,Event_control:F:24:2119,Repeat_control:F:32:2311|41=1,39=0|11=4:-1,13=5:-1,77=3:-1,52=6:-1,82=2:-1
any_pattern|0|2014|667|4|1|1|VpiName:S:0:0|2=0|
tagged_pattern|0|2358|668|183|1|3|VpiName:S:0:0,Pattern:O:1:0,Typespec:O:2:0|2=0|721=1:-1,605=2:-1
struct_pattern|0|2348|669|175|1|2|VpiName:S:0:0,Pattern:O:1:0|2=0|721=1:-1
unsupported_expr|56|2390|4001|199|0|1|||
unsupported_stmt|9|2391|4000|200|1|1|VpiValue:S:0:0||
include_file_info|0|2172|3023|94|1|1|VpiIncludedFile:S:0:0|3024=0|
sequence_inst|0|2329|664|164|3|2|VpiName:S:8:0,Sequence_decl:F:0:2325,Clocking_block:F:16:2064,Named_event_sequence_expr_groups:L:1:0|2=0|89=-1:3,650=2:-1,661=1:-1
seq_formal_decl|0|2322|679|162|2|4|VpiName:S:0:0,VpiDirection:I:8:0,Named_event_sequence_expr_group:O:2:0,Typespec:O:3:0,Attributes:M:1:2030|20=1,2=0|105=-1:4,102=2:-1,605=3:-1
sequence_decl|0|2325|661|163|2|5|VpiName:S:0:0,VpiFullName:S:8:0,Sequence_expr_multiclock_group:O:3:0,Attributes:M:1:2030,Variables:L:2:0,Seq_formal_decls:M:4:2322|3=1,2=0|105=-1:3,102=2:-1,679=-1:5,100=-1:4
prop_formal_decl|0|2284|699|147|2|3|VpiName:S:0:0,VpiDirection:I:8:0,VpiExpr:O:1:0,Typespec:O:2:0|20=1,2=0|102=2:-1,605=3:-1
property_inst|0|2292|660|144|3|3|VpiName:S:8:0,VpiDisableCondition:O:1:0,Property_decl:F:0:2287,Clocking_block:F:16:2064,VpiArguments:L:2:0|2=0|89=-1:4,650=3:-1,689=1:-1,655=2:-1
property_spec|0|2294|656|145|0|4|VpiDisableCondition:O:1:0,VpiClockingEvent:O:2:0,VpiPropertyExpr:O:3:0||690=1:-1,689=0:-1,657=2:-1
property_decl|0|2287|655|143|3|4|VpiName:S:0:0,VpiFullName:S:8:0,Property_spec:F:16:2294,Attributes:M:1:2030,Prop_formal_decls:M:2:2284,Variables:L:3:0|3=1,2=0|105=-1:3,699=-1:4,656=2:-1,100=-1:5
clocked_property|0|2061|902|35|0|3|VpiClockingEvent:O:1:0,VpiPropertyExpr:O:2:0||690=0:-1,657=1:-1
case_property_item|0|2045|905|23|0|3|Property_expr:O:2:0,Expressions:L:1:0||102=-1:1,657=0:-1
case_property|0|2044|662|22|0|3|VpiCondition:O:1:0,Case_property_items:M:2:2045||905=-1:1,71=0:-1
multiclock_sequence_expr|0|2232|658|121|0|2|Clocked_seqs:M:1:2062||659=-1:0
clocked_seq|0|2062|659|36|0|3|VpiClockingEvent:O:1:0,VpiSequenceExpr:O:2:0||690=0:-1,3005=1:-1
simple_expr|56|0|0|-1|0|2|VpiUses:L:1:0||101=-1:0
constant|56|2071|7|39|1|1|VpiConstType:I:0:0|40=0|
let_expr|56|2206|904|108|1|2|Let_decl:F:0:2204,Arguments:L:1:0||89=-1:1,903=0:-1
operation|56|2251|39|128|2|3|VpiOpType:I:0:0,VpiReordered:B:64:0,VpiFlattened:B:65:0,VpiOpStrong:B:66:0,Operands:L:1:0,Attributes:M:2:2030|5005=2,656=3,39=0,5001=1|105=-1:5,97=-1:4
ref_obj|77|2302|608|152|4|2|VpiName:S:0:0,VpiFullName:S:8:0,VpiDefName:S:16:0,VpiGeneric:B:192:0,VpiStructMember:B:193:0,Actual_group:O:1:0|9=2,3=1,653=3,2=0,3013=4|700=5:-1
ref_module|0|2300|5011|151|2|3|VpiName:S:0:0,VpiDefName:S:8:0,Actual_group:O:1:0,Ports:M:2:2268|9=1,2=0|700=2:-1,44=-1:3
ref_typespec|77|2304|769|153|3|2|VpiName:S:0:0,VpiFullName:S:8:0,VpiDefName:S:16:0,Actual_typespec:O:1:0|9=2,3=1,2=0|700=3:-1
part_select|81|2262|42|136|1|3|VpiConstantSelect:B:0:0,Left_range:O:1:0,Right_range:O:2:0|53=0|79=1:-1,83=2:-1
indexed_part_select|81|2176|130|95|2|3|VpiConstantSelect:B:0:0,VpiIndexedPartSelectType:I:8:0,Base_expr:O:1:0,Width_expr:O:2:0|53=0,72=1|131=2:-1,132=3:-1
var_select|81|2397|68|204|1|3|VpiConstantSelect:B:0:0,Expr:O:1:0,Exprs:L:2:0|53=0|78=1:2
bit_select|81|2035|106|15|1|2|VpiConstantSelect:B:0:0,VpiIndex:O:1:0|53=0|78=1:-1
variables|77|0|0|-1|9|11|VpiName:S:8:0,VpiFullName:S:16:0,VpiArrayMember:B:0:0,VpiSigned:B:1:0,VpiAutomatic:B:2:0,VpiAllocScheme:I:24:0,VpiConstantVariable:B:3:0,VpiIsRandomized:B:4:0,VpiRandType:I:32:0,VpiStructUnionMember:B:5:0,VpiScalar:B:6:0,VpiVisibility:I:40:0,VpiVector:B:7:0,Instance:O:6:0,Scope:O:7:0,Expr:O:8:0,Path_term:F:48:2264,Tchk_term:F:56:2368,Module_inst:F:64:2229,Ports:L:1:0,Variable_drivers:L:2:0,Variable_loads:L:3:0,Prim_terms:M:4:2272,Cont_assigns:M:5:2082,Indexes:L:9:0,Attributes:M:10:2030|658=5,607=2,50=4,612=6,3=1,608=7,2=0,610=8,17=10,65=3,615=9,18=12,620=11|105=-1:25,8=-1:23,91=-1:20,102=15:-1,78=-1:24,745=13:-1,93=-1:21,32=18:-1,43=16:-1,98=-1:19,46=-1:22,84=14:-1,62=17:-1
hier_path|77|2163|5000|86|2|3|VpiName:S:0:0,VpiFullName:S:8:0,Root_value:O:2:0,Path_elems:L:1:0|3=1,2=0|700=-1:3,102=2:-1
ref_var|88|2305|5003|154|2|3|VpiDefName:S:0:0,VpiGeneric:B:64:0,VpiStructMember:B:65:0,Actual_group:O:1:0,Task_func:O:2:0|9=0,653=1,3013=2|700=3:-1,127=4:-1
short_real_var|88|2334|613|169|0|1|||
real_var|88|2299|47|150|0|1|||
byte_var|88|2041|614|20|0|1|||
short_int_var|88|2332|611|167|0|1|||
int_var|88|2186|612|105|0|1|||
long_int_var|88|2216|610|113|0|1|||
integer_var|88|2189|25|99|0|1|||
time_var|88|2377|63|191|0|1|||
array_var|88|2020|3010|8|1|6|VpiArrayType:I:0:0,Left_expr:O:3:0,Right_expr:O:4:0,Var_selects:M:1:2397,Ranges:M:2:2296,Variables:L:5:0|606=0|79=1:-1,115=-1:4,48=-1:5,83=2:-1,68=-1:3
array_expr|56|2016|5010|5|0|2|Exprs:L:1:0||102=-1:0
reg_array|0|2307|116|156|1|4|VpiIsMemory:B:0:0,Left_expr:O:1:0,Right_expr:O:2:0,Regs:M:3:2306|73=0|79=1:-1,30=-1:3,83=2:-1
reg|0|2306|48|155|0|4|Left_expr:O:1:0,Right_expr:O:2:0,Index:O:3:0||78=2:-1,79=0:-1,83=1:-1
packed_array_var|88|2257|623|133|1|7|VpiPackedArrayMember:B:0:0,VpiConstantSelect:B:1:0,VpiPacked:B:2:0,Left_expr:O:2:0,Right_expr:O:3:0,Expr_index:O:6:0,Ranges:M:1:2296,Var_bits:M:4:2395,Elements:L:5:0|53=1,630=2,655=0|90=-1:7,743=-1:8,78=5:-1,79=3:-1,115=-1:6,83=4:-1
bit_var|88|2037|620|17|0|5|Left_expr:O:2:0,Right_expr:O:3:0,Ranges:M:1:2296,Var_bits:M:4:2395||90=-1:3,79=0:-1,115=-1:2,83=1:-1
logic_var|88|2213|3009|111|0|5|Left_expr:O:2:0,Right_expr:O:3:0,Ranges:M:1:2296,Var_bits:M:4:2395||90=-1:3,79=0:-1,115=-1:2,83=1:-1
struct_var|88|2350|618|177|1|4|VpiPackedArrayMember:B:0:0,VpiConstantSelect:B:1:0,Expr_index:O:3:0,Var_bits:M:1:2395,Variables:L:2:0|53=1,655=0|90=-1:3,78=2:-1,742=-1:4
union_var|88|2389|619|198|1|4|VpiPackedArrayMember:B:0:0,VpiConstantSelect:B:1:0,Expr_index:O:3:0,Var_bits:M:1:2395,Variables:L:2:0|53=1,655=0|90=-1:3,78=2:-1,742=-1:4
enum_var|88|2118|617|62|1|2|VpiPackedArrayMember:B:0:0,VpiConstantSelect:B:1:0,Expr_index:O:1:0|53=1,655=0|78=2:-1
string_var|88|2346|616|173|0|1|||
chandle_var|88|2050|622|26|0|2|Actual_group:O:1:0||700=0:-1
var_bit|88|2395|3008|203|1|3|VpiConstantSelect:B:0:0,Expr_index:O:1:0,Expr_indexes:L:2:0|53=0|78=1:2
task_func|14|0|0|-1|6|7|VpiDPICIdentifier:S:32:0,VpiMethod:B:0:0,VpiAccessType:I:8:0,VpiVisibility:I:16:0,VpiVirtual:B:1:0,VpiAutomatic:B:2:0,VpiDPIPure:B:3:0,VpiDPIContext:B:4:0,VpiDPICStr:I:24:0,Left_range:O:1:0,Right_range:O:2:0,Return:O:3:0,Stmt:O:5:0,Instance:O:6:0,Class_defn:F:40:2064,Io_decls:M:4:2199|604=2,50=5,668=0,667=8,666=7,665=6,645=1,635=4,620=3|652=14:-1,28=-1:15,745=13:-1,79=9:-1,666=11:-1,83=10:-1,104=12:-1
task|112|2359|59|184|0|1|||
function|112|2146|20|75|3|1|VpiSigned:B:0:0,VpiSize:I:8:0,VpiFuncType:I:16:0|44=2,65=0,4=1|
modport|0|2225|606|117|2|2|VpiName:S:0:0,Interface_inst:F:8:2194,Io_decls:M:1:2199|2=0|28=-1:2,601=1:-1
interface_tf_decl|0|2195|607|102|1|3|VpiAccessType:U:0:0,Tasks:M:1:2359,Functions:M:2:2146|604=0|20=-1:2,59=-1:1
cont_assign|0|2082|8|45|4|5|VpiValue:S:24:0,VpiNetDeclAssign:B:0:0,VpiStrength0:I:8:0,VpiStrength1:I:16:0,Delay:O:1:0,Rhs:O:2:0,Lhs:O:3:0,Cont_assign_bits:M:4:2083|43=1,31=2,32=3|90=-1:7,72=4:-1,77=6:-1,82=5:-1
cont_assign_bit|0|2083|128|46|5|4|VpiValue:S:32:0,VpiOffset:I:0:0,VpiNetDeclAssign:B:64:0,VpiStrength0:I:16:0,VpiStrength1:I:24:0,Delay:O:1:0,Rhs:O:2:0,Lhs:O:3:0|43=2,60=1,31=3,32=4|72=5:-1,77=7:-1,82=6:-1
ports|0|0|0|-1|8|5|VpiName:S:8:0,VpiExplicitName:S:48:0,VpiPortIndex:I:0:0,VpiPortType:I:16:0,VpiScalar:B:192:0,VpiVector:B:193:0,VpiConnByName:B:194:0,VpiDirection:I:32:0,VpiSize:I:40:0,High_conn:O:1:0,Low_conn:O:2:0,Typespec:O:3:0,Instance:O:4:0,Module_inst:F:56:2229|21=6,20=7,19=1,2=0,29=2,611=3,17=4,4=8,18=5|76=9:-1,745=12:-1,80=10:-1,32=13:-1,725=11:-1
port|119|2268|44|138|0|3|Bits:M:1:2269,Attributes:M:2:2030||105=-1:1,90=-1:0
port_bit|119|2269|45|139|0|1|||
checker_port|119|2054|5008|30|2|2|Property:F:0:2287,Sequence:F:8:2325,Attributes:M:1:2030||105=-1:2,655=0:-1,661=1:-1
checker_inst_port|0|2053|5009|29|2|1|Property:F:0:2292,Sequence:F:8:2329||660=0:-1,664=1:-1
primitive|0|0|0|-1|9|5|VpiDefName:S:8:0,VpiDelay:S:16:0,VpiName:S:24:0,VpiFullName:S:32:0,VpiValue:S:64:0,VpiArrayMember:B:0:0,VpiPrimType:I:40:0,VpiStrength0:I:48:0,VpiStrength1:I:56:0,Delay:O:2:0,Index:O:3:0,Attributes:M:1:2030,Prim_terms:M:4:2272|607=5,9=0,3=3,2=2,33=6,31=7,32=8|105=-1:11,72=9:-1,78=10:-1,46=-1:12
gate|124|2148|21|76|0|1|||
switch_tran|124|2352|55|179|0|1|||
udp|124|2385|65|194|1|1|Udp_defn:F:0:2387||66=0:-1
mod_path|0|2223|31|116|0|2|Attributes:M:1:2030||105=-1:0
tchk|0|2365|61|186|6|4|VpiDelay:S:32:0,VpiTchkType:I:40:0,Expr:O:1:0,Module_inst:F:0:2229,Tchk_ref_term:F:8:2368,Tchk_data_term:F:16:2368,Reg:F:24:2306,Expr_tchk_terms:L:2:0,Attributes:M:3:2030|38=1|105=-1:8,72=2:-1,102=-1:7,32=3:-1,86=5:-1,87=6:-1,88=4:-1
range|0|2296|115|148|1|3|VpiSize:I:0:0,Left_expr:O:1:0,Right_expr:O:2:0|4=0|79=1:-1,83=2:-1
udp_defn|0|2387|66|196|5|4|VpiDefName:S:0:0,VpiSize:I:8:0,VpiProtected:B:128:0,VpiPrimType:I:24:0,Initial:F:32:2178,Attributes:M:1:2030,Io_decls:M:2:2199,Table_entrys:M:3:2356|9=0,33=3,10=2,4=1|105=-1:5,28=-1:6,24=4:-1,58=-1:7
table_entry|0|2356|58|182|2|2|VpiValue:S:8:0,VpiSize:I:0:0,Attributes:M:1:2030|4=1|105=-1:2
io_decl|0|2199|28|106|4|6|VpiName:S:8:0,VpiDirection:I:0:0,VpiScalar:B:128:0,VpiSigned:B:129:0,VpiSize:I:24:0,VpiVector:B:130:0,Expr:O:1:0,Left_expr:O:2:0,Right_expr:O:3:0,Typespec:O:5:0,Ranges:M:4:2296|20=1,2=0,17=2,65=3,4=4,18=5|102=6:-1,79=7:-1,115=-1:10,83=8:-1,725=9:-1
alias_stmt|0|2005|646|2|0|1|||
clocking_block|14|2064|650|37|7|3|VpiInputEdge:I:0:0,VpiOutputEdge:I:8:0,Instance:O:1:0,Input_skew:F:16:2094,Output_skew:F:24:2094,Clocking_event:F:32:2119,Prefix:F:40:2406,Actual:F:48:2064,Clocking_io_decls:M:2:2067|651=0,652=1|700=7:-1,690=5:-1,651=-1:8,706=3:-1,745=2:-1,707=4:-1,714=6:-1
clocking_io_decl|0|2067|651|38|6|2|VpiName:S:8:0,VpiDirection:I:0:0,VpiInputEdge:I:16:0,VpiOutputEdge:I:24:0,Expr:O:1:0,Input_skew:F:32:2094,Output_skew:F:40:2094|20=1,651=2,2=0,652=3|102=4:-1,706=5:-1,707=6:-1
param_assign|0|2258|40|134|1|4|VpiConnByName:B:0:0,VpiOverriden:B:1:0,Rhs:O:1:0,Lhs:O:2:0,Attributes:M:3:2030|21=0,5004=1|105=-1:4,77=3:-1,82=2:-1
instance_array|0|0|0|-1|3|9|VpiName:S:0:0,VpiFullName:S:8:0,VpiSize:I:16:0,Expr:O:1:0,Left_expr:O:3:0,Right_expr:O:4:0,Elem_typespec:O:7:0,Ranges:M:2:2296,Instances:L:5:0,Modules:M:6:2229,Ports:M:8:2268|3=1,2=0,4=2|704=6:-1,102=3:-1,745=-1:8,79=4:-1,32=-1:9,44=-1:10,115=-1:7,83=5:-1
interface_array|138|2191|603|100|0|2|Param_assigns:M:1:2258||40=-1:0
program_array|138|2281|604|142|0|1|||
module_array|138|2227|112|118|0|2|Param_assigns:M:1:2258||40=-1:0
primitive_array|138|0|0|-1|0|3|Delay:O:1:0,Primitives:L:2:0||72=0:-1,103=-1:1
gate_array|142|2149|111|77|0|1|||
switch_array|142|2351|117|178|0|1|||
udp_array|142|2386|118|195|0|1|||
typespec|0|0|0|-1|1|3|VpiName:S:0:0,Typedef_alias:O:1:0,Instance:O:2:0|2=0|745=2:-1,701=1:-1
net_drivers|0|0|0|-1|0|1|||
net_loads|0|0|0|-1|0|1|||
prim_term|0|2272|46|140|3|3|VpiValue:S:16:0,VpiDirection:I:0:0,VpiTermIndex:I:8:0,Expr:O:2:0,Attributes:M:1:2030|20=1,30=2|105=-1:4,102=3:-1
path_term|0|2264|43|137|0|2|Attributes:M:1:2030||105=-1:0
tchk_term|0|2368|62|187|1|3|VpiEdge:I:0:0,Expr:O:1:0,Condition:O:2:0|36=0|71=2:-1,102=1:-1
nets|77|0|0|-1|9|11|VpiName:S:8:0,VpiFullName:S:16:0,VpiArrayMember:B:0:0,VpiConstantSelect:B:1:0,VpiExpanded:B:2:0,VpiImplicitDecl:B:3:0,VpiNetDeclAssign:B:4:0,VpiNetType:I:24:0,VpiResolvedNetType:I:32:0,VpiScalar:B:5:0,VpiExplicitScalared:B:6:0,VpiSigned:B:7:0,VpiStrength0:I:40:0,VpiStrength1:I:48:0,VpiChargeStrength:I:56:0,VpiVector:B:8:0,VpiExplicitVectored:B:9:0,VpiStructUnionMember:B:10:0,Sim_net:O:10:0,Module_inst:F:64:2229,Ports:L:1:0,Drivers:L:2:0,Loads:L:3:0,Local_drivers:L:4:0,Local_loads:L:5:0,Prim_terms:M:6:2272,Cont_assigns:M:7:2082,Path_terms:M:8:2264,Tchk_terms:M:9:2368|607=2,27=14,53=3,25=4,23=10,24=16,3=1,26=5,2=0,43=6,22=7,61=8,17=9,65=11,31=12,32=13,615=17,18=15|8=-1:26,91=-1:21,93=-1:22,122=-1:23,123=-1:24,32=19:-1,43=-1:27,98=-1:20,46=-1:25,126=18:-1,62=-1:28
net_bit|152|2242|37|126|0|2|Exprs:L:1:0||78=-1:0
net|152|0|0|-1|0|4|Net_bits:M:1:2242,Attributes:M:2:2030,Exprs:L:3:0||105=-1:1,90=-1:0,78=-1:2
struct_net|154|2347|683|174|1|2|VpiPackedArrayMember:B:0:0,Nets:L:1:0|655=0|742=-1:1
enum_net|154|2113|680|60|1|1|VpiPackedArrayMember:B:0:0|655=0|
integer_net|154|2187|681|97|0|1|||
time_net|154|2375|682|189|0|1|||
logic_net|154|2211|36|109|0|4|Left_expr:O:1:0,Right_expr:O:2:0,Ranges:M:3:2296||79=0:-1,115=-1:2,83=1:-1
array_net|152|2017|114|6|0|4|Ranges:M:1:2296,Nets:L:2:0,Attributes:M:3:2030||105=-1:2,36=-1:1,115=-1:0
packed_array_net|154|2255|693|131|1|5|VpiPackedArrayMember:B:0:0,Left_expr:O:1:0,Right_expr:O:2:0,Ranges:M:3:2296,Elements:L:4:0|655=0|743=-1:4,79=1:-1,115=-1:3,83=2:-1
event_typespec|146|2121|698|65|0|1|||
named_event|0|2234|34|123|6|3|VpiName:S:8:0,VpiFullName:S:16:0,VpiEndLabel:S:40:0,VpiArrayMember:B:0:0,VpiAutomatic:B:1:0,VpiAllocScheme:I:24:0,Event_typespec:F:32:2304,Attributes:M:1:2030,Threads:M:2:2373|658=5,607=3,50=4,771=2,3=1,2=0|105=-1:7,605=6:-1,734=-1:8
named_event_array|0|2235|129|124|0|1|||
parameter|77|2260|41|135|5|5|VpiName:S:16:0,VpiFullName:S:24:0,VpiImported:S:32:0,VpiConstType:I:0:0,VpiSigned:B:64:0,VpiLocalParam:B:65:0,Expr:O:1:0,Left_range:O:3:0,Right_range:O:4:0,Ranges:M:2:2296|40=3,3=1,3014=2,70=5,2=0,65=4|102=6:-1,79=7:-1,115=-1:9,83=8:-1
def_param|0|2089|10|50|1|2|Rhs:O:1:0,Lhs:F:0:2260||77=1:-1,82=0:-1
spec_param|77|2341|54|171|0|2|Attributes:M:1:2030||105=-1:0
class_typespec|146|2058|630|33|4|10|VpiClassType:I:0:0,VpiAutomatic:B:64:0,Extends:F:16:2304,Class_defn:F:24:2055,Variables:L:1:0,Task_funcs:L:2:0,Constraints:M:3:2075,Parameters:L:4:0,Param_assigns:M:5:2258,Virtual_interface_vars:M:6:2406,Named_events:M:7:2234,Named_event_arrays:M:8:2235,Scopes:L:9:0|50=1,640=0|652=3:-1,653=-1:6,677=2:-1,92=-1:12,645=-1:5,34=-1:10,129=-1:11,40=-1:8,41=-1:7,100=-1:4,728=-1:9
extends|0|2138|677|67|1|2|Class_typespec:F:0:2304,Arguments:L:1:0||89=-1:1,630=0:-1
class_defn|14|2055|652|31|3|5|VpiEndLabel:S:16:0,VpiVirtual:B:0:0,VpiAutomatic:B:1:0,Extends:F:8:2138,Task_funcs:L:1:0,Constraints:M:2:2075,Deriveds:M:3:2055,Class_typespecs:M:4:2058|50=2,771=0,635=1|630=-1:7,653=-1:5,727=-1:6,677=3:-1,645=-1:4
class_obj|14|2057|621|32|2|5|VpiObjId:I:0:0,Class_typespec:F:8:2304,Threads:M:1:2373,Messages:L:2:0,Task_funcs:L:3:0,Constraints:M:4:2075|660=0|630=1:-1,653=-1:5,735=-1:3,127=-1:4,734=-1:2
class_var|88|2060|615|34|2|1|VpiObjId:I:0:0,Class_obj:F:8:2057|660=0|621=1:-1
instance|14|0|0|-1|13|10|VpiDefName:S:0:0,VpiDefFile:S:24:0,VpiLibrary:S:72:0,VpiCell:S:80:0,VpiConfig:S:88:0,VpiArrayMember:B:64:0,VpiCellInstance:B:65:0,VpiDefNetType:I:16:0,VpiDefLineNo:I:32:0,VpiDefDelayMode:I:40:0,VpiProtected:B:66:0,VpiTimePrecision:I:48:0,VpiTimeUnit:I:56:0,VpiUnconnDrive:I:64:0,VpiAutomatic:B:67:0,VpiTop:B:68:0,Instance:O:9:0,Module_inst:F:96:2229,Programs:M:1:2280,Program_arrays:M:2:2280,Class_defns:M:3:2055,Task_funcs:L:4:0,Nets:L:5:0,Array_nets:M:6:2017,Spec_params:M:7:2341,Assertions:L:8:0|607=5,50=14,51=3,8=6,52=4,47=9,15=1,16=8,9=0,13=7,58=2,10=10,12=11,11=12,600=15,14=13|114=-1:23,744=-1:25,652=-1:20,745=16:-1,32=17:-1,36=-1:22,602=-1:18,604=-1:19,54=-1:24,127=-1:21
interface_inst|173|2194|601|101|4|15|VpiEndLabel:S:24:0,VpiIndex:I:0:0,Expr_dist:O:3:0,Instance_array:O:4:0,Global_clocking:F:8:2064,Default_clocking:F:16:2064,Interface_tf_decls:M:1:2195,Modports:M:2:2225,Mod_paths:M:5:2223,Cont_assigns:M:6:2082,Clocking_blocks:M:7:2064,Interfaces:M:8:2194,Interface_arrays:M:9:2191,Process:L:10:0,Ports:M:11:2268,Gen_scope_arrays:M:12:2156,Elab_tasks:L:13:0,Gen_stmts:L:14:0|771=0,78=1|650=-1:10,8=-1:9,709=5:-1,710=2:-1,133=-1:15,5012=-1:17,708=4:-1,121=3:-1,601=-1:11,603=-1:12,607=-1:6,31=-1:8,606=-1:7,44=-1:14,99=-1:13,57=-1:16
program|173|2280|602|141|3|10|VpiEndLabel:S:16:0,VpiIndex:I:0:0,Instance_array:O:1:0,Expr_dist:O:3:0,Default_clocking:F:8:2064,Interfaces:M:2:2194,Interface_arrays:M:4:2191,Process:L:5:0,Cont_assigns:M:6:2082,Clocking_blocks:M:7:2064,Ports:M:8:2268,Gen_scope_arrays:M:9:2156|771=0,78=1|650=-1:9,8=-1:8,709=4:-1,710=3:-1,133=-1:11,121=2:-1,601=-1:5,603=-1:6,44=-1:10,99=-1:7
package|173|2254|600|130|2|1|VpiEndLabel:S:8:0,VpiUnit:B:0:0|771=0,602=1|
module_inst|173|2229|32|119|7|22|VpiEndLabel:S:48:0,VpiIndex:I:0:0,VpiTopModule:B:64:0,VpiDefDecayTime:I:16:0,Expr_dist:O:1:0,Instance_array:O:2:0,Global_clocking:F:24:2064,Default_clocking:F:32:2064,Module_array:F:40:2227,Ports:M:3:2268,Interfaces:M:4:2194,Interface_arrays:M:5:2191,Process:L:6:0,Modules:M:7:2229,Module_arrays:M:8:2227,Gen_scope_arrays:M:9:2156,Cont_assigns:M:10:2082,Primitives:L:11:0,Primitive_arrays:L:12:0,Mod_paths:M:13:2223,Tchks:M:14:2365,Def_params:M:15:2089,Io_decls:M:16:2199,Alias_stmts:M:17:2005,Clocking_blocks:M:18:2064,Elab_tasks:L:19:0,Ref_modules:M:20:2300,Gen_stmts:L:21:0|48=3,771=0,78=1,7=2|646=-1:23,650=-1:24,8=-1:16,10=-1:21,709=7:-1,710=4:-1,133=-1:15,5012=-1:27,708=6:-1,28=-1:22,121=5:-1,601=-1:10,603=-1:11,31=-1:19,32=-1:13,112=8:14,44=-1:9,103=-1:17,113=-1:18,99=-1:12,5011=-1:26,57=-1:25,61=-1:20
checker_decl|173|2051|5006|27|1|4|Default_clocking:F:0:2064,Process:L:1:0,Cont_assigns:M:2:2082,Ports:M:3:2054||8=-1:2,709=0:-1,44=-1:3,99=-1:1
checker_inst|173|2052|5007|28|0|2|Ports:M:1:2053||44=-1:0
short_real_typespec|146|2333|626|168|1|1|Resolution_func:F:0:2146||20=0:-1
real_typespec|146|2298|637|149|1|1|Resolution_func:F:0:2146||20=0:-1
byte_typespec|146|2040|627|19|1|1|VpiSigned:B:0:0|65=0|
short_int_typespec|146|2331|628|166|1|1|VpiSigned:B:0:0|65=0|
int_typespec|146|2185|629|104|2|3|VpiValue:S:0:0,VpiSigned:B:64:0,Cast_to_expr:O:1:0,Ranges:M:2:2296|65=1|102=2:-1,115=-1:3
long_int_typespec|146|2215|625|112|1|1|VpiSigned:B:0:0|65=0|
integer_typespec|146|2188|635|98|2|2|VpiValue:S:0:0,VpiSigned:B:64:0,Expr:O:1:0|65=1|102=2:-1
time_typespec|146|2376|636|190|1|1|VpiSigned:B:0:0|65=0|
enum_typespec|146|2117|633|61|0|3|Base_typespec:O:1:0,Enum_consts:M:2:2111||703=0:-1,634=-1:1
string_typespec|146|2345|631|172|0|1|||
chandle_typespec|146|2049|632|25|0|1|||
module_typespec|146|2230|768|120|0|1|||
struct_typespec|146|2349|638|176|2|2|VpiPacked:B:0:0,Resolution_func:F:8:2146,Members:M:1:2383|630=0|20=1:-1,644=-1:2
union_typespec|146|2388|639|197|1|2|VpiPacked:B:0:0,VpiTagged:B:1:0,Members:M:1:2383|630=0,632=1|644=-1:2
logic_typespec|146|2212|641|110|3|5|VpiVector:B:0:0,VpiSigned:B:1:0,Left_expr:O:2:0,Right_expr:O:3:0,Index_typespec:O:4:0,Elem_typespec:F:8:2304,Resolution_func:F:16:2146,Ranges:M:1:2296|65=1,18=0|704=5:-1,20=6:-1,702=4:-1,79=2:-1,115=-1:7,83=3:-1
packed_array_typespec|146|2256|692|132|2|6|VpiVector:B:0:0,Left_expr:O:2:0,Right_expr:O:3:0,Elem_typespec:O:4:0,Typespec:O:5:0,Resolution_func:F:8:2146,Ranges:M:1:2296|18=0|704=3:-1,20=5:-1,702=4:-1,79=1:-1,115=-1:6,83=2:-1
array_typespec|146|2019|642|7|2|6|VpiArrayType:I:0:0,Left_expr:O:2:0,Right_expr:O:3:0,Index_typespec:O:4:0,Elem_typespec:O:5:0,Resolution_func:F:8:2146,Ranges:M:1:2296|606=0|704=4:-1,20=5:-1,702=3:-1,79=1:-1,115=-1:6,83=2:-1
void_typespec|146|2408|643|206|0|1|||
unsupported_typespec|146|2392|4002|201|1|2|VpiPacked:B:0:0,Ranges:M:1:2296|630=0|115=-1:1
sequence_typespec|146|2330|696|165|0|1|||
property_typespec|146|2295|697|146|0|1|||
interface_typespec|146|2197|3001|103|2|2|VpiDefName:S:0:0,VpiIsModPort:B:64:0,Param_assigns:M:1:2258|9=0,3007=1|40=-1:2
type_parameter|146|2380|609|192|3|3|VpiFullName:S:8:0,VpiImported:S:16:0,VpiLocalParam:B:0:0,Typespec:O:1:0,Expr:O:2:0|3=0,3014=1,70=2|102=4:-1,605=3:-1
typespec_member|0|2383|644|193|7|4|VpiName:S:0:0,VpiRefFile:S:16:0,VpiRandType:B:64:0,VpiRefLineNo:I:24:0,VpiRefColumnNo:I:32:0,VpiRefEndLineNo:I:40:0,VpiRefEndColumnNo:I:48:0,Typespec:O:1:0,Default_value:O:2:0,Actual_value:O:3:0|2=0,610=2,3020=4,3022=6,3021=5,3018=1,3019=3|102=9:-1,605=7:-1
enum_const|0|2111|634|59|4|1|VpiName:S:0:0,VpiValue:S:8:0,VpiDecompile:S:16:0,VpiSize:I:24:0|54=2,2=0,4=3|
bit_typespec|146|2036|640|16|3|5|VpiVector:B:0:0,VpiSigned:B:1:0,Left_expr:O:2:0,Right_expr:O:3:0,Typespec:O:4:0,Bit_typespec:F:8:2304,Resolution_func:F:16:2146,Ranges:M:1:2296|65=1,18=0|704=5:-1,20=6:-1,702=4:-1,79=2:-1,115=-1:7,83=3:-1
tf_call|56|0|0|-1|1|3|VpiName:S:0:0,Scope:O:1:0,Tf_call_args:L:2:0|2=0|89=-1:2,84=1:-1
user_systf|0|2393|67|202|0|1|||
sys_func_call|206|2353|56|180|3|1|VpiFuncType:I:0:0,VpiUserDefn:B:64:0,User_systf:F:16:2393|44=0,45=1|67=2:-1
sys_task_call|206|2355|57|181|2|1|VpiUserDefn:B:0:0,User_systf:F:8:2393|45=0|67=1:-1
method_func_call|206|2220|648|114|2|3|VpiUserDefn:B:0:0,Prefix:O:1:0,With:O:2:0,Function:F:8:2146|45=0|20=3:-1,714=1:-1,715=2:-1
method_task_call|206|2222|649|115|2|3|VpiUserDefn:B:0:0,Prefix:O:1:0,With:O:2:0,Task:F:8:2359|45=0|714=1:-1,59=3:-1,715=2:-1
func_call|206|2145|19|74|2|1|VpiFuncType:I:0:0,Function:F:8:2146|44=0|20=1:-1
task_call|206|2360|60|185|1|1|Task:F:0:2359||59=0:-1
constraint_expr|0|0|0|-1|0|1|||
constraint_ordering|0|2080|654|41|0|3|Solve_befores:L:1:0,Solve_afters:L:2:0||732=-1:1,731=-1:0
constraint|0|2075|653|40|5|3|VpiName:S:0:0,VpiFullName:S:8:0,VpiVirtual:B:128:0,VpiAutomatic:B:129:0,VpiAllocScheme:I:24:0,VpiAccessType:I:32:0,VpiIsConstraintEnabled:B:130:0,Attributes:M:1:2030,Constraint_items:L:2:0|604=5,658=4,50=3,3=1,638=6,2=0,635=2|105=-1:7,746=-1:8
import_typespec|146|2171|2171|93|1|1|Item:F:0:2071||726=0:-1
dist_item|0|2101|645|56|1|3|VpiDistType:I:0:0,Value_range:O:1:0,Weight:O:2:0|625=0|720=1:-1,722=2:-1
distribution|214|2103|678|57|1|3|VpiSoft:B:0:0,Expr:O:2:0,Dist_items:M:1:2101|639=0|645=-1:2,102=1:-1
implication|214|2170|749|92|0|3|Condition:O:1:0,Constraint_exprs:L:2:0||71=0:-1,747=-1:1
constr_if|214|2073|738|43|0|3|Condition:O:1:0,Constraint_exprs:L:2:0||71=0:-1,747=-1:1
constr_if_else|214|2074|739|44|0|4|Condition:O:1:0,Constraint_exprs:L:2:0,Else_constraint_exprs:L:3:0||71=0:-1,747=-1:1,748=-1:2
constr_foreach|214|2072|736|42|0|4|Variable:O:1:0,VpiLoopVars:L:2:0,Constraint_exprs:L:3:0||747=-1:2,737=-1:1,100=0:-1
soft_disable|214|2338|3006|170|0|2|Expr:O:1:0||102=0:-1
gen_stmt|0|0|0|-1|1|2|VpiName:S:0:0,Attributes:M:1:2030|2=0|105=-1:1
gen_if|225|2152|5013|80|0|3|VpiCondition:O:1:0,VpiStmt:O:2:0||71=0:-1,104=1:-1
gen_if_else|225|2153|5014|81|0|4|VpiCondition:O:1:0,VpiStmt:O:2:0,VpiElseStmt:O:3:0||71=0:-1,73=2:-1,104=1:-1
gen_for|225|2151|15|79|1|7|VpiLocalVarDecls:I:0:0,VpiForInitStmt:O:3:0,VpiCondition:O:4:0,VpiForIncStmt:O:5:0,VpiStmt:O:6:0,VpiForInitStmts:L:1:0,VpiForIncStmts:L:2:0|609=0|71=2:-1,74=3:6,75=1:5,104=4:-1
gen_case|225|2150|5016|78|0|3|VpiCondition:O:1:0,Case_items:M:2:2042||6=-1:1,71=0:-1
gen_region|225|2154|5017|82|0|2|VpiStmt:O:1:0||104=0:-1
design|0|2097|3000|53|2|15|VpiName:S:8:0,VpiElaborated:B:0:0,Include_file_infos:M:1:2172,AllPackages:M:2:2254,TopPackages:M:3:2254,AllClasses:M:4:2055,AllInterfaces:M:5:2194,AllUdps:M:6:2387,AllPrograms:M:7:2280,AllModules:M:8:2229,Typespecs:L:9:0,Let_decls:M:10:2204,Task_funcs:L:11:0,Parameters:L:12:0,Param_assigns:M:13:2258,TopModules:M:14:2229|5002=1,2=0|2007=-1:5,2008=-1:6,2009=-1:9,2010=-1:3,2011=-1:8,2012=-1:7,2378=-1:15,2379=-1:4,3023=-1:2,903=-1:11,40=-1:14,41=-1:13,127=-1:12,725=-1:10
""";
}
