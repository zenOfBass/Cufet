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
    // Entries are SORTED (Ordinal): the raw OS order is filesystem-dependent (NTFS happens to be
    // alphabetical, ext4 is not), so an unsorted listing is nondeterministic across machines.
    // Sorting defines the undefined — same normalize-the-unobservable move as FormatRecord's field
    // order — and makes listings deterministic given directory content (and native-oracle-testable).
    private object EvaluateDirectoryContents(DirectoryContentsExpression dce)
    {
        var path = (string)Evaluate(dce.Path)!;
        try
        {
            return Directory.GetFileSystemEntries(path)
                            .OrderBy(e => e, StringComparer.Ordinal)
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
            // Deterministic, platform-independent fallback (NOT ex.Message) — the same templating
            // FileIoFailure/LaunchFailure received in 9A/9C, applied here when directory-contents
            // landed natively (the native errno path reproduces this string bit-identically).
            category = "disk-error";
            message  = $"reading the directory '{path}' failed";
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
            // Deterministic, platform-independent fallback (NOT ex.Message, whose text is
            // host/runtime-specific and can't be reproduced by the native compiler's errno path).
            // The native backend produces this identical string for any non-ENOENT/EACCES errno.
            category = "disk-error";
            message  = $"accessing the file '{path}' failed";
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

    // Its own templates rather than DirectoryIoFailure's, which say "reading the directory" —
    // accurate for listing, wrong for changing into one. As everywhere on this boundary, the text
    // is deterministic rather than ex.Message, because the native backend reproduces it from errno.
    private static FailureUnwind ChangeDirectoryFailure(string path, string kind)
    {
        var (category, message) = kind switch
        {
            "not-found"      => ("not-found",       $"the directory '{path}' was not found"),
            "not-a-directory"=> ("not-a-directory", $"'{path}' is not a directory"),
            "permission"     => ("permission-denied", $"permission denied entering directory '{path}'"),
            _                => ("disk-error",      $"changing to the directory '{path}' failed"),
        };
        return new FailureUnwind(new FailureValue(message, category));
    }

    // 'The current directory becomes <path>.' — a fallible statement, like writing to a file.
    //
    // The existence checks run BEFORE SetCurrentDirectory because .NET collapses "no such
    // directory" and "that is a file" into the same IOException, while POSIX chdir distinguishes
    // them as ENOENT and ENOTDIR. Checking here is what lets both backends produce the same
    // category — `cd` onto a file is an ordinary typo and deserves to say so.
    private void ExecuteCurrentDirectorySetStatement(CurrentDirectorySetStatement cd)
    {
        var path = (string)Evaluate(cd.Path);

        if (!Directory.Exists(path))
            throw ChangeDirectoryFailure(path, File.Exists(path) ? "not-a-directory" : "not-found");

        try
        {
            Directory.SetCurrentDirectory(path);
        }
        catch (UnauthorizedAccessException)
        {
            throw ChangeDirectoryFailure(path, "permission");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            throw ChangeDirectoryFailure(path, "other");
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

    /// <summary>
    /// The argument list of either `run` form. Exactly one of the two is populated: a literal
    /// list written at the call, or one expression yielding the whole list at run time.
    /// </summary>
    private string[] RunArguments(IReadOnlyList<IExpression> args, IExpression? argsSeries)
    {
        if (argsSeries == null) return args.Select(a => (string)Evaluate(a)).ToArray();
        var series = (CufetSeries)Evaluate(argsSeries);
        return series.Select(v => (string)v).ToArray();
    }

    private object EvaluateRunExpr(RunExpression run)
    {
        var program = (string)Evaluate(run.Program);
        var args    = RunArguments(run.Args, run.ArgsSeries);
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
                // ★ A program in charge of its own interrupts is not in charge of the CHILD’s. The terminal
                // delivered the signal to it too, and what it does about that is its own business — a shell
                // waits for it to go and prints a fresh prompt, which is what bash does. Killing it here
                // would override the child’s own answer.
                if (_interruptRequested && !_programHandlesInterrupts)
                {
                    proc.Kill(entireProcessTree: true);
                    break;
                }
            }
            Task.WaitAll(stdoutTask, stderrTask);
            // ⚠⚠ The rule the statement checkpoint in Interpreter.Core.cs states, which this path used
            // to skip: unwinding unconditionally tore a program down before it could poll, so
            // `If an interrupt is requested:` could never survive a launch. Ignore interrupts and Ctrl-C
            // behaves as it does everywhere else; handle them and you are in charge of them here too.
            if (_interruptRequested && !_programHandlesInterrupts) throw new InterruptUnwind();

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

    /// <summary>
    /// `run &lt;program&gt;.` — launch it, let it have the terminal, wait, keep nothing.
    /// </summary>
    /// <remarks>
    /// ★★ The whole difference from the expression form is the three lines NOT here: no
    /// <c>RedirectStandardOutput</c>, no <c>RedirectStandardError</c>, no reader tasks. The child
    /// inherits this process’s console, so its output streams as it happens and a program that draws
    /// — vim, less, top — has a real terminal to ask about. Capturing and discarding would have done
    /// neither: by the time there is text to discard, the child has already been handed a pipe.
    /// </remarks>
    private void ExecuteRunStatement(RunStatement run)
    {
        var program = (string)Evaluate(run.Program);
        var args    = RunArguments(run.Args, run.ArgsSeries);
        try
        {
            // ⚠ Same hazard as the compiled path: the child writes to the same descriptor this
            // program writes through, so anything still buffered here would arrive after the
            // child’s output rather than before it.
            _out.Flush();

            var psi = new ProcessStartInfo(program) { UseShellExecute = false };
            // Each argument added individually — no shell, no injection possible.
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for '{program}'");

            // ⚠ Polled rather than blocked, for the same reason the expression form polls: a blocked
            // wait cannot notice Ctrl-C, and the child must be taken down with the program.
            while (!proc.WaitForExit(50))
            {
                // ★ A program in charge of its own interrupts is not in charge of the CHILD’s. The terminal
                // delivered the signal to it too, and what it does about that is its own business — a shell
                // waits for it to go and prints a fresh prompt, which is what bash does. Killing it here
                // would override the child’s own answer.
                if (_interruptRequested && !_programHandlesInterrupts)
                {
                    proc.Kill(entireProcessTree: true);
                    break;
                }
            }
            // ⚠⚠ The rule the statement checkpoint in Interpreter.Core.cs states, which this path used
            // to skip: unwinding unconditionally tore a program down before it could poll, so
            // `If an interrupt is requested:` could never survive a launch. Ignore interrupts and Ctrl-C
            // behaves as it does everywhere else; handle them and you are in charge of them here too.
            if (_interruptRequested && !_programHandlesInterrupts) throw new InterruptUnwind();
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
            // Deterministic, platform-independent fallback (NOT ex.Message) so the native compiler's
            // errno path reproduces it bit-identically — same principle as FileIoFailure.
            category = "io-error";
            message  = $"running the program '{program}' failed";
        }
        return new FailureUnwind(new FailureValue(message, category));
    }

    private object EvaluateCastExpr(CastExpression cast)
    {
        // Foreign source, not a Cufet function — there is no body to call into, so nothing below
        // applies to it: an axiom is not fallible and has no failure to route.
        if (cast.RunsAxiom is { } axiom) return RunAxiomCall(cast.Args, axiom, cast.Line);

        // ★ An axiom that arrived as a VALUE — through a parameter, a field, a series element. The
        // checker could not name its source, so the source is fetched from the value here. Nothing
        // else differs: what is held IS the literal, so the same RunAxiomCall does the rest.
        if (cast.RunsAxiomValue)
            return RunAxiomCall(cast.Args, HeldAxiom(cast.Function, cast.Line), cast.Line);

        var result = ExecuteCallExpr(CalledFunction(cast.Function, cast.ResolvedFunctionName, cast.Line, cast.Column),
                                     cast.Args, cast.Line)
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
            Scope[trySt.ExceptionBindingKey] = new ExceptionValue(caughtEx.Message);
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
