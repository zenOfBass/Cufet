namespace Cufet.Interpreter;

/// <summary>Runs a foreign axiom on behalf of the interpreter, and marshals what comes back.</summary>
/// <remarks>
/// <para>
/// ★ An INTERFACE, and injected, because of what it needs: the interpreter's answer to an axiom is
/// to compile it and call it, which wants a C toolchain — and the interpreter is the layer the
/// compiler is built on, not the other way round. The implementation therefore lives in
/// Cufet.Compiler (ForeignShim.cs) and is handed in by whoever assembles the two.
/// </para>
/// <para>
/// ★ Its absence is a real outcome, not an oversight. The playground runs this interpreter in
/// wasm, where no foreign call can work at all, and "this program cannot run in this environment"
/// has to be sayable — so a null runner gives that message rather than a crash.
/// </para>
/// </remarks>
public interface IForeignRunner
{
    /// <summary>Gets this axiom ready to be called, without calling it.</summary>
    /// <remarks>
    /// ★ Every axiom in the program is prepared BEFORE the first statement runs, because the
    /// compiled backend builds them all at build time and refuses the whole program if one will
    /// not compile. Preparing lazily meant a bad axiom late in a file printed the earlier output
    /// first interpreted and nothing at all compiled — two answers to one program.
    /// </remarks>
    void Prepare(string language, string source,
                 IReadOnlyList<(CufetType Type, string Name)> parameters, int line);

    /// <summary>Runs an axiom that produces a whole number, and gives back that number.</summary>
    /// <remarks>
    /// ⚠ The one conversion the first slice carries, and it is first because it cannot be lossy:
    /// a `number` is a decimal with 28–29 significant digits, so every 64-bit integer crosses
    /// exactly. Anything the C side produces that is not a whole number is refused by the C
    /// compiler rather than truncated — see ForeignC.
    ///
    /// ★ The parameters come along because they are what says how to marshal: the writer names no
    /// C type anywhere, so the declared Cufet types are the only description of the arguments that
    /// exists. `arguments` holds the interpreter's own values, in declaration order.
    /// </remarks>
    decimal RunForWholeNumber(string language, string source,
                              IReadOnlyList<(CufetType Type, string Name)> parameters,
                              IReadOnlyList<object> arguments, int line);
}
