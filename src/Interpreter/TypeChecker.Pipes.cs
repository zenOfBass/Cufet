namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Pipe type checking ────────────────────────────────────────────────────
    //
    // v1 scope: structural validation only.
    //   Subprocess branch: both operands must be RunExpressions (text-typed I/O).
    //   Task branch: both operands must evaluate to FunctionType.
    //
    // Cross-pipe type compatibility (producer outputs T, consumer reads T) is NOT
    // statically checked in v1: the consumer body is checked with an unknown iterator
    // type, meaning body expressions are left unverified by the type checker until
    // the pipe is formed. The runtime catches mismatches. This mirrors how lambdas
    // are checked lazily in closure contexts. A future slice can tighten this by
    // storing bind bodies and re-checking the consumer body in pipe context.

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
                throw new TypeException(FormatTypeError(
                    "a pipe stage must be a function",
                    null,
                    GetExprLine(stage),
                    $"use a {FormatType(t)} as a pipe stage",
                    "Pipe stages must be Bind'd functions or lambdas. Did you mean to use '|' between function names?"));
        }
    }

    private void CheckOutputStatement(OutputStatement os)
    {
        // Just validate the value is typeable. Context enforcement (must be in a producer)
        // is done at runtime.
        InferType(os.Value);
    }

    private void CheckForEachFromInput(ForEachFromInputStatement fe)
    {
        // The iterator type is only known in pipe context (from the producer's output type).
        // In v1, skip body type-checking for consumer loops — the runtime enforces types.
        // A future slice will re-check the body with the correct element type once the
        // producer's output type is determined at the pipe site.
    }

    // Expression-position subprocess pipe: 'run A | run B' used as a value.
    // Returns the same FailureType(RunResultType) that a single 'run' expression returns,
    // so all failure-handling surfaces ('but on failure', Try, 'or pass the failure off')
    // work identically. Task pipes in expression position are a static error.
    private CufetType? InferSubprocessPipeExpr(PipeExpression pipe)
    {
        var stages = FlattenPipe(pipe);

        if (!stages.TrueForAll(s => s is RunExpression))
            throw new TypeException(FormatTypeError(
                "only subprocess pipes can be used as values",
                null, pipe.Line,
                "use a task-function pipe in expression position",
                "Task pipes are statement-only. Only 'run A | run B' subprocess pipes produce a result record."));

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
            throw new TypeException(FormatTypeError(
                "running a program can fail — you must handle the failure",
                null, pipe.Line,
                "use a subprocess pipe without handling the launch failure",
                "Wrap in 'Try to: / In case of failure:', use 'but on failure <default>', or use 'or pass the failure off'."));
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
}
