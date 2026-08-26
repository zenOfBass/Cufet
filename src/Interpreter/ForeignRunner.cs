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
    /// <summary>Declares source every later axiom in this language can see.</summary>
    /// <remarks>
    /// ★★ A resultless axiom is SOURCE, not a call — there is nothing to invoke and nothing to
    /// marshal, so it does not go through Prepare. It is accumulated and pasted into every shim
    /// this language builds afterwards, which is the interpreted twin of the compiled backend
    /// pasting it above the wrappers.
    ///
    /// ⚠ Declared BEFORE anything is prepared, and that ordering is load-bearing: a shim is cached
    /// by its content, so preparing an axiom before the preamble it needs would cache a shim that
    /// cannot compile and hand back that failure forever.
    /// </remarks>
    void Declare(string language, string source, int line);

    /// <summary>Gets this axiom ready to be called, without calling it.</summary>
    /// <remarks>
    /// ★ Every axiom in the program is prepared BEFORE the first statement runs, because the
    /// compiled backend builds them all at build time and refuses the whole program if one will
    /// not compile. Preparing lazily meant a bad axiom late in a file printed the earlier output
    /// first interpreted and nothing at all compiled — two answers to one program.
    /// </remarks>
    void Prepare(string language, string source,
                 IReadOnlyList<(CufetType Type, string Name)> parameters,
                 CufetType result, int line);

    /// <summary>Runs an axiom and gives back the Cufet value it produced.</summary>
    /// <remarks>
    /// ★ The parameters and the result come along because they are what says how to marshal: the
    /// writer names no C type anywhere, so the declared Cufet types are the only description of
    /// this call that exists. `arguments` holds the interpreter's own values, in declaration order.
    ///
    /// ⚠ Nothing here decides a conversion. The wrapper the shim compiles is the same one the
    /// compiled backend emits, so every choice that could be argued about — which cast, which
    /// guard — was made once in ForeignC and made for both.
    /// </remarks>
    /// <returns>The Cufet value, or null when the source had nothing to give.</returns>
    object? Run(string language, string source,
               IReadOnlyList<(CufetType Type, string Name)> parameters,
               CufetType result, IReadOnlyList<object> arguments, int line);
}
