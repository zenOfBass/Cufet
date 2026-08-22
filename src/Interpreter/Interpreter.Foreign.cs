namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Foreign interoperability: running an axiom ───────────────────────────

    /// <summary>Runs an axiom and gives back the marshalled Cufet value.</summary>
    /// <remarks>
    /// ★ Everything hard about this is on the other side of IForeignRunner. What is here is the
    /// one thing the interpreter owns: knowing that an axiom has run, and refusing clearly when
    /// nothing in this environment can run one.
    /// </remarks>
    private object RunAxiom(AxiomLiteral axiom, int line)
    {
        if (ForeignRunner is not { } runner)
            throw new RuntimeException(
                $"This program calls {axiom.Language ?? "foreign"} source, which cannot run here "
              + $"(line {line}).\n\n"
              + "Foreign source is compiled and called through a C toolchain. Build the program "
              + "with 'cufet build' to run it, or run it where a C compiler is available.");

        return runner.RunForWholeNumber(axiom.Language!, axiom.Source, line);
    }

    /// <summary>An axiom declaration binds the source itself — nothing about it is evaluated.</summary>
    /// <remarks>
    /// ⚠ The value stored is the literal, and it is deliberately inert. Passing an axiom around
    /// does not run it; only returning one into a type that is not an axiom does.
    /// </remarks>
    private static object AxiomValue(AxiomLiteral axiom) => axiom;
}
