using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        [typeof(ChaseType)]           = ChaseType.Instance,
        [typeof(RabbitType)]          = new RabbitType(),
        [typeof(AddressType)]         = AddressType.Instance,
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
        [typeof(AxiomType)]           = new AxiomType("c-language"),
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

    // ── The per-type behavioural switches ───────────────────────────
    //
    // ★★ The half of this job that was deferred when these tests were written. The entry then said
    // the tests "close the door new constructs came through rather than judging the existing cells,
    // which is a separate job needing a reason per cell" — and a new construct came through the
    // door anyway: AxiomType got its FormatTypeName arm, which IS checked here, and silently did
    // not get its EmitStructs arm, which was not. A probe found that, not the suite.
    //
    // ★ The table below is the reason per cell. Every type is run through every switch and the
    // outcome recorded as supported or refused; a refusal not listed here fails, and so does a
    // listed refusal that starts succeeding. That is what turns "this type is not handled" from an
    // oversight into a decision somebody wrote down.
    //
    // ⚠ What this CANNOT check is whether a supported arm is CORRECT — only that one exists. The
    // AddressType arm that shipped printing a raw pointer would have passed this happily. It closes
    // the omission class, which is the one that recurs.
    //
    // ⚠⚠ And it reads a THROW as "unhandled", so a switch whose fallback returns a plausible
    // answer is invisible to it however many types it forgets. DepStructName was exactly that — a
    // local function ending in `_ => null`, where null means "depends on nothing" — and it had to
    // be extracted and made throwing before it could be listed here. Any new per-type switch has
    // to do the same to be worth auditing.

    /// <summary>A per-type switch, by the name a reader would look for it under.</summary>
    private sealed record Switch(string Name, Func<CodeGenerator, CufetType, string> Invoke);

    private static CodeGenerator FreshGenerator() =>
        (CodeGenerator)Activator.CreateInstance(typeof(CodeGenerator), nonPublic: true)!;

    private static string CallPrivate(CodeGenerator gen, string method, params object?[] args)
    {
        var m = typeof(CodeGenerator).GetMethod(method,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"CodeGenerator.{method} is gone or renamed — this audit is checking nothing.");
        try { return (string)m.Invoke(m.IsStatic ? null : gen, args)!; }
        catch (TargetInvocationException e) when (e.InnerException is not null) { throw e.InnerException; }
    }

    private static readonly Switch[] PerTypeSwitches =
    [
        // ⚠ The ENTRY POINTS, not the Raw switches behind them. Both apply NoStashes on the way in,
        // so calling Raw would audit a path no caller takes — and would report `stash of T` as
        // unhandled when the rewrite handles it before the switch is ever reached.
        new("EmitCType",     (g, t) => CallPrivate(g, "EmitCType", t)),
        new("TypeSig",       (g, t) => CallPrivate(g, "TypeSig", t)),
        new("FormatTypeName",(g, t) => CallPrivate(g, "FormatTypeName", t)),
        new("EqCall",        (g, t) => CallPrivate(g, "EqCall", "a", "b", t)),
        new("WriteCall",     (g, t) => CallPrivate(g, "WriteCall", "v", t)),
        // The switch that motivated this audit and that the audit could not originally see: it was
        // a LOCAL function inside EmitStructs, and reflection cannot reach one. Extracted, and its
        // `_ => null` fallback replaced with a throw, so a missing arm is a failure here rather
        // than a plausible "depends on nothing".
        new("DepStructName", (g, t) => CallPrivate(g, "DepStructName", t)),
    ];

    /// <summary>Refusals that are DELIBERATE, each with the reason it is one.</summary>
    /// <remarks>
    /// ⚠ A reason is required. "It throws today" is not one — the whole point of the table is that
    /// somebody decided, so an entry that cannot say why is an omission wearing a disguise.
    /// </remarks>
    private static readonly Dictionary<(string Switch, Type Type), string> DeliberateRefusals = new()
    {
        // Nothing holds a `void`: it is the absence of a value, and `voidable T` is how absence is
        // carried. A C declaration for it would be a slot for something that never arrives.
        [("EmitCType", typeof(VoidType))] = "void is not a value a slot can hold",
        [("TypeSig", typeof(VoidType))] = "no struct is ever keyed on void",
        [("EqCall", typeof(VoidType))] = "nothing to compare",
        [("WriteCall", typeof(VoidType))] = "nothing to print",

        // A `stash of T` is rewritten to its closure form before any of these see it — NoStashes
        // does that on the way in — so a raw StashType reaching one is a bug in the rewrite, not a
        // missing arm. It is refused so the rewrite failing is loud.
        // ⚠ EqCall and WriteCall take whatever their caller hands them, and every caller gets the
        // type from TypeOf — which de-stashes. A raw StashType arriving here is a broken rewrite,
        // so refusing is what makes that loud rather than silently comparing two closures.
        [("EqCall", typeof(StashType))] = "TypeOf de-stashes first; a raw one here is a broken rewrite",
        [("WriteCall", typeof(StashType))] = "TypeOf de-stashes first; a raw one here is a broken rewrite",

        // Book, mapping and the two failure MARKERS are checker vocabulary, not runtime values: a
        // book is a scope, a marker is what `the failure` narrows through. None reaches the backend.
        [("EmitCType", typeof(BookType))] = "a book is a scope, never a value",
        [("TypeSig", typeof(BookType))] = "a book is a scope, never a value",
        [("EqCall", typeof(BookType))] = "a book is a scope, never a value",
        [("WriteCall", typeof(BookType))] = "a book is a scope, never a value",
        [("EmitCType", typeof(MappingType))] = "checker vocabulary for a map shape, not a runtime type",
        [("TypeSig", typeof(MappingType))] = "checker vocabulary for a map shape, not a runtime type",
        [("EqCall", typeof(MappingType))] = "checker vocabulary for a map shape, not a runtime type",
        [("WriteCall", typeof(MappingType))] = "checker vocabulary for a map shape, not a runtime type",
        [("TypeSig", typeof(FailureMarkerType))] = "a caught failure has one fixed C struct, not a keyed one",
        [("EqCall", typeof(FailureMarkerType))] = "failures are inspected by message and category, never compared whole",
        [("EmitCType", typeof(ExceptionMarkerType))] = "an exception is a control path, not a value a slot holds",
        [("TypeSig", typeof(ExceptionMarkerType))] = "an exception is a control path, not a value a slot holds",
        [("EqCall", typeof(ExceptionMarkerType))] = "an exception is a control path, not a value a slot holds",

        // An interface is a shape a value CONFORMS to; the value itself is always some object.
        [("EmitCType", typeof(InterfaceType))] = "monomorphized away — a value is always the concrete object",
        [("TypeSig", typeof(InterfaceType))] = "monomorphized away — a value is always the concrete object",
        [("EqCall", typeof(InterfaceType))] = "monomorphized away — a value is always the concrete object",
        [("WriteCall", typeof(InterfaceType))] = "monomorphized away — a value is always the concrete object",

        // Live machinery with identity but no readable content. Comparing or printing one would have
        // to invent an answer, and two backends are two processes — any answer would differ.
        [("EqCall", typeof(ChannelType))] = "identity, not value — and it would differ between backends",
        [("WriteCall", typeof(ChannelType))] = "identity, not value — and it would differ between backends",
        [("TypeSig", typeof(ChannelType))] = "one fixed runtime struct, not a keyed one",
        [("EqCall", typeof(TaskHandleType))] = "identity, not value — and it would differ between backends",
        [("WriteCall", typeof(TaskHandleType))] = "identity, not value — and it would differ between backends",
        [("TypeSig", typeof(TaskHandleType))] = "one fixed runtime struct, not a keyed one",
        [("EqCall", typeof(ReadableStreamType))] = "an open FILE* — identity, not value",
        [("WriteCall", typeof(ReadableStreamType))] = "an open FILE* — identity, not value",
        [("TypeSig", typeof(ReadableStreamType))] = "one fixed runtime type (FILE*), not a keyed one",
        [("EqCall", typeof(WritableStreamType))] = "an open FILE* — identity, not value",
        [("WriteCall", typeof(WritableStreamType))] = "an open FILE* — identity, not value",
        [("TypeSig", typeof(WritableStreamType))] = "one fixed runtime type (FILE*), not a keyed one",

        // A `T or failure` is consumed at the call site — handled, propagated, or defaulted. It is
        // never stored whole, printed, or compared.
        [("EqCall", typeof(FailureType))] = "consumed at the call site, never compared",
        [("WriteCall", typeof(FailureType))] = "consumed at the call site, never printed whole",

    };

    [Fact]
    public void EveryCufetType_IsAccountedForInEveryPerTypeSwitch()
    {
        var unexplained = new List<string>();
        var stale = new List<string>();

        foreach (var sw in PerTypeSwitches)
            foreach (var (clr, instance) in Instances)
            {
                bool listed = DeliberateRefusals.ContainsKey((sw.Name, clr));
                bool refused;
                try
                {
                    // A fresh generator per cell: these switches REGISTER structs as a side effect,
                    // and a shared one would let an earlier cell decide a later one's answer.
                    sw.Invoke(FreshGenerator(), instance);
                    refused = false;
                }
                catch (CompilerException) { refused = true; }
                catch (TypeException) { refused = true; }

                if (refused && !listed) unexplained.Add($"  {sw.Name} refuses {clr.Name}");
                if (!refused && listed) stale.Add($"  {sw.Name} now handles {clr.Name}");
            }

        Assert.True(unexplained.Count == 0,
            $"{unexplained.Count} per-type switch cell(s) refuse a type with no recorded reason:\n"
          + string.Join("\n", unexplained)
          + "\n\nEither add the arm, or add the (switch, type) pair to DeliberateRefusals with the "
          + "reason it is deliberate. An unexplained refusal is how a missing arm hides.");

        Assert.True(stale.Count == 0,
            $"{stale.Count} recorded refusal(s) are no longer refusals:\n"
          + string.Join("\n", stale)
          + "\n\nRemove them from DeliberateRefusals — a table that records decisions nobody made "
          + "any more stops being read.");
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
        // CufetAxiomDefinition — considered, one descent at a time. The compiler's four never meet
        // one (CiteExpansion drops every block before a backend sees a program). The checker's two
        // walk a FUNCTION body for its returns, and what a block holds returns nowhere — it is
        // placed elsewhere and checked there. Linter.ChildBlocks now descends, because the linter
        // runs on the program as written and that is the only place the text exists. SemanticTokens
        // deliberately does not, and the reason is the SURFACE rather than any limitation: `[ … ]`
        // means the text inside is not the program around it, and it has to mean that whichever tag
        // it carries — foreign source cannot be highlighted, so Cufet source in the same brackets is
        // not highlighted either. See SemanticTokenTests, which holds both kinds of block to it.
        "BindStatement", "ConditionArm", "CufetAxiomDefinition",
        "ForEachFromInputStatement", "ForEachStatement",
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
        // ⚠ Keyed on FILE and member, so moving a walk between files reads as a new one. That is
        // the guard working: splitting CodeGenerator.cs into partials made three of these look
        // unaccounted-for, and the right answer was to confirm each is still the same walk and
        // repoint it — not to loosen the key, which is what makes a walk impossible to lose.
        "Ast.cs: Contains",
        "CodeGenerator.cs: ProgramUsesOpenUnion",
        "CodeGenerator.cs: TaskBodyMayMutate",
        "CodeGenerator.Expressions.cs: IsBoundSomewhere",
        "CodeGenerator.Expressions.cs: CaptureWriteIsObservable",
        "CodeGenerator.Expressions.cs: CollectRefsDefs",
        // The one walk behind every question StashTransform asks — does this bury, does it return,
        // does a `Stop` escape it, is this name mentioned — and the type checker's burying-function
        // detection calls the same one. Proof it sees inside ConditionArm and JudgeArm:
        // Interpreter.Tests/StashDetectionTests. Both arm tests were shown RED by keying the walk on
        // IStatement/IExpression instead of the namespace. (The Otherwise test in that file stayed
        // green under the same break and is labelled there as not discriminating — an `Otherwise`
        // body is an ordinary property.)
        "StashTransform.cs: Search",
        // Q1 for cufet blocks: a block may reach only for names that belong to the PROGRAM, so a
        // name it did not declare cannot silently mean whatever the site that cited it happens to
        // have. Proof it sees inside ConditionArm and JudgeArm:
        // Interpreter.Tests/CufetAxiomTests.ACaptureHidingInAnIfArmBody_IsStillCaught and
        // ACaptureHidingInAJudgeArmBody_IsStillCaught. Both were shown RED by keying the walk on
        // IStatement/IExpression instead of the namespace, and in both the captured name appears
        // ONLY inside an arm's BODY — a `Judge` subject is an ordinary property any walk reaches.
        "CiteExpansion.cs: RequireNoCapture",
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
    /// <summary>
    /// Every word the lexer reserves must be a word the editor grammar has heard of.
    /// </summary>
    /// <remarks>
    /// !! `Cite` shipped without being one. It went into TokenType, into the lexer's keyword
    /// switch, and into GRAMMAR.md's table — and nothing said the TextMate grammar was a fourth
    /// place, so it rendered as plain text with every keyword around it coloured. Nothing failed;
    /// a reader noticed it in a screenshot.
    ///
    /// ★ What this proves and what it does not. It proves the grammar has an OPINION about every
    /// reserved word — that adding one to the lexer cannot leave the editor silently unaware of it.
    /// It does not prove the word is scoped WELL, or scoped at all: a word may legitimately appear
    /// in a rule that scopes other things, which is how `the` and `a` reach the grammar (see the
    /// articles-and-prepositions rule, whose whole job is to leave them unpainted so the words
    /// that carry meaning stand out). Checking the scope a word RECEIVES cannot be done from the
    /// JSON — an alternation inside a rule is not tied to a capture — so the weaker claim is made
    /// honestly rather than the stronger one approximated.
    ///
    /// ⚠ No allow-list, deliberately. Every one of the reserved words is mentioned today, so an
    /// exception here would be a new decision rather than a recorded one.
    /// </remarks>
    [Fact]
    public void EveryReservedWord_IsKnownToTheEditorGrammar()
    {
        var lexer = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Lexer", "Lexer.cs"));
        var reserved = new Regex(@"""([a-z][a-z-]*)""\s*=>\s*TokenType\.")
            .Matches(lexer).Select(m => m.Groups[1].Value).Distinct().OrderBy(w => w).ToList();

        Assert.True(reserved.Count > 100,
            $"only {reserved.Count} reserved words were found in Lexer.cs — the keyword switch has "
            + "moved or been respelled, and this test is now checking almost nothing.");

        var grammarPath = Path.Combine(
            FindRepoRoot(), "editors", "vscode", "syntaxes", "cufet.tmLanguage.json");
        Assert.True(File.Exists(grammarPath),
            $"the editor grammar was not found at {grammarPath} — it has moved, and this test has "
            + "stopped comparing anything.");

        // Every `match` and `begin` in the file, whatever rule it belongs to.
        var patterns = new List<string>();
        void Collect(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "match", "begin" })
                    if (node.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                        patterns.Add(p.GetString()!);
                foreach (var child in node.EnumerateObject()) Collect(child.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var child in node.EnumerateArray()) Collect(child);
        }
        using var grammar = JsonDocument.Parse(File.ReadAllText(grammarPath));
        Collect(grammar.RootElement);

        var blob = string.Join("\n", patterns);
        var unknown = reserved
            .Where(word => !Regex.IsMatch(blob, $@"(?<![\w-]){Regex.Escape(word)}(?![\w-])"))
            .ToList();

        Assert.True(unknown.Count == 0,
            $"{unknown.Count} reserved word(s) the editor grammar has never heard of: "
            + string.Join(", ", unknown)
            + "\n\nA word the lexer reserves but the grammar does not mention renders as plain text "
            + "while the keywords around it are coloured, and nothing else in this suite notices. "
            + "Add it to the rule it belongs to in editors/vscode/syntaxes/cufet.tmLanguage.json "
            + "— or, if it is an article or a preposition, to the rule that deliberately leaves "
            + "those unpainted.");
    }

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

                // ⚠ The window is how far below the launch to look, and it is sized to the code
                // rather than to the rule — it was 20 and the close sat on the 19th line, so the
                // next honest addition to the ProcessStartInfo (redirecting stderr) pushed a
                // correct launcher out of view and failed this. Keep it comfortably ahead of the
                // block it has to span; keying on `binPath` is what keeps it from reaching into
                // an unrelated launch, not the tightness of this number.
                var window = string.Join("\n", lines.Skip(i).Take(40));
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
