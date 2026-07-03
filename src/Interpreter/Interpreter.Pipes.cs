using System.Diagnostics;
using System.ComponentModel;

namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // Implicit pipe channels — set around each stage's execution, null outside a pipe.
    // Sequential (buffered) v1: producer fills outputChan to completion, then consumer drains it.
    private ChannelValue? _pipeInputChan;
    private ChannelValue? _pipeOutputChan;

    private void ExecutePipe(PipeExpression pipe)
    {
        var stages = FlattenPipe(pipe);

        if (stages.TrueForAll(s => s is RunExpression))
            ExecuteSubprocessPipe(stages, pipe.Line);
        else
            ExecuteTaskPipe(stages, pipe.Line);
    }

    // ── Task pipe ────────────────────────────────────────────────────────────────
    // Runs N stages sequentially: producer fills an output channel, then consumer drains it.
    // N stages need N-1 channels (one between each adjacent pair).

    private void ExecuteTaskPipe(List<IExpression> stages, int line)
    {
        var channels = Enumerable.Range(0, stages.Count - 1).Select(_ => new ChannelValue()).ToList();

        var savedInput  = _pipeInputChan;
        var savedOutput = _pipeOutputChan;

        try
        {
            for (int i = 0; i < stages.Count; i++)
            {
                _pipeInputChan  = i == 0               ? null           : channels[i - 1];
                _pipeOutputChan = i == stages.Count - 1 ? null           : channels[i];

                var funcVal = Evaluate(stages[i]);
                if (funcVal is not FunctionValue func)
                    throw new RuntimeException(
                        $"Pipe stage {i + 1} is not a function (line {line}).");

                ExecuteCall(func, $"pipe stage {i + 1}", [], line);

                // Signal to the next stage that no more values are coming.
                if (i < channels.Count)
                    channels[i].Close();
            }
        }
        finally
        {
            _pipeInputChan  = savedInput;
            _pipeOutputChan = savedOutput;
        }
    }

    // ── Subprocess pipe ──────────────────────────────────────────────────────────
    // Chains OS processes: stdout of each feeds into stdin of the next.
    // Buffered v1: each process runs to completion before the next starts.

    private void ExecuteSubprocessPipe(List<IExpression> stages, int line)
    {
        string? currentInput = null;

        foreach (var stage in stages)
        {
            var run     = (RunExpression)stage;
            var program = (string)Evaluate(run.Program);
            var args    = run.Args.Select(a => (string)Evaluate(a)).ToArray();

            var psi = new ProcessStartInfo(program)
            {
                RedirectStandardInput  = currentInput != null,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            try
            {
                using var proc = Process.Start(psi)
                    ?? throw new RuntimeException($"Failed to start '{program}' (line {line}).");

                if (currentInput != null)
                {
                    proc.StandardInput.Write(currentInput);
                    proc.StandardInput.Close();
                }

                var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

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

                var stderrOutput = stderrTask.Result;
                if (stderrOutput.Length > 0)
                    _err.Write(stderrOutput);

                currentInput = stdoutTask.Result;
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException
                                           or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                throw LaunchFailure(program, ex);
            }
        }

        if (currentInput != null)
            _out.Write(currentInput);
    }

    // ── output statement ─────────────────────────────────────────────────────────
    // Sends a value into the implicit output channel. Only valid inside a pipe stage.

    private void ExecuteOutputStatement(OutputStatement os)
    {
        if (_pipeOutputChan == null)
            throw new RuntimeException(
                $"'output' used outside of a pipe on line {os.Line} — 'output' can only appear inside a function that is used as a pipe stage.");

        var value = Evaluate(os.Value);
        _pipeOutputChan.Enqueue(DeepCopyForChannel(value));
    }

    // ── for each … from the input ────────────────────────────────────────────────
    // Drains the implicit input channel until it is closed. Only valid inside a pipe stage.

    private void ExecuteForEachFromInput(ForEachFromInputStatement fe)
    {
        if (_pipeInputChan == null)
            throw new RuntimeException(
                $"'for each from the input' used outside of a pipe on line {fe.Line} — this loop can only appear inside a function that is used as a pipe stage.");

        string iterKey = fe.IteratorName;

        while (true)
        {
            // Wait until a value is available or the channel is closed.
            while (!_pipeInputChan.HasValue && !_pipeInputChan.IsClosed)
            {
                // Yield cooperatively if the scheduler is running; otherwise spin
                // (safe in buffered v1 because producer runs to completion first).
                _scheduler?.DrainOne();
                if (_interruptRequested) throw new InterruptUnwind();
            }

            if (!_pipeInputChan.HasValue && _pipeInputChan.IsClosed)
                break; // channel drained and closed — consumer is done

            var item = _pipeInputChan.Dequeue();

            EnterScope();
            Scope[iterKey] = item;
            bool stopped = false;
            try { foreach (var s in fe.Body) Execute(s); }
            catch (StopException)  { stopped = true; }
            catch (SkipException)  { /* next item */ }
            finally { ExitScope(); }

            if (stopped) break;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

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
}
