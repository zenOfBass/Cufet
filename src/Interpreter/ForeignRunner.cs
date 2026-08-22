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
    /// <summary>Runs an axiom that produces a whole number, and gives back that number.</summary>
    /// <remarks>
    /// ⚠ The one conversion the first slice carries, and it is first because it cannot be lossy:
    /// a `number` is a decimal with 28–29 significant digits, so every 64-bit integer crosses
    /// exactly. Anything the C side produces that is not a whole number is refused by the C
    /// compiler rather than truncated — see ForeignC.
    /// </remarks>
    decimal RunForWholeNumber(string language, string source, int line);
}
