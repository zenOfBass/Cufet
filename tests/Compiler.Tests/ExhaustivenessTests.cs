using System.Reflection;
using Cufet.Interpreter;
using Xunit;

namespace Cufet.Compiler.Tests;

// Three bugs in two days had one shape: a hand-written switch over AST node types or CufetType
// cases, missing an arm, whose default returned a plausible wrong answer instead of failing.
// `Judge` missing from the compiler's interrupt search; `bits` missing from nine per-type
// switches; `Define object` inside a book pull missing from the object collector.
//
// Two of the three were invisible until a user wrote the program that hit them. These tests make
// the SHAPE fail instead — the point is not to test today's types but to fail the day a new one
// is added and someone forgets a walk.
public class ExhaustivenessTests
{
    // One instance of every CufetType. Written by hand rather than reflected into existence
    // because several need a payload — and that is the feature: a NEW CufetType has no entry
    // here, so EveryCufetTypeHasAFactory fails and whoever added it is told to come here first.
    private static readonly Dictionary<Type, CufetType> Instances = new()
    {
        [typeof(NumberType)]          = CufetType.Number,
        [typeof(BitsType)]            = new BitsType(),
        [typeof(TextType)]            = CufetType.Text,
        [typeof(FactType)]            = CufetType.Fact,
        [typeof(VoidType)]            = new VoidType(),
        [typeof(MatrixType)]          = MatrixType.Instance,
        [typeof(RabbitType)]          = new RabbitType(),
        [typeof(MappingType)]         = new MappingType(CufetType.Text, CufetType.Number),
        [typeof(FailureMarkerType)]   = new FailureMarkerType(),
        [typeof(ExceptionMarkerType)] = new ExceptionMarkerType(),
        [typeof(SeriesType)]          = new SeriesType(CufetType.Number),
        [typeof(VoidableType)]        = new VoidableType(CufetType.Number),
        [typeof(FailureType)]         = new FailureType(CufetType.Number),
        [typeof(ChannelType)]         = new ChannelType(CufetType.Number),
        [typeof(ReadableStreamType)]  = new ReadableStreamType(CufetType.Text),
        [typeof(WritableStreamType)]  = new WritableStreamType(CufetType.Text),
        [typeof(MapType)]             = new MapType(CufetType.Text, CufetType.Number),
        [typeof(RecordType)]          = new RecordType([CufetType.Number], []),
        [typeof(ObjectType)]          = new ObjectType("thing", [], [], []),
        [typeof(InterfaceType)]       = new InterfaceType("shape"),
        [typeof(FunctionType)]        = new FunctionType([CufetType.Number], CufetType.Number),
        [typeof(TaskHandleType)]      = new TaskHandleType(CufetType.Number),
        [typeof(BookType)]            = new BookType("math", []),
        [typeof(UnionType)]           = new UnionType([CufetType.Number, CufetType.Text]),
    };

