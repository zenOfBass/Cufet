namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Books (bundled standard-library modules) ──────────────────────────────

    private static readonly Dictionary<string, BookValue> BuiltinBookValues = BuildBuiltinBookValues();

    private static Dictionary<string, BookValue> BuildBuiltinBookValues()
    {
        var books = new Dictionary<string, BookValue>(StringComparer.OrdinalIgnoreCase);

        // The native remainder under the Cufet layer (Prelude/math.cufe owns floor/ceiling/round
        // and the constants; their native copies are DELETED, per the migration rule).
        var mathFunctions = new Dictionary<string, Func<object[], object?>>(StringComparer.OrdinalIgnoreCase)
        {
            // Total function: the decimal overload directly — no double conversion needed.
            ["absolute value"] = args => (object)Math.Abs((decimal)args[0]),
            // Partial functions: decimal→double for the call, !IsFinite check, double→decimal back.
            // Math.Log(0) returns NegativeInfinity, not NaN — must use !IsFinite, not IsNaN.
            ["square root"]    = args => MathPartial(Math.Sqrt((double)(decimal)args[0])),
            ["log"]            = args => MathPartial(Math.Log((double)(decimal)args[0])),
            ["power"]          = args => MathPartial(Math.Pow((double)(decimal)args[0], (double)(decimal)args[1])),
        };

        books["math"] = new BookValue("math", mathFunctions,
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        // collections book — every member is written in Cufet (Prelude/collections.cufe) and
        // reaches here as an ordinary method on the book's Cufet layer; the native side
        // introduces the matrix TYPE and nothing else. The native copies are deliberately
        // DELETED, not shadowed: deleting in the same change as each migration is what makes the
        // tests PROOF the Cufet path runs — a shadowed native member would answer identically
        // and prove nothing.
        books["collections"] = new BookValue(
            "collections",
            new Dictionary<string, Func<object[], object?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        // chance book — effectful randomness. Functions are dispatched via dedicated AST nodes
        // (RandomNumber/RandomItem/RandomlyShuffled/RandomGuess) using the per-interpreter _rng.
        // Pull just registers the book in scope; all real work happens in Interpreter.Core.
        books["chance"] = new BookValue(
            "chance",
            new Dictionary<string, Func<object[], object?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        return books;
    }

    private static object? MathPartial(double result)
    {
        if (!double.IsFinite(result)) return VoidValue.Instance;
        try   { return (object)(decimal)result; }
        catch (OverflowException) { return VoidValue.Instance; }
    }

    private void ExecutePullStatement(PullStatement ps)
    {
        EnterScope();
        try
        {
            foreach (var (bookName, localName) in ps.Books)
            {
                if (BuiltinBookValues.TryGetValue(bookName, out var bookValue))
                {
                    // ★ A book with a Cufet layer — the prelude defines a module object under the
                    // book's own name — is pulled as ONE module: the layer is instantiated here
                    // (pulling INSTANTIATES) and rides on the binding, and dispatch lets its
                    // members win over the native ones. A book without a layer binds the shared
                    // native singleton as before.
                    Scope[localName] = _objectDefs.TryGetValue(bookName, out var layerDef)
                        ? new BookValue(bookValue.Name, bookValue.Functions, bookValue.Constants,
                                        (ObjectValue)InstantiateModule(layerDef, ps.Line))
                        : bookValue;
                    continue;
                }

                // ★ A MODULE: pulling INSTANTIATES it, the same way `Pull a rabbit as den.` makes a
                // region rather than naming a shared one. That is what keeps a book's singleton-ness
                // a property of books rather than of the mechanism.
                if (!_objectDefs.TryGetValue(bookName, out var moduleDef))
                    throw new RuntimeException($"Nothing named '{bookName}' to pull (line {ps.Line}).");
                Scope[localName] = InstantiateModule(moduleDef, ps.Line);
            }
            foreach (var s in ps.Body)
                Execute(s);
        }
        finally
        {
            ExitScope();
        }
    }

    /// <summary>
    /// Builds the instance a `Pull &lt;module&gt;.` binds.
    /// </summary>
    /// <remarks>
    /// ⚠ Fields have no values to give, because a pull site has nowhere to put them — `Pull
    /// greeting-kit.` names a module, not a construction. So a module is an object with no fields
    /// for now, and one with fields is refused here rather than being silently built half-empty.
    /// If a real need for pull-time arguments arises, that is the requirement that earns a change;
    /// it is not one to invent ahead of time.
    /// </remarks>
    private object InstantiateModule(ObjectDefinition def, int line)
    {
        if (def.PositionalTypes.Count > 0 || def.NamedFields.Count > 0)
            throw new RuntimeException(
                $"'{def.Name}' has fields, so it can't be pulled as a module — a pull has nowhere to "
                + $"put their values (line {line}). Build it with 'a new {def.Name} {{ ... }}' instead.");
        return BuildObjectValue(def, [], [], line);
    }

    private object? DispatchBookFunction(BookValue bv, string memberName, IReadOnlyList<IExpression> args, int line)
    {
        if (!bv.Functions.TryGetValue(memberName, out var fn))
        {
            if (bv.Constants.ContainsKey(memberName))
                throw new RuntimeException(
                    $"'{memberName}' in book '{bv.Name}' is a constant — access it via '{bv.Name}'s {memberName}' without 'of' (line {line}).");
            throw new RuntimeException($"Book '{bv.Name}' has no function '{memberName}' (line {line}).");
        }
        var argValues = args.Select(Evaluate).ToArray();
        return fn(argValues);
    }
}
