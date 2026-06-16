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
        FailureUnwind? caught = null;
        try
        {
            foreach (var s in trySt.Body)
                Execute(s);
        }
        catch (FailureUnwind fu)
        {
            caught = fu;
        }

        if (caught == null) return;

        var savedFailure = _env.TryGetValue("the failure", out var prev);
        _env["the failure"] = caught.Value;
        try
        {
            foreach (var s in trySt.FailureHandler)
                Execute(s);
        }
        finally
        {
            if (savedFailure) _env["the failure"] = prev!;
            else _env.Remove("the failure");
        }
    }
}