    private static IEnumerable<Type> AllCufetTypes() =>
        typeof(CufetType).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(CufetType)) && !t.IsAbstract)
            .OrderBy(t => t.Name);

    [Fact]
    public void EveryCufetType_HasAFactory()
    {
        // The gate on the two tests below: they can only be as complete as this table is.
        var missing = AllCufetTypes().Where(t => !Instances.ContainsKey(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0,
            $"New CufetType(s) with no entry in ExhaustivenessTests.Instances: {string.Join(", ", missing)}. " +
            "Add one, then check every per-type switch names it — that omission is what shipped " +
            "'printing a value is not yet supported'.");
    }

    [Fact]
    public void EveryCufetType_HasAName()
    {
        // ★ The single assertion that would have caught the `bits` bug at the moment it was most
        // visible: the error message said "a 'value'", which IS this fallback.
        var format = typeof(CodeGenerator).GetMethod(
            "FormatTypeName", BindingFlags.NonPublic | BindingFlags.Static)!;

        var unnamed = new List<string>();
        foreach (var (clr, instance) in Instances)
        {
            var name = (string)format.Invoke(null, [instance])!;
            if (name == "value") unnamed.Add(clr.Name);
        }

        Assert.True(unnamed.Count == 0,
            $"CodeGenerator.FormatTypeName falls through to \"value\" for: {string.Join(", ", unnamed)}. " +
            "A refusal that cannot name the type it is refusing sends the reader looking in the " +
            "wrong place.");
    }

    // ── The node side ─────────────────────────────────────────────────────
    //
    // The three bugs were all one thing: a new construct was added, and a hand-written walk that
    // descends into statement bodies was not told about it. `Judge` for the interrupt search,
    // `Pull a book on` for the object collector.
    //
    // ★ This does NOT try to judge today's omissions. Several are deliberate — InferBodyReturnType
    // stops at a nested BindStatement on purpose, because a nested function's returns are not the
    // outer one's. Auditing ~60 walk-by-node cells is a separate job needing a reason per cell.
    //
    // What it does is close the door the three bugs came through: a body-bearing node type that
    // NOBODY has considered cannot appear silently. Adding one fails this test, and the failure
    // says where to look.
    private static readonly HashSet<string> KnownBodyBearingNodes =
    [
        "BindStatement", "ConditionArm", "ForEachFromInputStatement", "ForEachStatement",
        "GetterDeclaration", "IfStatement", "JudgeArm", "JudgeStatement", "LambdaLiteral",
        "LaunchTaskStatement", "OperatorOverloadDeclaration", "PullRabbitStatement",
        "PullStatement", "RepeatUntilStatement", "SetterDeclaration", "TryStatement",
        "UnmakerDeclaration", "WhileStatement", "WithOpenStatement",
    ];

    // The walks that descend by hand and must therefore be revisited. Everything else either uses
    // AstSearch (no list to fall behind) or throws on an unknown node (fails loudly on first use).
    private const string HandWrittenDescents =
        "CodeGenerator: AnalyzePipes, InferStageOutput, InferTaskResultType, InferBodyReturnType; " +
        "SemanticTokens: Walk (statements and expressions); Linter: ChildBlocks; " +
        "TypeChecker.Functions: WalkBodyForReturnDepths, SymbolicExprDepth";

    [Fact]
    public void EveryBodyBearingNode_HasBeenConsidered()
    {
        var found = typeof(Program).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && (typeof(IStatement).IsAssignableFrom(t)
                                       || typeof(IExpression).IsAssignableFrom(t)
                                       || t.Name is "ConditionArm" or "JudgeArm"))
            .Where(t => t.GetProperties().Any(p =>
                p.PropertyType.IsGenericType &&
                p.PropertyType.GetGenericArguments()[0].Name is "IStatement" or "ConditionArm" or "JudgeArm"))
            .Select(t => t.Name)
            .ToHashSet();

        var added = found.Except(KnownBodyBearingNodes).OrderBy(n => n).ToList();
        Assert.True(added.Count == 0,
            $"New body-bearing AST node type(s): {string.Join(", ", added)}.\n" +
            $"Every hand-written descent has to learn about it, or it will silently see nothing " +
            $"inside it — which is exactly how `Judge` came to compile without a signal substrate " +
            $"and how `Define object` inside a book pull crashed the compiler.\n" +
            $"Check: {HandWrittenDescents}.\n" +
            $"Then add the node here.");

        var removed = KnownBodyBearingNodes.Except(found).OrderBy(n => n).ToList();
        Assert.True(removed.Count == 0,
            $"Body-bearing node type(s) gone from the AST: {string.Join(", ", removed)}. " +
            "Remove them from KnownBodyBearingNodes so this list keeps meaning something.");
    }

    [Fact]
    public void EveryCufetType_IsNamedByTheFrontEndToo()
    {
        // The other half of the pair. Both backends' messages must be able to say what a type IS,
        // and they should agree that a name exists — they are describing one language.
        var format = typeof(TypeChecker).GetMethod(
            "FormatType", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(format);

        var unnamed = Instances
            .Where(kv => (string)format!.Invoke(null, [kv.Value])! == "<unknown>")
            .Select(kv => kv.Key.Name)
            .ToList();

        Assert.True(unnamed.Count == 0,
            $"TypeChecker.FormatType falls through to \"<unknown>\" for: {string.Join(", ", unnamed)}.");
    }

    // ── Diagnostics must not leak internal vocabulary ────────────────────
    //
    // The ROADMAP asked for "a periodic error-message audit for internal vocabulary". Periodic
    // means someone has to remember; this makes it continuous instead.
    //
    // The failure is real and shipped once: awaiting inside a task reported `'TaskHandleType' is
    // not yet supported by the compiler (slice 5B: records + objects + text)` — a C# class name,
    // this project's internal slice numbering, and a feature list with nothing to do with it. The
    // most recent sweep found `"Open unions are the CAT.2 slice."`, which was worse than jargon:
    // it also described a SHIPPED feature as missing.
    //
    // Scoped to prose deliberately. Emitted C is full of `cufet_*` and `cv_*` and should be — it
    // is code, not a sentence — so a string only counts if it reads like one.
    [Fact]
    public void UserFacingMessages_DoNotLeakInternalVocabulary()
    {
        var srcRoot = FindRepoRoot();
        var codey = new System.Text.RegularExpressions.Regex(
            @"[;{}]|->|\*\)|\(\)|#include|static |return |\+\+|==|!=|&&|\|\||%s|%d|\bint \b");
        var banned = new (string Label, System.Text.RegularExpressions.Regex Pattern)[]
        {
            ("internal slice/arc code", new(@"\b(slice\s*\d+[A-Z]?|Arc\s*\d+[A-Z]?|CONC\.[A-Z]|ESC\.\d[a-z]?|CAT\.\d|TCAP|UNMK|DD\.\d|ISA\.\d[a-z]?|INT\.\d|CL\.\d)\b")),
            ("C# type name",           new(@"\b[A-Z][A-Za-z]*(?:Statement|Expression|Marker|Struct)\b")),
            ("emitted-C identifier",   new(@"\b(?:cv_|cf_|cun_|cchan_|cser_|cvd_)\w+")),
            ("private field name",     new(@"\b_[a-z]\w{3,}")),
        };

        var offenders = new List<string>();
        foreach (var dir in new[] { "src/Lexer", "src/Interpreter", "src/Compiler", "src/App" })
        foreach (var file in Directory.GetFiles(Path.Combine(srcRoot, dir), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//") || t.StartsWith("*")) continue;
                foreach (System.Text.RegularExpressions.Match lit in
                         System.Text.RegularExpressions.Regex.Matches(lines[i], "\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    // Interpolation holes hold expressions, not prose the message ships.
                    var body = System.Text.RegularExpressions.Regex.Replace(lit.Groups[1].Value, @"\{[^{}]*\}", "");
                    if (body.Length < 25 || body.Split(' ').Length < 5) continue;
                    if (codey.IsMatch(body)) continue;
                    foreach (var (label, pattern) in banned)
                        if (pattern.Match(body) is { Success: true } m)
                            offenders.Add($"{Path.GetFileName(file)}:{i + 1} [{label}: {m.Value}] {body.Trim()[..Math.Min(90, body.Trim().Length)]}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "these user-facing messages contain vocabulary only a compiler author knows:\n  "
            + string.Join("\n  ", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cufet.sln"))) dir = dir.Parent;
        return dir?.FullName ?? "";
    }
}
