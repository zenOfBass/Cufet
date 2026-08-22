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

        void Note(AxiomLiteral axiom, int line)
        {
            if (seen.Add(ForeignC.Identity(axiom.Language ?? "", axiom.Source, axiom.Parameters,
                                          axiom.ReturnType)))
                pending.Add((axiom, line));
        }

        AstSearch.Visit(program.Statements, node =>
        {
            // The two places an axiom RUNS: returned by name, and called with arguments.
            if (node is ReturnStatement { RunsAxiom: { } returned } ret) Note(returned, ret.Line);
            if (node is CastExpression { RunsAxiom: { } called } cast)   Note(called, cast.Line);
        });

        if (pending.Count == 0) return;

        if (ForeignRunner is not { } runner)
            throw CannotRunForeignSource(pending[0].Axiom, pending[0].Line);

        foreach (var (axiom, line) in pending)
            runner.Prepare(axiom.Language!, axiom.Source, axiom.Parameters, axiom.ReturnType!, line);
    }

    /// <summary>Runs an axiom called with arguments — `cast open-file on (path, flags)`.</summary>
    private object RunAxiomCall(CastExpression cast, AxiomLiteral axiom)
    {
        if (ForeignRunner is not { } runner) throw CannotRunForeignSource(axiom, cast.Line);

        // ⚠ Evaluated in declaration order, left to right, which is the order the compiled backend
        // evaluates its call arguments in. An argument with a side effect would otherwise happen in
        // a different order on the two backends.
        //
        // ★ Converted HERE rather than inside the runner, because the refusals are Cufet's: the
        // range check on a `number` and the sentence it raises belong to the language, and the
        // runner's job stops at putting bytes in a slot.
        var arguments = new List<object>(cast.Args.Count);
        for (int i = 0; i < cast.Args.Count; i++)
        {
            var value = Evaluate(cast.Args[i]);
            arguments.Add(axiom.Parameters[i].Type switch
            {
                // ⚠ The program's OWN number formatting, so the refusal reads the same way a
                // printed number does — and the same way the compiled backend's does.
                NumberType => ForeignC.ToForeignWhole((decimal)value, cast.Line, d => Format(d)),
                FactType   => (bool)value ? 1L : 0L,
                _          => value,   // text, already the UTF-8 the C side wants
            });
        }

        // ★ null means the source had nothing to give — a NULL `char*`. Naming that `void` is the
        // language's job, not the runner's, and it is why such an axiom must declare
        // `voidable text`: NULL is C's universal "nothing", landing in the mechanism Cufet
        // already has rather than in a new one.
        return runner.Run(axiom.Language!, axiom.Source, axiom.Parameters, axiom.ReturnType!,
                          arguments, cast.Line)
            ?? VoidValue.Instance;
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
        if (ForeignRunner is not { } runner) throw CannotRunForeignSource(axiom, line);
        return runner.Run(axiom.Language!, axiom.Source, axiom.Parameters, axiom.ReturnType!, [], line)
            ?? VoidValue.Instance;
    }

    /// <summary>The refusal for an environment with no way to compile and call foreign source.</summary>
    /// <remarks>
    /// ★ A required outcome, not a failure. The playground runs this interpreter in wasm, where no
    /// foreign call can work at all — so "this program cannot run in this environment" has to be
    /// sayable, and now it is said before the program produces any output rather than partway
    /// through it.
    /// </remarks>
    private static RuntimeException CannotRunForeignSource(AxiomLiteral axiom, int line) =>
        new($"This program calls {axiom.Language ?? "foreign"} source, which cannot run here "
          + $"(line {line}).\n\n"
          + "Foreign source is compiled and called through a C toolchain. Build the program "
          + "with 'cufet build' to run it, or run it where a C compiler is available.");

    /// <summary>An axiom declaration binds the source itself — nothing about it is evaluated.</summary>
    /// <remarks>
    /// ⚠ The value stored is the literal, and it is deliberately inert. Passing an axiom around
    /// does not run it; only returning one into a type that is not an axiom does.
    /// </remarks>
    private static object AxiomValue(AxiomLiteral axiom) => axiom;
}
