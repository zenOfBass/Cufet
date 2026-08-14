namespace Cufet.Interpreter;

/// <summary>
/// Rewrites every stash-producing function into a factory that hands back a CLOSURE — so neither
/// backend needs suspension machinery, because after this pass there is nothing left to suspend.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Why a closure and not a generated object type. The obvious lowering gives each burying
/// function its own nominal type — `stash_walk`, `stash_ticker` — which works but is NOT first
/// class: those types have different fields and different sizes, so a `series of stash of number`
/// would need one representation for elements that are not the same shape, i.e. a vtable. Cufet
/// declines vtables, which is what makes interfaces free.
/// </para>
/// <para>
/// A closure already has a uniform representation the language ships and trusts —
/// <c>cfn_N { fn ptr; void* env; }</c>, two pointers, copied by value — and closures are already
/// storable in a series. So EVERY `stash of T` lowers to the same type, and holding one in a local,
/// passing it to a function, or putting it in a collection all work with no union, no narrowing and
/// no dispatch table. Verified on both backends before this was written.
/// </para>
/// <para>
/// ★ The frame is a ONE-ELEMENT SERIES, and that is not arbitrary. A closure captures value types
/// by SNAPSHOT and region types by SHARE, so state that must survive between resumptions has to
/// live in a region. A series is the smallest one. The same write-back shape — read element one,
/// modify, store element one — is what a delegated sub-stash needs, so there is one idiom rather
/// than two.
/// </para>
/// <para>
/// ⚠ The lambda is returned INLINE, never bound to a local first: a closure that escapes indirectly
/// is refused by the compiler because its environment is opaque once built.
/// </para>
/// </remarks>
public static class StashTransform
{
    /// <summary>
    /// Rewrites the program's burying functions. <paramref name="buryingFunctions"/> comes from the
    /// type checker, which decided the question by walking each body for a `bury`.
    /// </summary>
    public static IReadOnlyList<IStatement> Expand(
        IReadOnlyList<IStatement> statements,
        IReadOnlySet<string> buryingFunctions)
    {
        if (buryingFunctions.Count == 0) return statements;   // the common case: tree untouched
        return Rewrite(statements, buryingFunctions);
    }

