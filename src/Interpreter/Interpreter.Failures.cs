using System.IO;
using System.Linq;

namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Failures (recoverable errors as values) ───────────────────────────────

    // ── File I/O ─────────────────────────────────────────────────────────────

    // Maps .NET IO exceptions to Cufet failure values at the I/O boundary.
    // Host exceptions must not surface as Cufet exceptions — file-not-found is recoverable.
    private static FailureUnwind FileIoFailure(string path, Exception ex)
    {
        string category, message;
        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            category = "not-found";
            message  = $"the file '{path}' was not found";
        }
        else if (ex is UnauthorizedAccessException)
        {
            category = "permission-denied";
            message  = $"permission denied accessing '{path}'";
        }
        else
        {
            category = "disk-error";
            message  = ex.Message;
        }
        return new FailureUnwind(new FailureValue(message, category));
    }

    private object EvaluateFileReadExpr(FileReadExpression fe)
    {
        var path = (string)Evaluate(fe.Path);
        try
        {
            return fe.Form switch
            {
                FileReadForm.All      => File.ReadAllText(path),
                FileReadForm.AllLines => File.ReadAllLines(path).Select(l => (object)l).ToList(),
                _ => throw new InvalidOperationException($"Unknown FileReadForm {fe.Form}"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FileIoFailure(path, ex);
        }
    }

    private void ExecuteFileWriteStatement(FileWriteStatement fw)
    {
        var value = (string)Evaluate(fw.Value);
        var path  = (string)Evaluate(fw.Path);
        try
        {
            if (fw.Append)
                File.AppendAllText(path, value);
            else
                File.WriteAllText(path, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FileIoFailure(path, ex);
        }
    }

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

        EnterScope();
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
        finally { ExitScope(); }

        if (caughtFailure != null)
        {
            EnterScope();
            Scope["the failure"] = caughtFailure.Value;
            try
            {
                foreach (var s in trySt.FailureHandler!)
                    Execute(s);
            }
            finally { ExitScope(); }
            return;
        }

        if (caughtEx != null)
        {
            EnterScope();
            Scope["the exception"] = new ExceptionValue(caughtEx.Message);
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
            finally { ExitScope(); }
            if (!suppress) throw caughtEx; // re-raise by default
        }
    }
}
