namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Failures (recoverable errors as values) ───────────────────────────────

    private object EvaluateCastExpr(CastExpression cast)
    {
        var result = ExecuteCallExpr(cast.Function, cast.Args, cast.Line)
            ?? throw new RuntimeException(
                $"{FuncDisplayName(cast.Function)} gives nothing back — it can't be used as a value (line {cast.Line}).");
        if (result is FailureValue fv)
            throw new FailureUnwind(fv);
        return result;
    }

    private object EvaluateFailureLiteral(FailureLiteral lit)
    {
        var message  = (string)Evaluate(lit.Message);
        var category = lit.Category != null ? (string)Evaluate(lit.Category) : null;
        return new FailureValue(message, category);
    }

    private object EvaluateFailureFallback(FailureFallback ff)
    {
        try
        {
            return Evaluate(ff.Fallible);
        }
        catch (FailureUnwind)
        {
            return Evaluate(ff.Default);
        }
    }

    private object EvaluateFailurePropagate(FailurePropagate fp)
    {
        try
        {
            return Evaluate(fp.Fallible);
        }
        catch (FailureUnwind fu)
        {
            throw new ReturnException(fu.Value);
        }
    }

    private void ExecuteTryStatement(TryStatement trySt)
    {
        FailureUnwind?    caughtFailure = null;
        RuntimeException? caughtEx      = null;

        try
        {
            foreach (var s in trySt.Body)
                Execute(s);
        }
        catch (FailureUnwind fu) when (trySt.FailureHandler != null)
        {
            caughtFailure = fu;
        }
        catch (RuntimeException re) when (trySt.ExceptionHandler != null)
        {
            caughtEx = re;
        }

        if (caughtFailure != null)
        {
            var savedFailure = _env.TryGetValue("the failure", out var prev);
            _env["the failure"] = caughtFailure.Value;
            try
            {
                foreach (var s in trySt.FailureHandler!)
                    Execute(s);
            }
            finally
            {
                if (savedFailure) _env["the failure"] = prev!;
                else _env.Remove("the failure");
            }
            return;
        }

        if (caughtEx != null)
        {
            var savedEx = _env.TryGetValue("the exception", out var prev);
            _env["the exception"] = new ExceptionValue(caughtEx.Message);
            bool suppress = false;
            try
            {
                foreach (var s in trySt.ExceptionHandler!)
                    Execute(s);
            }
            catch (SuppressSignal)
            {
                suppress = true;
            }
            finally
            {
                if (savedEx) _env["the exception"] = prev!;
                else _env.Remove("the exception");
            }
            if (!suppress) throw caughtEx; // re-raise by default
        }
    }
}
