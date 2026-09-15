namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Books (bundled standard-library modules) ──────────────────────────────

    private static readonly Dictionary<string, BookValue> BuiltinBookValues = BuildBuiltinBookValues();

    /// <summary>FNV-1a, 64-bit, over a file's bytes — void when the file cannot be read.</summary>
    /// <remarks>
    /// ⚠⚠ THIS ALGORITHM IS WRITTEN TWICE — here, and as `cufet_checksum_file` in the emitted runtime.
    /// Two implementations of one function is exactly the divergence the oracle exists to catch,
    /// and the oracle would only catch it if some program's output happened to differ. What makes
    /// it safe is that FNV-1a has PUBLISHED TEST VECTORS, so neither side is graded by the other —
    /// both are checked against numbers nobody on this project wrote. Pinned in BlueprintChecksumTests.
    ///
    /// ★ Non-cryptographic on purpose: a build detects change, it does not resist an adversary.
    /// ⚠ 64 bits means a collision is possible in principle, and a collision is a stale build with
    /// no complaint — the failure this language declines everywhere. At a few thousand files that
    /// is around one in ten trillion, which is documented rather than pretended away.
    ///
    /// ★ Void rather than a failure for an unreadable file: a build asks about inputs that do not
    /// exist yet all the time, and "there is nothing there to checksum" is an answer, not an error.
    /// </remarks>
    private static object Checksum(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            return VoidValue.Instance;
        }

        ulong hash = 0xcbf29ce484222325;          // the FNV offset basis
        foreach (var one in bytes)
        {
            hash ^= one;
            hash *= 0x100000001b3;                // the FNV prime
        }
        return new BitsValue(hash, 'x', 64);
    }

    private static Dictionary<string, BookValue> BuildBuiltinBookValues()
    {
        var books = new Dictionary<string, BookValue>(StringComparer.OrdinalIgnoreCase);

        // ★ Nothing native left in `math` — every member, transcendentals included, is written
        // in Cufet (Prelude/math.cufe) and reaches here as an ordinary method on the book's
        // layer. The value survives only to carry the book's NAME.
        books["math"] = new BookValue("math",
            new Dictionary<string, Func<object[], object?>>(StringComparer.OrdinalIgnoreCase),
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

        // blueprints book — one native member, `checksum`. See TypeChecker.Book for why this one stays
        // native where math and collections migrated to Cufet.
        books["blueprints"] = new BookValue(
            "blueprints",
            new Dictionary<string, Func<object[], object?>>(StringComparer.OrdinalIgnoreCase)
            {
                ["checksum"] = args => Checksum(args[0] as string ?? ""),
            },
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        // chance book — effectful randomness. Functions are dispatched via dedicated AST nodes
        // (RandomNumber/RandomItem/RandomlyShuffled/RandomGuess) using the per-interpreter _rng.
        // Pull just registers the book in scope; all real work happens in Interpreter.Core.
        books["chance"] = new BookValue(
            "chance",
            new Dictionary<string, Func<object[], object?>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        // Language books — pulled to write foreign source at all, and empty by construction: a book
        // on a LANGUAGE offers no members, it admits axioms in that language. See
        // TypeChecker.Foreign, which owns the list both sides read.
        foreach (var language in TypeChecker.LanguageBookNamesForBooks())
            books[language] = new BookValue(
                language,
                new Dictionary<string, Func<object[], object?>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        return books;
    }

    // Names bound by a `Pull` that is still open — see the note in SaveScopes for what it is for.
    private readonly HashSet<string> _pulledModuleNames = new(StringComparer.Ordinal);

    /// <summary>The pulls each MODULE was written inside, by the name it was declared under.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ The module half of `FunctionValue.LexicalPulls`, and the runtime that had to land before
    /// the checker could stop charging a module's private dependencies to its caller. A module
    /// written inside `Pull a book on math.` reaches `math` while it is CHECKED — the checker's own
    /// `SaveScopes` carries the pull into a detached body — and then found nothing at run time,
    /// because `ExecuteMethod` imports the CALLER's scope. That gap is the whole reason the need
    /// was pushed onto the caller in the first place.
    /// </para>
    /// <para>
    /// ★ Keyed by NAME rather than carried on the instance, because a module is reached through an
    /// `ObjectValue` that is deep-copied at every binding site, and a capability is a property of
    /// the DECLARATION, not of any one copy of it. `_objectDefs` is keyed the same way for the same
    /// reason.
    /// </para>
    /// <para>
    /// ⚠ Only a declaration written inside a pull appears here at all, so a module declared at top
    /// level costs nothing and keeps deferring to its caller exactly as before — which is the
    /// `circles` pattern `DropUnpulledLayers` relies on.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, IReadOnlyList<(string Local, string Book)>> _moduleLexicalPulls =
        new(StringComparer.Ordinal);

    /// <summary>What a `Pull` binds for one name — and what a lexically captured pull rebinds.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ ONE PLACE, because it is now asked from two. `ExecutePullStatement` asks when the block
    /// opens; a function written inside that block asks again when it is CALLED, possibly long
    /// after the block closed. Two copies of this would be two answers to "what is a pulled name",
    /// and the divergence this exists to fix was exactly that kind of disagreement.
    /// </para>
    /// <para>
    /// ★ PULLING INSTANTIATES, and a fresh instance is semantically exact because module objects
    /// are FIELDLESS by decision — there is no state for two instances to disagree about. The
    /// compiled backend has always done this: it emits a fresh compound literal rather than the
    /// pull's binding, and its own note says that is what makes a `Bind` hoisted out of a pull body
    /// work. ⚠ If modules ever grow state, this is the line that breaks first, in both backends.
    /// </para>
    /// </remarks>
    private object PulledValue(string bookName, int line)
    {
        if (BuiltinBookValues.TryGetValue(bookName, out var bookValue))
            // ★ A book with a Cufet layer — the prelude defines a module object under the book's
            // own name — is pulled as ONE module: the layer is instantiated here and rides on the
            // binding, and dispatch lets its members win over the native ones. A book without a
            // layer binds the shared native singleton as before.
            return _objectDefs.TryGetValue(bookName, out var layerDef)
                ? new BookValue(bookValue.Name, bookValue.Functions, bookValue.Constants,
                                (ObjectValue)InstantiateModule(layerDef, line))
                : bookValue;

        // ★ A MODULE: pulling INSTANTIATES it, the same way `Pull a rabbit as den.` makes a region
        // rather than naming a shared one. That is what keeps a book's singleton-ness a property of
        // books rather than of the mechanism.
        if (!_objectDefs.TryGetValue(bookName, out var moduleDef))
            throw new RuntimeException($"Nothing named '{bookName}' to pull (line {line}).");
        return InstantiateModule(moduleDef, line);
    }

    /// <summary>Rebinds the pulls a function was written inside, for a call made outside them.</summary>
    /// <remarks>
    /// ⚠ FILLS GAPS ONLY. A pull live at the call site already carries through `SaveScopes`, and an
    /// alias there may legitimately name the same book differently — so this adds what is missing
    /// and overwrites nothing.
    /// </remarks>
    private void BindLexicalPulls(IReadOnlyList<(string Local, string Book)> pulls, int line)
    {
        foreach (var (local, book) in pulls)
            if (!Scope.ContainsKey(local))
                Scope[local] = PulledValue(book, line);
    }

    private void ExecutePullStatement(PullStatement ps)
    {
        EnterScope();
        var pulledHere = new List<string>();
        try
        {
            foreach (var (bookName, localName) in ps.Books)
            {
                if (_pulledModuleNames.Add(localName)) pulledHere.Add(localName);
                Scope[localName] = PulledValue(bookName, ps.Line);
            }
            foreach (var s in ps.Body)
                Execute(s);
        }
        finally
        {
            // Only the names THIS pull introduced — an inner pull reusing an outer's alias must
            // not un-mark it on the way out.
            foreach (var name in pulledHere) _pulledModuleNames.Remove(name);
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