    private static List<IStatement> Rewrite(
        IReadOnlyList<IStatement> statements,
        IReadOnlySet<string> buryingFunctions)
    {
        var output = new List<IStatement>(statements.Count);
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case BindStatement bind when buryingFunctions.Contains(bind.Name) && bind.UntoType == null:
                    output.AddRange(ToFactory(bind));
                    break;

                // Burying functions are hoistable, so they can sit inside these bodies too.
                case PullStatement ps:
                    output.Add(ps with { Body = Rewrite(ps.Body, buryingFunctions) });
                    break;
                case PullRabbitStatement prs:
                    output.Add(prs with { Body = Rewrite(prs.Body, buryingFunctions) });
                    break;

                default:
                    output.Add(stmt);
                    break;
            }
        }
        return output;
    }

    /// <summary>
    /// `Bind number to f, given (…): Bury a. Bury b. Done.`
    /// becomes
    /// `Bind (voidable number function given ()) to f, given (…):
    ///      Define «frame» as a series of number with (0).
    ///      Return a function given ():
    ///          Define «step» as item 1 of «frame».
    ///          If «step» is 0: The item 1 of «frame» becomes 1. Return a. Done.
    ///          If «step» is 1: The item 1 of «frame» becomes 2. Return b. Done.
    ///          Return void.
    ///      Done.
    ///  Done.`
    /// </summary>
    private static List<IStatement> ToFactory(BindStatement bind)
    {
        int line = bind.Line, col = bind.Column;
        var element = bind.ReturnType!;                       // what it buries
        var yields  = new VoidableType(element);
        var frameType = new SeriesType(CufetType.Number);
        string resumeName = ResumePrefix + bind.Name;

        // ★ The resume body is hoisted into a NAMED function rather than living in the lambda, and
        // the reason is a real front-end limit: a lambda's return type is INFERRED, and `return T`
        // does not unify with `return void`. The first `Return <buried value>` would fix the type as
        // T, and the terminal `Return void.` that reports a spent stash would then be an error. A
        // named function DECLARES `voidable T`, so both are fine.
        //
        // Its first parameter is the frame; the rest are the original function's, so everything the
        // body referred to is still in scope by the same name and needs no rewriting.
        var resume = new BindStatement(
            resumeName,
            yields,
            [(frameType, FrameName), .. bind.Parameters],
            BuildResumeBody(bind, line, col),
            UntoType: null,
            ConstructsTypeName: null,
            line, col);

        // The factory: make a frame, hand back a closure over it.
        var factoryBody = new List<IStatement>
        {
            // Region-typed, so the closure SHARES it rather than snapshotting it — which is the
            // whole reason the step survives from one resumption to the next.
            new DefineStatement(FrameName, new SeriesLiteral(
                [Number(0, line, col)], CufetType.Number, line, col), false, false, line, col),

            // ⚠ The lambda is returned INLINE. A closure bound to a local and then returned is
            // refused by the compiler — its environment is opaque once built.
            new ReturnStatement(
                new LambdaLiteral([],
                    [new ReturnStatement(
                        new CastExpression(Var(resumeName, line, col),
                            [Var(FrameName, line, col),
                             .. bind.Parameters.Select(p => (IExpression)Var(p.Name, line, col))],
                            line, col),
                        line, col)],
                    line, col),
                line, col),
        };

        var factory = new BindStatement(
            bind.Name,
            new FunctionType([], yields),
            bind.Parameters,
            factoryBody,
            UntoType: null,
            ConstructsTypeName: null,
            line, col);

        return [resume, factory];
    }

    // The closure body: read the step, dispatch to the block that resumes there.
    private static List<IStatement> BuildResumeBody(BindStatement bind, int line, int col)
    {
        var body = new List<IStatement>
        {
            new DefineStatement(StepName,
                new SeriesAccess(Var(FrameName, line, col), Number(1, line, col), line, col),
                false, false, line, col),
        };

        int step = 0;
        foreach (var stmt in bind.Body)
        {
            if (stmt is not BuryStatement bury)
                throw new StashUnsupportedException(stmt, bind.Name);

            body.Add(new IfStatement(
                [new ConditionArm(
                    Equals(Var(StepName, line, col), Number(step, line, col), line, col),
                    [
                        SetStep(step + 1, line, col),
                        new ReturnStatement(bury.Value, bury.Line, bury.Column),
                    ])],
                ElseBody: null));
            step++;
        }

        // Spent. Also the terminator the checker needs: it cannot see that the dispatch above is
        // exhaustive, so without this the method reads as able to fall off its end.
        body.Add(new ReturnStatement(new VoidLiteral(line, col), line, col));
        return body;
    }

    private static IStatement SetStep(int to, int line, int col) =>
        new SeriesSetStatement(Var(FrameName, line, col), Number(1, line, col), Number(to, line, col), line, col);

    // ★ Names a user cannot write. An identifier is letters, digits and INTERNAL DASHES — never an
    // underscore — so these can never collide with a parameter or a local. (A capital would not
    // work: it lexes fine and is then refused, because an uppercase-initial identifier is illegal
    // everywhere. And `stash-frame` would NOT be safe, since the keyword check matches the whole
    // lexeme: `stash-frame` is still a legal user identifier despite `stash` being reserved.)
    private const string FrameName  = "stash_frame";
    private const string ResumePrefix = "stash_resume_";
    private const string StepName  = "stash_step";

    private static VariableReference Var(string name, int line, int col) => new(name, line, col);
    // NumberLiteral is one of the ten position-less nodes — it carries no line or column.
    private static NumberLiteral Number(int n, int line, int col) => new(n);
    // TokenType.Equal, not TokenType.Is — `is` is the SURFACE spelling and the parser lowers it to
    // Equal. Generated AST has to speak the lowered form; emitting `Is` reached the interpreter as
    // "Unknown binary operator".
    private static IExpression Equals(IExpression l, IExpression r, int line, int col) =>
        new BinaryExpression(l, Cufet.Lexer.TokenType.Equal, r, line, col);
}

/// <summary>
/// A shape inside a burying body that this pass cannot yet linearise.
/// </summary>
/// <remarks>
/// ★ A clean refusal, not a crash. The transform handles straight-line burys today; control flow —
/// a bury inside a loop or a conditional — needs the body split into basic blocks with the step
/// acting as a program counter, which is the next increment. Until then a program that would be
/// mis-lowered is told so, rather than compiling into something subtly wrong.
/// </remarks>
public sealed class StashUnsupportedException : Exception
{
    public StashUnsupportedException(IStatement stmt, string functionName)
        : base($"'{functionName}' buries, and this pass can only handle a body that is a straight "
             + $"run of 'bury' statements so far — it found a {stmt.GetType().Name}. A bury inside a "
             + $"loop or a conditional needs the body split into blocks, which is not built yet.")
    { }
}
