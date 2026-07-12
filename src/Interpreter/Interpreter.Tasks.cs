namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    private object EvaluateAwaitedResultExpression(AwaitedResultExpression are)
    {
        var handleObj = Evaluate(are.Task);
        if (handleObj is not TaskHandle handle)
            throw new RuntimeException(
                $"'the awaited result of' requires a named task handle (line {are.Line}). " +
                "Use 'Have rabbit start a task as <name>:' to create a named task.");

        var task = handle.CSharpTask!;

        // Yield cooperatively until the task's C# Task is complete.
        // If already done (completed task or double-await), returns immediately.
        if (!task.IsCompleted)
        {
            _scheduler!.DrainUntil(() => task.IsCompleted || _interruptRequested);
            if (_interruptRequested) throw new InterruptUnwind();
        }

        // Re-throw any RuntimeException (or other fault) from the task body.
        // ReturnException is caught in RunTaskBody and stored on the handle — not faulted.
        task.GetAwaiter().GetResult();

        // If the task produced a failure, throw FailureUnwind so 'but on failure' /
        // Try blocks at the await site can catch it — same pattern as EvaluateCastExpr.
        if (handle.Result is FailureValue fv)
            throw new FailureUnwind(fv);

        // The task's result type is inferred from its `return` statements, but the type checker
        // does not require every path to return — so a value-returning task can still fall off
        // its end without setting a result (HasResult stays false, Result stays null). Awaiting
        // that is a runtime error: turn it into a clean Cufet failure rather than letting a C#
        // null escape into Evaluate (which crashes Format with a NullReferenceException).
        if (!handle.HasResult || handle.Result is null)
            throw new RuntimeException(
                $"the awaited task finished without returning a value — there is no result to await (line {are.Line}).");

        return handle.Result;
    }
}
