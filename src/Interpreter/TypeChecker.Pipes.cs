namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Pipe type checking ────────────────────────────────────────────────────
    //
    //   Subprocess branch: every stage must be a RunExpression (text-typed I/O).
    //   Task branch: every stage must evaluate to a FunctionType.
    //
    // Cross-stage element types ARE checked. A stage's input type is not written down
    // anywhere — `for each n from the input:` declares no type — so it can only come
    // from the stage upstream. That means a consumer body cannot be checked where it is
    // written; it has to be re-checked here, once the pipe says what flows into it.
    //
    // Until this existed, a consumer body was never type-checked AT ALL, and a mismatch
    // (a producer emitting `number` into a stage doing `the length of n`) escaped the
    // front end entirely — surfacing interpreted as a host-level cast exception, and
    // compiled as a gcc error against generated C. Loud, but neither was a Cufet error.

    // Free functions by name, so a stage named in a pipe can have its body re-checked.
    // Filled by the same hoisting pre-pass that registers their signatures.
    private readonly Dictionary<string, BindStatement> _freeBinds = new();

    // The element type flowing INTO the stage currently being re-checked. Null everywhere
    // except during that re-check — which is what keeps `for each … from the input` a
    // no-op when a stage's body is checked normally, at its own Bind site.
    private CufetType? _pipeInputElem;

    // Each stage's settled input element type, so a function reused across pipes at two
    // different element types is caught rather than silently re-checked twice.
    private readonly Dictionary<string, CufetType> _pipeStageElem = new();

    private void CheckPipe(PipeExpression pipe)
    {
        var stages = FlattenPipe(pipe);

        // Subprocess branch: all stages are RunExpression nodes.
        if (stages.TrueForAll(s => s is RunExpression))
        {
            // Each RunExpression already calls InferRunExpr which enforces failure-handling.
            // In a subprocess pipe we waive the must-handle requirement — the pipe itself
            // is fire-and-wait, failures surface as RuntimeExceptions.
            // We still validate the program and argument types by inferring each stage.
            // Use _inFailureHandledContext to suppress the must-handle error for run exprs.
            var prevCtx = _inFailureHandledContext;
            _inFailureHandledContext = true;
            try { foreach (var s in stages) InferType(s); }
            finally { _inFailureHandledContext = prevCtx; }
            return;
        }

        // Task branch: all stages must be functions.
        foreach (var stage in stages)
        {
            var t = InferType(stage);
            if (t == null) continue; // unknown type — runtime catches it
            if (t is not FunctionType)
                throw TypeError(
                    "a pipe stage must be a function",
                    null,
                    GetExprLine(stage), GetExprColumn(stage),
                    $"use a {FormatType(t)} as a pipe stage",
                    "Pipe stages must be Bind'd functions or lambdas. Did you mean to use '|' between function names?");
        }

        CheckStageElementTypes(stages);
    }

    // Walks the pipe left to right, carrying each stage's output element type into the next
    // stage as its input, and re-checking that stage's body with the iterator bound to it.
    private void CheckStageElementTypes(List<IExpression> stages)
    {
        CufetType? flowing = null;   // nothing flows into the first stage
        foreach (var stage in stages)
        {
            // Only a stage named directly can be re-checked — a lambda's body was already
            // checked inline where it was written, and an indirect function value has no body
            // to reach. Both leave `flowing` unknown, which stops the chain without erroring:
            // an unchecked stage should not cause a false positive downstream.
            if (stage is not VariableReference vr || !_freeBinds.TryGetValue(vr.Name, out var bind))
            {
                flowing = null;
                continue;
            }

            // One input element type per stage function, across every pipe in the program —
            // the same restriction the compiler enforces, checked here so the message is a
            // Cufet error rather than a codegen refusal.
            if (flowing != null && _pipeStageElem.TryGetValue(vr.Name, out var settled)
                && !settled.Equals(flowing))
                throw TypeError(
                    $"'{vr.Name}' already reads {FormatTypePlural(settled)} from its input",
                    null, GetExprLine(stage), GetExprColumn(stage),
                    $"also use it on a pipe carrying {FormatTypePlural(flowing)}",
                    "A pipe stage reads one kind of value. Write a separate function for the other pipe.");
            if (flowing != null) _pipeStageElem[vr.Name] = flowing;

            flowing = CheckStageBody(bind, flowing);
        }
    }

    // Re-checks one stage's body with `incoming` bound to its `from the input` iterator, and
    // returns the element type the stage itself emits (from the first `output` it reaches).
    // CheckBind saves and restores scopes, so running it a second time is self-contained.
    private CufetType? CheckStageBody(BindStatement bind, CufetType? incoming)
    {
        var savedElem = _pipeInputElem;
        var savedOut  = _pipeOutputElem;
        _pipeInputElem  = incoming;
        _pipeOutputElem = null;
        try
        {
            CheckBind(bind);
            return _pipeOutputElem;
        }
        finally { _pipeInputElem = savedElem; _pipeOutputElem = savedOut; }
    }

    // The element type of the stage body currently being re-checked, taken from its first
    // `output` — set by CheckOutputStatement while _pipeInputElem is active.
    private CufetType? _pipeOutputElem;

    private void CheckOutputStatement(OutputStatement os)
    {
        var t = InferType(os.Value);
        // Record the producer's element type for the stage downstream. First `output` wins,
        // matching how the compiler infers a stage's output type.
        if (_pipeOutputElem == null && t != null) _pipeOutputElem = t;
    }

    private void CheckForEachFromInput(ForEachFromInputStatement fe)
    {
        // Outside a pipe re-check there is no way to know the iterator's type — the stage has
        // not been connected to a producer yet — so the body is left for the pipe site.
        if (_pipeInputElem is not { } elem) return;

        EnterScope();
        Scope[fe.IteratorName] = new TypeInfo(ResolveParamType(elem), null!, fe.Line);
        try { CheckBlock(fe.Body); }
        finally { ExitScope(); }
    }

    // Expression-position subprocess pipe: 'run A | run B' used as a value.
    // Returns the same FailureType(RunResultType) that a single 'run' expression returns,
    // so all failure-handling surfaces ('but on failure', Try, 'or pass the failure off')
    // work identically. Task pipes in expression position are a static error.
    private CufetType? InferSubprocessPipeExpr(PipeExpression pipe)
    {
        var stages = FlattenPipe(pipe);

        if (!stages.TrueForAll(s => s is RunExpression))
            throw TypeError(
                "only subprocess pipes can be used as values",
                null, pipe.Line, pipe.Column,
                "use a task-function pipe in expression position",
                "Task pipes are statement-only. Only 'run A | run B' subprocess pipes produce a result record.");

        // Validate program/argument types for each stage, suppressing the per-stage
        // must-handle error — the pipe itself carries the FailureType wrapper.
        var savedCtx = _inFailureHandledContext;
        _inFailureHandledContext = true;
        try { foreach (var s in stages) InferType(s); }
        finally { _inFailureHandledContext = savedCtx; }

        // The pipe's type mirrors a single 'run' in the same context.
        if (_inTryBlock)
            return RunResultType;
        if (!_inFailureHandledContext)
            throw TypeError(
                "running a program can fail — you must handle the failure",
                null, pipe.Line, pipe.Column,
                "use a subprocess pipe without handling the launch failure",
                "Wrap in 'Try to: / In case of failure:', use 'but on failure <default>', or use 'or pass the failure off'.");
        return new FailureType(RunResultType);
    }

    // Flatten PipeExpression(PipeExpression(A, B), C) → [A, B, C] (left-associative).
    private static List<IExpression> FlattenPipe(PipeExpression pipe)
    {
        var stages = new List<IExpression>();
        void Flatten(IExpression e)
        {
            if (e is PipeExpression p) { Flatten(p.Left); Flatten(p.Right); }
            else stages.Add(e);
        }
        Flatten(pipe);
        return stages;
    }

    // Returns the line number for any expression (best-effort).
    private static int GetExprLine(IExpression e) => e switch
    {
        VariableReference vr     => vr.Line,
        RunExpression     run    => run.Line,
        LambdaLiteral     lam    => lam.Line,
        PipeExpression    pipe   => pipe.Line,
        _                        => 0,
    };

    // The column that pairs with GetExprLine — same shapes, same best-effort fallback.
    private static int GetExprColumn(IExpression e) => e switch
    {
        VariableReference vr     => vr.Column,
        RunExpression     run    => run.Column,
        LambdaLiteral     lam    => lam.Column,
        PipeExpression    pipe   => pipe.Column,
        _                        => 0,
    };
}
