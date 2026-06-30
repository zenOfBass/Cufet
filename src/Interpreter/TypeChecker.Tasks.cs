namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    private CufetType? InferAwaitedResultExpression(AwaitedResultExpression are)
    {
        var handleType = InferType(are.Task);

        if (handleType is not TaskHandleType tht)
        {
            if (handleType != null)
                throw new TypeException(FormatTypeError(
                    "'the awaited result of' requires a task handle",
                    null, are.Line,
                    $"await the result of a {FormatType(handleType)}",
                    "Use 'the awaited result of <name>' where <name> was declared with 'Have rabbit start a task as <name>:'."));
            return null; // unknown — let runtime catch it
        }

        if (tht.ResultType == null)
            throw new TypeException(FormatTypeError(
                "this task has no return value — there is no result to await",
                null, are.Line,
                "await the result of a task that never returns a value",
                "Add 'return <value>.' to the task body, or use 'Have rabbit start a task:' (no name) for fire-and-forget tasks."));

        // Inside a Try block, unwrap FailureType so the success type is usable.
        if (_inTryBlock && tht.ResultType is FailureType frt)
            return frt.Inner;

        // Strict-fallible rule: must handle the failure at the await site.
        if (tht.ResultType is FailureType && !_inFailureHandledContext)
            throw new TypeException(FormatTypeError(
                "this task can fail — you must handle the failure at the await site",
                null, are.Line,
                "use a fallible task's result without handling the failure",
                "Wrap with 'but on failure <default>', use a 'Try to:' block, or 'or pass the failure off'."));

        return tht.ResultType;
    }
}
