namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Failures (recoverable errors as values) ───────────────────────────────

    private CufetType InferFailureLiteral(FailureLiteral lit)
    {
        var msgType = InferType(lit.Message);
        if (msgType != null && msgType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "a failure message must be text",
                null, lit.Line,
                $"use a {FormatType(msgType)} as the failure message",
                "Write the message as a text literal, e.g. a failure \"something went wrong\"."));

        if (lit.Category != null)
        {
            var catType = InferType(lit.Category);
            if (catType != null && catType != CufetType.Text)
                throw new TypeException(FormatTypeError(
                    "a failure category must be text",
                    null, lit.Line,
                    $"use a {FormatType(catType)} as the failure category",
                    "Write the category as a text literal, e.g. of category \"bad-input\"."));
        }

        return CufetType.FailureMarker;
    }

    // "X but on failure Y" → plain T.
    // Checks that X is FailureType(T) and Y is assignable to T; returns T.
    private CufetType? InferFailureFallback(FailureFallback ff)
    {
        var savedCtx = _inFailureHandledContext;
        _inFailureHandledContext = true;
        var fallibleType = InferType(ff.Fallible);
        _inFailureHandledContext = savedCtx;
        var defaultType  = InferType(ff.Default);

        if (fallibleType is FailureType f)
        {
            if (defaultType != null && !IsAssignable(f.Inner, defaultType))
                throw new TypeException(FormatTypeError(
                    $"the default value is a {FormatType(defaultType)}, but the fallible holds {FormatTypePlural(f.Inner)}",
                    null, ff.Line,
                    $"use a {FormatType(defaultType)} as the default for a {FormatType(f.Inner)} or failure",
                    $"The default after 'but on failure' must be a {FormatType(f.Inner)}."));
            return f.Inner;
        }
        if (fallibleType is FailureMarkerType)
            return defaultType; // always-failure: result is always the default
        if (fallibleType != null)
            throw new TypeException(FormatTypeError(
                $"'{FormatType(fallibleType)}' can never fail",
                null, ff.Line,
                $"use 'but on failure' on a {FormatType(fallibleType)} value",
                "Only fallible values (declared as 'T or failure') can fail. 'but on failure' is only needed for fallible values."));
        return null;
    }

    // "X or pass the failure off" → T (strips FailureType wrapper).
    // Requires the enclosing function to also be declared fallible.
    private CufetType? InferFailurePropagate(FailurePropagate fp)
    {
        var savedCtx = _inFailureHandledContext;
        _inFailureHandledContext = true;
        var fallibleType = InferType(fp.Fallible);
        _inFailureHandledContext = savedCtx;

        if (fallibleType is FailureType f)
        {
            if (_expectedReturnType is not FailureType)
                throw new TypeException(FormatTypeError(
                    "you can only propagate a failure from a function declared as fallible",
                    null, fp.Line,
                    "propagate a failure from a non-fallible function",
                    "Declare this function's return type as 'T or failure' to allow propagation, or handle the failure with 'but on failure' or a Try block instead."));
            return f.Inner;
        }
        if (fallibleType is FailureMarkerType)
        {
            if (_expectedReturnType is not FailureType)
                throw new TypeException(FormatTypeError(
                    "you can only propagate a failure from a function declared as fallible",
                    null, fp.Line,
                    "propagate a failure from a non-fallible function",
                    "Declare this function's return type as 'T or failure' to allow propagation, or handle the failure with 'but on failure' or a Try block instead."));
            return null;
        }
        if (fallibleType != null)
            throw new TypeException(FormatTypeError(
                $"'{FormatType(fallibleType)}' can never fail — nothing to propagate",
                null, fp.Line,
                $"propagate a failure from a {FormatType(fallibleType)} value",
                "Only fallible values (declared as 'T or failure') can be propagated."));
        return null;
    }

    // ── File I/O ─────────────────────────────────────────────────────────────

    // read all from the file "<path>"       → text or failure (FailureType(Text))
    // read all lines from the file "<path>" → series of text or failure (FailureType(SeriesType(Text)))
    // Inside a Try body (_inTryBlock): auto-unwrap to the success type.
    // Inside handled context (_inFailureHandledContext): return FailureType (caller handles it).
    // Otherwise: static error — must handle the failure.
    private CufetType InferFileReadExpr(FileReadExpression fe)
    {
        var pathType = InferType(fe.Path);
        if (pathType != null && pathType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "a file path must be text",
                null, fe.Line,
                $"use a {FormatType(pathType)} as a file path",
                "Write the path as a text literal like \"config.txt\", or use a text variable."));

        CufetType successType = fe.Form == FileReadForm.AllLines
            ? new SeriesType(CufetType.Text)
            : CufetType.Text;

        if (_inTryBlock)
            return successType; // inside Try body: failure branch not taken, unwrap to T

        if (!_inFailureHandledContext)
            throw new TypeException(FormatTypeError(
                "reading from a file can fail — you must handle the failure",
                null, fe.Line,
                "use a file read's result without handling the failure",
                "Wrap the read in a 'Try to: / In case of failure:' block, use 'but on failure <default>', or use 'or pass the failure off'."));

        return new FailureType(successType);
    }

    // write <text> to the file "<path>"  /  append <text> to the file "<path>"
    // Validates operand types; failure is runtime-only (caught by enclosing Try if present).
    private void CheckFileWrite(FileWriteStatement fw)
    {
        var valueType = InferType(fw.Value);
        if (valueType != null && valueType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "you can only write text to a file",
                null, fw.Line,
                $"write a {FormatType(valueType)} to a file",
                "Convert the value to text first with 'converted to text', or build a text value with 'joined to'."));

        var pathType = InferType(fw.Path);
        if (pathType != null && pathType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "a file path must be text",
                null, fw.Line,
                $"use a {FormatType(pathType)} as a file path",
                "Write the path as a text literal like \"output.txt\", or use a text variable."));
    }

    // ── Process execution ─────────────────────────────────────────────────────

    // Synthetic RecordType for the result of 'run': (errors: text, exit-code: number, output: text).
    // Named fields are alphabetically ordered per RecordType convention.
    private static readonly RecordType RunResultType = new RecordType(
        positionalTypes: [],
        namedFields:
        [
            ("errors",    CufetType.Text),
            ("exit-code", CufetType.Number),
            ("output",    CufetType.Text),
        ]
    );

    // run <program> [with arguments (...)] → result or failure (FailureType(RunResultType)).
    // Inside a Try body (_inTryBlock): auto-unwrap to RunResultType.
    // Inside handled context (_inFailureHandledContext): return FailureType (caller handles it).
    // Otherwise: static error — must handle the launch failure.
    private CufetType InferRunExpr(RunExpression run)
    {
        var programType = InferType(run.Program);
        if (programType != null && programType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "the program to run must be text",
                null, run.Line,
                $"use a {FormatType(programType)} as the program name",
                "Write the program name as a text literal like \"ls\", or use a text variable."));

        foreach (var arg in run.Args)
        {
            var argType = InferType(arg);
            if (argType != null && argType != CufetType.Text)
                throw new TypeException(FormatTypeError(
                    "each argument must be text",
                    null, run.Line,
                    $"use a {FormatType(argType)} as a program argument",
                    "Arguments are passed directly to the program — each must be a text value."));
        }

        if (_inTryBlock)
            return RunResultType;

        if (!_inFailureHandledContext)
            throw new TypeException(FormatTypeError(
                "running a program can fail — you must handle the failure",
                null, run.Line,
                "use a run result without handling the launch failure",
                "Wrap in 'Try to: / In case of failure:', use 'but on failure <default>', or use 'or pass the failure off'."));

        return new FailureType(RunResultType);
    }

    private void CheckTryStatement(TryStatement trySt)
    {
        if (trySt.FailureHandler == null && trySt.ExceptionHandler == null)
            throw new TypeException(FormatTypeError(
                "a 'Try' block must have at least one handler",
                null, trySt.Line,
                "write a Try block with no handlers",
                "Add 'In case of failure:' and/or 'In case of exception (the exception):' after the Try body."));

        // Body: only set _inTryBlock when there IS a failure handler, so CastExpression results
        // of FailureType(T) auto-unwrap to T. Without a failure handler, fallible calls inside
        // the body must be handled explicitly ('but on failure' / 'or pass the failure off').
        var savedInTryBlock = _inTryBlock;
        _inTryBlock = trySt.FailureHandler != null;
        EnterScope();
        try { foreach (var s in trySt.Body) CheckStatement(s); }
        finally { ExitScope(); _inTryBlock = savedInTryBlock; }

        // Failure handler: bind "the failure" as FailureMarkerType.
        if (trySt.FailureHandler != null)
        {
            EnterScope();
            Scope["the failure"] = new TypeInfo(CufetType.FailureMarker,
                new VariableReference("the failure", trySt.Line), trySt.Line);
            try { foreach (var s in trySt.FailureHandler) CheckStatement(s); }
            finally { ExitScope(); }
        }

        // Exception handler: bind "the exception" as ExceptionMarkerType; set _inExceptionHandler.
        if (trySt.ExceptionHandler != null)
        {
            EnterScope();
            Scope["the exception"] = new TypeInfo(CufetType.ExceptionMarker,
                new VariableReference("the exception", trySt.Line), trySt.Line);
            var savedInEx = _inExceptionHandler;
            _inExceptionHandler = true;
            try { foreach (var s in trySt.ExceptionHandler) CheckStatement(s); }
            finally { _inExceptionHandler = savedInEx; ExitScope(); }
        }
    }
}
