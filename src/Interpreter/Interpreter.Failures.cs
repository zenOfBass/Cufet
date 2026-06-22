using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Failures (recoverable errors as values) ───────────────────────────────

    // ── Directory traversal ──────────────────────────────────────────────────────

    // the contents of the directory <path>  →  series of text (full paths) or failure
    private object EvaluateDirectoryContents(DirectoryContentsExpression dce)
    {
        var path = (string)Evaluate(dce.Path)!;
        try
        {
            return Directory.GetFileSystemEntries(path)
                            .Select(e => (object)e)
                            .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DirectoryIoFailure(path, ex);
        }
    }

    // the path <path> exists / is a directory / is a file  →  boolean (infallible)
    private object EvaluatePathCheck(PathCheckExpression pce)
    {
        var path = (string)Evaluate(pce.Path)!;
        return pce.Kind switch
        {
            PathCheckKind.Exists      => (object)(Directory.Exists(path) || File.Exists(path)),
            PathCheckKind.IsDirectory => (object)Directory.Exists(path),
            PathCheckKind.IsFile      => (object)File.Exists(path),
            _ => throw new InvalidOperationException($"Unknown PathCheckKind {pce.Kind}"),
        };
    }

    private static FailureUnwind DirectoryIoFailure(string path, Exception ex)
    {
        string category, message;
        if (ex is DirectoryNotFoundException)
        {
            category = "not-found";
            message  = $"the directory '{path}' was not found";
        }
        else if (ex is UnauthorizedAccessException)
        {
            category = "permission-denied";
            message  = $"permission denied reading directory '{path}'";
        }
        else
        {
            category = "disk-error";
            message  = ex.Message;
        }
        return new FailureUnwind(new FailureValue(message, category));
    }

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

    // "With the file '<path>' open for reading/writing as <name>: ... Done."
    // Opens the file, binds the stream, executes the body, then closes on every exit path.
    private void ExecuteWithOpen(WithOpenStatement wos)
    {
        var path = (string)Evaluate(wos.Path);

        if (wos.Mode == OpenMode.Reading)
        {
            StreamReader reader;
            try { reader = new StreamReader(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw FileIoFailure(path, ex); }

            EnterScope();
            Scope[wos.BindingName] = new ReadableStreamValue(reader);
            try
            {
                foreach (var stmt in wos.Body)
                    Execute(stmt);
            }
            finally
            {
                ExitScope();
                reader.Dispose();
            }
        }
        else
        {
            StreamWriter writer;
            try { writer = new StreamWriter(path, append: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw FileIoFailure(path, ex); }

            EnterScope();
            Scope[wos.BindingName] = new WritableStreamValue(writer);
            try
            {
                foreach (var stmt in wos.Body)
                    Execute(stmt);
            }
            finally
            {
                ExitScope();
                writer.Dispose(); // flushes and closes
            }
        }
    }

    // "write <value> to <stream>" — incremental text write; no newline added.
    private void ExecuteWriteToStream(WriteToStreamStatement wts)
    {
        var text = (string)Evaluate(wts.Value);
        var sv   = (WritableStreamValue)Evaluate(wts.Stream);
        try { sv.Writer.Write(text); }
        catch (IOException ex)
        { throw new FailureUnwind(new FailureValue(ex.Message, "disk-error")); }
    }

    // ── Process execution ─────────────────────────────────────────────────────

    private object EvaluateRunExpr(RunExpression run)
    {
        var program = (string)Evaluate(run.Program);
        var args    = run.Args.Select(a => (string)Evaluate(a)).ToArray();
        try
        {
            var psi = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            // Each argument added individually — no shell, no injection possible.
            // ProcessStartInfo.ArgumentList passes each string as a separate OS-level argument.
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for '{program}'");

            // Read stdout and stderr concurrently — sequential reads deadlock when the process
            // fills one pipe buffer while Cufet is blocked draining the other.
            var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
            // Poll instead of blocking: lets Ctrl-C kill the child tree and unwind.
            // Needed because redirecting stdout/stderr can detach the child from the console
            // process group, preventing Windows from forwarding Ctrl-C automatically.
            while (!proc.WaitForExit(50))
            {
                if (_interruptRequested)
                {
                    proc.Kill(entireProcessTree: true);
                    break;
                }
            }
            Task.WaitAll(stdoutTask, stderrTask);
            if (_interruptRequested) throw new InterruptUnwind();

            return new RecordValue(
                [],
                [
                    ("errors",    (object)stderrTask.Result),
                    ("exit-code", (object)(decimal)proc.ExitCode),
                    ("output",    (object)stdoutTask.Result),
                ]
            );
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException
                                       or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw LaunchFailure(program, ex);
        }
    }

    // Maps .NET process-launch exceptions to Cufet failure values at the launch boundary.
    // Host launch-exceptions must not propagate into Cufet — a missing program is recoverable.
    private static FailureUnwind LaunchFailure(string program, Exception ex)
    {
        int w32Code = ex is Win32Exception w32 ? w32.NativeErrorCode : -1;
        string category, message;
        if (ex is FileNotFoundException or DirectoryNotFoundException || w32Code is 2 or 3)
        {
            category = "not-found";
            message  = $"the program '{program}' was not found";
        }
        else if (ex is UnauthorizedAccessException || w32Code == 5)
        {
            category = "permission-denied";
            message  = $"permission denied executing '{program}'";
        }
        else
        {
            category = "io-error";
            message  = ex.Message;
        }
        return new FailureUnwind(new FailureValue(message, category));
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
