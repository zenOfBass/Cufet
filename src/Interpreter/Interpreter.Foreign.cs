namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Foreign interoperability: running an axiom ───────────────────────────

    /// <summary>
    /// Builds every axiom the program can run, before the program runs at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★★ This is what keeps the two backends saying the same thing about a program that will not
    /// build. The compiled backend meets every axiom at BUILD time and refuses the whole program if
    /// one of them will not compile; the interpreter used to compile each on first use, so a bad
    /// axiom late in a file printed all the earlier output and then failed, where compiling it
    /// printed nothing. Two answers to one program — the shape the no-divergence rule exists for.
    /// </para>
    /// <para>
    /// ★ The set is the returns that RUN an axiom, not every axiom literal, because that is exactly
    /// what the compiler emits a wrapper for. An axiom declared and never returned is compiled by
    /// neither backend, so preparing it here would refuse a program the compiler builds happily —
    /// the same divergence pointing the other way.
    /// </para>
    /// <para>
    /// ★ Deduplicated by content, so the same axiom returned in three places is built once. That
    /// also matches the compiled side, which names a wrapper after a hash of its source.
    /// </para>
    /// <para>
    /// ⚠ The walk is `AstSearch`, keyed on the namespace — a hand-written one would silently miss
    /// whatever node type nobody remembered, and "silently prepared fewer axioms" reads as the
    /// original bug rather than as a walk that went stale.
    /// </para>
    /// </remarks>
    private void PrepareForeignSource(Program program)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<(AxiomLiteral Axiom, int Line)>();
        bool carriesAxiomValues = false;

        void Note(AxiomLiteral axiom, int line)
        {
            if (seen.Add(ForeignC.Identity(axiom.Language ?? "", axiom.Source, axiom.Parameters,
                                          axiom.ReturnType)))
                pending.Add((axiom, line));
            // ⚠ The release axiom may be named ONLY by `and free it with`, never called by hand —
            // and it still has to be built before the program starts, or the first block exit pays
            // for a gcc run and a bad one fails partway through a program that had produced output.
            if (axiom.ReleaseAxiom is { } release) Note(release, line);
        }

        AstSearch.Visit(program.Statements, node =>
        {
            // The two places an axiom RUNS: returned by name, and called with arguments.
            if (node is ReturnStatement { RunsAxiom: { } returned } ret) Note(returned, ret.Line);
            if (node is CastExpression { RunsAxiom: { } called } cast)   Note(called, cast.Line);
            // ⚠ And called for its EFFECT. Miss this and a program whose only axiom is discarded
            // compiles its shim lazily on first use again — the divergence up-front preparation
            // exists to close.
            if (node is CastStatement  { RunsAxiom: { } run }     stmt)   Note(run, stmt.Line);
            // ★ And called through a VALUE, where there is no source to name. The callee is decided
            // at run time, so which axiom this reaches cannot be known from here.
            if (node is CastExpression { RunsAxiomValue: true } or CastStatement { RunsAxiomValue: true })
                carriesAxiomValues = true;
        });

        // ⚠ Deliberately imprecise, and the imprecision is the safe direction. A value-carried call
        // could reach any axiom the program declares, so once one exists, every DECLARED axiom that
        // could be run has to be built up front — building too many costs one gcc invocation, while
        // building too few fails partway through a program that had already produced output, which
        // is exactly what up-front preparation exists to prevent.
        //
        // ★ Gated on a value-carried call existing at all, so a program without one is unchanged:
        // declaring an axiom and never running it still needs no toolchain.
        if (carriesAxiomValues)
            AstSearch.Visit(program.Statements, node =>
            {
                // An axiom with no declared result cannot be wrapped — there is no C return type to
                // build one from — and the checker has already refused every way of running it.
                if (node is DefineStatement { Value: AxiomLiteral { ReturnType: not null } declared } def)
                    Note(declared, def.Line);
            });

        // ★ Resultless axioms are SOURCE, not calls: collected separately, declared first, and never
        // prepared — there is nothing to invoke. Order matters, and it is why they are gathered in
        // their own pass: a shim is cached by content, so preparing an axiom before the preamble it
        // names would cache one that cannot compile.
        var declared = new List<(string Language, string Source, int Line)>();
        AstSearch.Visit(program.Statements, node =>
        {
            if (node is DefineStatement { Value: AxiomLiteral { ReturnType: null } decl } def
                && decl.Language is { } declLanguage)
                declared.Add((declLanguage, decl.Source, def.Line));
        });

        if (pending.Count == 0 && declared.Count == 0) return;

        if (ForeignRunner is not { } runner)
            throw pending.Count > 0
                ? CannotRunForeignSource(pending[0].Axiom.Language, pending[0].Line)
                : CannotRunForeignSource(declared[0].Language, declared[0].Line);

        foreach (var (language, source, line) in declared)
            runner.Declare(language, source, line);

        foreach (var (axiom, line) in pending)
            runner.Prepare(axiom.Language!, axiom.Source, axiom.Parameters, axiom.ReturnType!, line);
    }

    // Foreign addresses awaiting release, and the block depth each was acquired at. The C twin is
    // `cufet_um_obj`/`cufet_um_fn` with `cufet_num` as the snapshot — same registry, same LIFO
    // order, same rule that a block exit runs back to its own base.
    private readonly List<(nint Handle, AxiomLiteral Release, int Line)> _pendingReleases = [];
    private readonly List<int> _scopeReleaseBase = [];

    /// <summary>Registers a freshly ACQUIRED address with the axiom that frees it, if one was named.</summary>
    /// <remarks>
    /// ★★ Keyed on the axiom that RAN, at the moment it ran. It used to be keyed on the `Define`
    /// that caught the result, which tied a property of the axiom to a property of the call site,
    /// and three things fell out of that — the same three the compiled twin in EmitForeignAddress
    /// records:
    ///
    ///   1. An acquisition nobody named leaked. `Cast copy-of on ("x").` allocated and registered
    ///      nothing, as did one used inline in a condition.
    ///   2. An axiom with a release clause could not be passed around: a call reached through a
    ///      value has no `Define` to hang the registration on.
    ///   3. It was the more dangerous half of the trade it claimed to make. Registering per
    ///      BINDING is what risks a double free — names multiply and can reach one pointer — while
    ///      registering per ACQUISITION happens exactly once per allocation by construction.
    ///
    /// ⚠ A void result is not registered: NULL is C saying it had nothing to give, and freeing
    /// that is undefined rather than tidy.
    /// </remarks>
    private void RegisterForeignRelease(AxiomLiteral acquired, object? value)
    {
        if (value is not ForeignAddress address) return;
        if (acquired.ReleaseAxiom is not { } release) return;
        _pendingReleases.Add((address.Handle, release, acquired.Line));
    }

    /// <summary>Frees everything acquired since <paramref name="base"/>, newest first.</summary>
    /// <remarks>
    /// ⚠ LIFO, like the unmakers beside it, and for the same reason: a handle acquired later may
    /// depend on one acquired earlier.
    /// </remarks>
    private void RunForeignReleases(int @base)
    {
        while (_pendingReleases.Count > @base)
        {
            var (handle, release, line) = _pendingReleases[^1];
            _pendingReleases.RemoveAt(_pendingReleases.Count - 1);
            if (ForeignRunner is not { } runner) continue;
            runner.Run(release.Language!, release.Source, release.Parameters, release.ReturnType!,
                       [new ForeignAddress(handle)], line);
        }
    }

    /// <summary>Runs an axiom called with arguments — `cast open-file on (path, flags)`.</summary>
    private object RunAxiomCall(IReadOnlyList<IExpression> callArgs, AxiomLiteral axiom, int line)
    {
        if (ForeignRunner is not { } runner) throw CannotRunForeignSource(axiom.Language, line);

        // ⚠ Evaluated in declaration order, left to right, which is the order the compiled backend
        // evaluates its call arguments in. An argument with a side effect would otherwise happen in
        // a different order on the two backends.
        //
        // ★ Converted HERE rather than inside the runner, because the refusals are Cufet's: the
        // range check on a `number` and the sentence it raises belong to the language, and the
        // runner's job stops at putting bytes in a slot.
        var arguments = new List<object>(callArgs.Count);
        for (int i = 0; i < callArgs.Count; i++)
        {
            var value = Evaluate(callArgs[i]);
            arguments.Add(axiom.Parameters[i].Type switch
            {
                // ⚠ The program's OWN number formatting, so the refusal reads the same way a
                // printed number does — and the same way the compiled backend's does.
                NumberType => ForeignC.ToForeignWhole((decimal)value, line, d => Format(d)),
                FactType   => (bool)value ? 1L : 0L,
                _          => value,   // text, already the UTF-8 the C side wants
            });
        }

        // ★ null means the source had nothing to give — a NULL `char*`. Naming that `void` is the
        // language's job, not the runner's, and it is why such an axiom must declare
        // `voidable text`: NULL is C's universal "nothing", landing in the mechanism Cufet
        // already has rather than in a new one.
        var produced = runner.Run(axiom.Language!, axiom.Source, axiom.Parameters, axiom.ReturnType!,
                                  arguments, line);
        RegisterForeignRelease(axiom, produced);
        return produced ?? VoidValue.Instance;
    }

    /// <summary>Runs an axiom and gives back the marshalled Cufet value.</summary>
    /// <remarks>
    /// ★ Everything hard about this is on the other side of IForeignRunner. What is here is the
    /// one thing the interpreter owns: knowing that an axiom has run.
    ///
    /// ⚠ The runner check stays even though PrepareForeignSource already made it. Execute is the
    /// only route that prepares, and a guard that reads "this cannot be null by now" is how a
    /// second entry point one day gets a null-reference instead of a sentence.
    /// </remarks>
    private object RunAxiom(AxiomLiteral axiom, int line)
    {
        if (ForeignRunner is not { } runner) throw CannotRunForeignSource(axiom.Language, line);
        var produced = runner.Run(axiom.Language!, axiom.Source, axiom.Parameters,
                                  axiom.ReturnType!, [], line);
        RegisterForeignRelease(axiom, produced);
        return produced ?? VoidValue.Instance;
    }

    /// <summary>The refusal for an environment with no way to compile and call foreign source.</summary>
    /// <remarks>
    /// ★ A required outcome, not a failure. The playground runs this interpreter in wasm, where no
    /// foreign call can work at all — so "this program cannot run in this environment" has to be
    /// sayable, and now it is said before the program produces any output rather than partway
    /// through it.
    /// </remarks>
    private static RuntimeException CannotRunForeignSource(string? language, int line) =>
        new($"This program calls {language ?? "foreign"} source, which cannot run here "
          + $"(line {line}).\n\n"
          + "Foreign source is compiled and called through a C toolchain. Build the program "
          + "with 'cufet build' to run it, or run it where a C compiler is available.");

    /// <summary>The axiom a value-carried call reaches — evaluated, not looked up by name.</summary>
    /// <remarks>
    /// ★ The whole reason an axiom needs no new runtime representation on this backend: the value a
    /// name holds IS the AxiomLiteral (see <see cref="AxiomValue"/>), so passing one through a
    /// parameter, a field or a series element carries the source with it and there is nothing to
    /// resolve. The compiled backend has to work for this — it has only C text, and the value there
    /// is a function pointer to the wrapper.
    ///
    /// ⚠ The guard is not defensive noise. The checker admits this call only when the callee's type
    /// is an AxiomType, so anything else here is a checker bug — and a cast to AxiomLiteral would
    /// report it as an InvalidCastException from inside the interpreter rather than as the thing
    /// that went wrong.
    /// </remarks>
    private AxiomLiteral HeldAxiom(IExpression callee, int line) =>
        Evaluate(callee) as AxiomLiteral
        ?? throw new RuntimeException(
            $"this call expected foreign source and found something else (line {line}).");

    /// <summary>An axiom declaration binds the source itself — nothing about it is evaluated.</summary>
    /// <remarks>
    /// ⚠ The value stored is the literal, and it is deliberately inert. Passing an axiom around
    /// does not run it; only returning one into a type that is not an axiom does.
    /// </remarks>
    private static object AxiomValue(AxiomLiteral axiom) => axiom;
}
