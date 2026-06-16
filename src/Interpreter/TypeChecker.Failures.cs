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
        foreach (var s in trySt.Body)
            CheckStatement(s);
        _inTryBlock = savedInTryBlock;

        // Failure handler: bind "the failure" as FailureMarkerType.
        if (trySt.FailureHandler != null)
        {
            var savedFailure = _env.TryGetValue("the failure", out var prevFailure) ? prevFailure : null;
            _env["the failure"] = new TypeInfo(CufetType.FailureMarker,
                new VariableReference("the failure", trySt.Line), trySt.Line);
            foreach (var s in trySt.FailureHandler)
                CheckStatement(s);
            if (savedFailure != null) _env["the failure"] = savedFailure;
            else _env.Remove("the failure");
        }

        // Exception handler: bind "the exception" as ExceptionMarkerType; set _inExceptionHandler.
        if (trySt.ExceptionHandler != null)
        {
            var savedEx = _env.TryGetValue("the exception", out var prevEx) ? prevEx : null;
            _env["the exception"] = new TypeInfo(CufetType.ExceptionMarker,
                new VariableReference("the exception", trySt.Line), trySt.Line);
            var savedInEx = _inExceptionHandler;
            _inExceptionHandler = true;
            foreach (var s in trySt.ExceptionHandler)
                CheckStatement(s);
            _inExceptionHandler = savedInEx;
            if (savedEx != null) _env["the exception"] = savedEx;
            else _env.Remove("the exception");
        }
    }
}
