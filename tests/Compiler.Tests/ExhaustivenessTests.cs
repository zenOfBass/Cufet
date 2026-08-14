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
        [typeof(StashType)]           = new StashType(CufetType.Number),
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
            "Insert one, then check every per-type switch names it — that omission is what shipped " +
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

    // ── The reflection walks must not gate descent on the interfaces ──────
    //
    // The most-repeated bug in this codebase, seven times over: a generic walk that descends with
    //
    //     if (child is IExpression or IStatement) Recurse(child);
    //
    // `ConditionArm` (every `If`/`Otherwise` arm) and `JudgeArm` implement NEITHER interface. They
    // are plain records that HOLD statements, so that gate steps straight over the condition and
    // the body of every `If` and every judgement. It reads as complete, which is why it kept being
    // written — asking "is this an AST node?" as a disjunction of the two interfaces is simply the
    // wrong question, and the namespace is the right one.
    //
    // Two of the seven shipped as live divergences: a task capturing a variable used only inside an
    // `If` arm emitted `cv_<name> undeclared`, and a capture-WRITE one `If` deep escaped the refusal
    // entirely — `check --native` reported no problems while the interpreter printed 5 and the
    // compiled binary printed 0.
    //
    // The behavioural tests for those two live in PipelineMemoryTests and PipelineEscapeTests, and
    // they only cover walks that already exist. This is the part they cannot do: fail on the SEVENTH
    // walk, written the same wrong way, before anyone runs a program through it.
    //
    // Gating on nothing at all is fine and two walks do it — descending into everything only ever
    // over-approximates, and over-approximating is the safe direction here.
    private static readonly HashSet<string> KnownReflectionWalks =
    [
        "Ast.cs: Contains",
        "CodeGenerator.cs: ProgramUsesOpenUnion",
        "CodeGenerator.cs: TaskBodyMayMutate",
        "CodeGenerator.cs: IsBoundSomewhere",
        "CodeGenerator.cs: CaptureWriteIsObservable",
        "CodeGenerator.cs: CollectRefsDefs",
        // Proof it sees inside ConditionArm and JudgeArm: Interpreter.Tests/StashDetectionTests.
        // Both arm tests were shown RED by keying the walk on IStatement/IExpression instead of the
        // namespace. (The Otherwise test in that file stayed green under the same break and is
        // labelled there as not discriminating — an `Otherwise` body is an ordinary property.)
        "TypeChecker.Core.cs: BuriesInOwnBody",
    ];

    // A generic AST walk's fingerprint: it asks an unknown node for its properties.
    private const string WalkFingerprint = "GetType().GetProperties()";

    private static readonly System.Text.RegularExpressions.Regex MemberDecl = new(
        @"^    (?:(?:public|private|internal|protected|static|sealed|override|virtual|partial|async|unsafe|new)\s+)+[\w<>,\[\]\?\.]+\s+(\w+)\s*(?:<[^>]*>)?\s*\(");

    // `x is IStatement`, `x is not (IExpression or IStatement)`, and everything between.
    private static readonly System.Text.RegularExpressions.Regex InterfaceGate = new(
        @"\bis\s+(?:not\s+)?\(?\s*(?:IExpression|IStatement)\b(?:\s*or\s*(?:IExpression|IStatement)\b)?");

    // The disjunction on its own — banned everywhere, walk or not. "Is this an expression or a
    // statement?" has exactly one meaning, "is this an AST node?", and exactly one correct spelling.
    private static readonly System.Text.RegularExpressions.Regex EitherInterface = new(
        @"\bis\s+(?:not\s+)?\(?\s*(?:IExpression|IStatement)\s+or\s+(?:IExpression|IStatement)\b");

    // Every member that contains the walk fingerprint, as (label, first line, last line).
    private static List<(string Label, string File, int Start, int End, string[] Lines)> FindWalks()
    {
        var walks = new List<(string, string, int, int, string[])>();
        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);

            // Member boundaries by declaration line at class indentation. Deliberately NOT by
            // brace matching: CodeGenerator.cs emits C, so its string literals are full of braces.
            var decls = new List<(int Line, string Name)>();
            for (int i = 0; i < lines.Length; i++)
                if (MemberDecl.Match(lines[i]) is { Success: true } m) decls.Add((i, m.Groups[1].Value));

            for (int d = 0; d < decls.Count; d++)
            {
                int start = decls[d].Line;
                int end = d + 1 < decls.Count ? decls[d + 1].Line - 1 : lines.Length - 1;
                bool isWalk = false;
                for (int i = start; i <= end && !isWalk; i++)
                    isWalk = lines[i].Contains(WalkFingerprint);
                if (isWalk)
                    walks.Add(($"{Path.GetFileName(file)}: {decls[d].Name}", file, start, end, lines));
            }
        }
        return walks;
    }

    [Fact]
    public void ReflectionWalks_DoNotGateDescentOnTheInterfaces()
    {
        var offenders = new List<string>();

        foreach (var (label, _, start, end, lines) in FindWalks())
            for (int i = start; i <= end; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//") || t.StartsWith("*")) continue;   // the comments SAY the rule
                if (InterfaceGate.Match(lines[i]) is { Success: true } m)
                    offenders.Add($"{label} (line {i + 1}) [{m.Value.Trim()}] {t}");
            }

        Assert.True(offenders.Count == 0,
            "a reflection walk over the AST gates its descent on IExpression/IStatement:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nConditionArm and JudgeArm implement neither, so this walks past the condition and "
            + "body of every `If` arm and every judgement. Gate on the namespace instead — "
            + "`node.GetType().Namespace == typeof(IStatement).Namespace` — or on nothing at all.");

        // The same shape outside a walk is still the wrong question, so it is banned everywhere.
        var elsewhere = new List<string>();
        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//") || t.StartsWith("*")) continue;
                if (EitherInterface.IsMatch(lines[i]))
                    elsewhere.Add($"{Path.GetFileName(file)}:{i + 1} {t}");
            }
        }

        Assert.True(elsewhere.Count == 0,
            "`is IExpression or IStatement` asks \"is this an AST node?\" and gets the answer wrong "
            + "for ConditionArm and JudgeArm. Ask the namespace:\n  " + string.Join("\n  ", elsewhere));
    }

    [Fact]
    public void EveryReflectionWalk_IsAccountedFor()
    {
        var found = FindWalks().Select(w => w.Label).ToHashSet();

        var added = found.Except(KnownReflectionWalks).OrderBy(n => n).ToList();
        Assert.True(added.Count == 0,
            $"New generic AST walk(s): {string.Join(", ", added)}.\n" +
            "A walk that descends by reflection must be shown to see INSIDE `ConditionArm` and " +
            "`JudgeArm` — the two AST records that implement neither IExpression nor IStatement. " +
            "Write a test where the thing the walk is looking for appears ONLY inside an `If` arm " +
            "body and ONLY inside a `Judge` arm body (a first attempt at one of these captured the " +
            "Judge's Subject, which is an ordinary property, and passed with the bug reverted).\n" +
            "Then add the walk here.");

        var removed = KnownReflectionWalks.Except(found).OrderBy(n => n).ToList();
        Assert.True(removed.Count == 0,
            $"Reflection walk(s) gone: {string.Join(", ", removed)}. Remove them from " +
            "KnownReflectionWalks so this list keeps meaning something.");
    }

    // Every hand-written source file of the front end and both backends. `obj/` and `bin/` are
    // excluded: they hold generated files (GlobalUsings, AssemblyInfo) that nobody wrote and
    // nobody can fix.
    private static IEnumerable<string> SourceFiles()
    {
        var root = FindRepoRoot();
        foreach (var dir in new[] { "src/Lexer", "src/Interpreter", "src/Compiler", "src/App" })
        foreach (var file in Directory.GetFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            yield return file;
        }
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
        foreach (var file in SourceFiles())
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

    // ── Every launcher of a compiled binary must close its stdin ─────────
    //
    // Six places in this test project start a compiled Cufet binary, each with its own
    // hand-rolled ProcessStartInfo. Five of them did not redirect stdin, so the child inherited
    // the TEST HOST's — and what that is depends on how the suite was launched. Under
    // `dotnet test` on Linux it is a pipe somebody still holds open, so a program that reads
    // input blocks on read() forever.
    //
    // Measured: a single binary sat in `pipe_read` for 2h15m having used ZERO CPU, with the
    // whole suite waiting on it. It never showed on Windows (the inherited handle gives EOF) and
    // never showed in CI or the mutation harness (both redirect from /dev/null). It took running
    // the suite interactively through wsl.exe — the one launch that supplies a live pipe.
    //
    // ★ Patching five by hand is the same shape as the AST-walk bug this file already guards:
    // one rule, N copies, one forgotten, silent when wrong. So it gets the same treatment.
    [Fact]
    public void EveryCompiledBinaryLauncher_ClosesStdin()
    {
        var offenders = new List<string>();
        var root = FindRepoRoot();

        foreach (var file in Directory.GetFiles(
                     Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            var lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                // Keyed on `binPath` deliberately: this is about launching a COMPILED CUFET
                // PROGRAM. `new ProcessStartInfo("/bin/kill", …)` in the interrupt runner is not
                // one and must not be dragged in.
                if (!lines[i].Contains("new ProcessStartInfo(binPath)")) continue;

                var window = string.Join("\n", lines.Skip(i).Take(20));
                if (!window.Contains("RedirectStandardInput"))
                    offenders.Add($"{rel}:{i + 1} does not set RedirectStandardInput");
                else if (!window.Contains("StandardInput.Close()"))
                    offenders.Add($"{rel}:{i + 1} redirects stdin but never closes it");
            }
        }

        Assert.True(offenders.Count == 0,
            "a compiled Cufet binary is launched without a closed stdin:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nRedirect stdin and close it immediately. The close is what turns a read into "
            + "EOF, which is what the interpreter gives a program whose reader is null — so both "
            + "backends agree by construction instead of by accident. Inheriting the test host's "
            + "stdin instead wedges the whole suite for hours, and only on Linux, and only when "
            + "launched with a live pipe.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cufet.sln"))) dir = dir.Parent;
        return dir?.FullName ?? "";
    }
}
