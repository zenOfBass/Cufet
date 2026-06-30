namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    private object? EvaluateAwaitedResultExpression(AwaitedResultExpression are)
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
            _scheduler!.DrainUntil(() => task.IsCompleted);

        // Re-throw any RuntimeException (or other fault) from the task body.
        // ReturnException is caught in RunTaskBody and stored on the handle — not faulted.
        task.GetAwaiter().GetResult();

        // If the task produced a failure, throw FailureUnwind so 'but on failure' /
        // Try blocks at the await site can catch it — same pattern as EvaluateCastExpr.
        if (handle.Result is FailureValue fv)
            throw new FailureUnwind(fv);

        return handle.Result;
    }
}
