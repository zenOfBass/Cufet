namespace Cufet.Interpreter;

/// <summary>The C both backends put around a foreign axiom.</summary>
/// <remarks>
/// <para>
/// ★★ One place, because the boundary is the part that can silently disagree. The compiled backend
/// pastes an axiom into its own C and the interpreter compiles the same axiom into a shim; if those
/// two wrapped it differently — a different cast, a different guard — the same program would give
/// two answers and both would look right. Writing the wrapper once makes that impossible rather
/// than unlikely, which is the same reasoning the shared case table records.
/// </para>
/// <para>
/// ⚠ Both backends take the value through `(long long)` after the SAME guard, so what each one
/// converts to a decimal is the identical integer. The decimal construction that follows differs in
/// spelling (C builds a CufetDec, C# builds a System.Decimal) and cannot differ in result: a
/// decimal holds every 64-bit integer exactly, so neither side rounds.
/// </para>
/// </remarks>
public static class ForeignC
{
    /// <summary>Names the guard macro every wrapped axiom is checked by.</summary>
    public const string WholeGuardName = "CUFET_C_WHOLE";

    /// <summary>What C an axiom can assume is already included.</summary>
    /// <remarks>
    /// <para>
    /// ★★ ONE list, emitted by both backends, because "which headers an axiom sees" is a language
    /// question and not a property of how the program was run. Two lists would mean an axiom that
    /// compiles built and fails interpreted — a divergence in what the program IS, which is the
    /// class this project refuses outright.
    /// </para>
    /// <para>
    /// ★★ **A generous FIXED set, rather than letting a writer name headers, and the reason is
    /// linking.** Everything below links by default: libc and the POSIX headers need no flag, and
    /// mingw links kernel32/user32/advapi32 for `windows.h` on its own. A THIRD-PARTY library does
    /// not — `#include &lt;sqlite3.h&gt;` gets the declarations and then the link fails with
    /// "undefined reference", measured here as `__imp_socket` before `-lws2_32` was added. So
    /// header control on its own would hand someone a feature that cannot work for the case that
    /// makes them want it. If it ever comes, it comes as "this needs library X" — headers AND link
    /// flags, together — and the trigger is the first person who wants a non-system library.
    /// </para>
    /// <para>
    /// ⚠ **The split is MEASURED, not assumed** (`gcc -fsyntax-only`, mingw-w64 15.1 and Linux gcc
    /// 16.1): Linux has every header here, and mingw has none of the ten in the POSIX branch. Guard
    /// it wrong and every Windows build of an axiom-bearing program fails on the include, not on
    /// the axiom.
    /// </para>
    /// <para>
    /// ★ Refusing a missing header IS the design. A program calling `tcgetattr` is POSIX-only
    /// whatever Cufet does about it, and DESIGN already declined to warn about that — because a
    /// function absent on this platform fails loudly and early, which is the opposite of the silent
    /// wrong answer the guardrails exist for. On Windows the header is simply not there and gcc
    /// says so.
    /// </para>
    /// <para>
    /// ⚠ **`&lt;windows.h&gt;` is not free, and the price is measured**: the common set preprocesses in
    /// ~98 ms and the Windows trio takes it to ~749 ms, which shows up as a `cufet build` going
    /// from ~618 ms to ~1300 ms. Only a program that CONTAINS an axiom pays it, and it buys the
    /// entire Win32 API — without it the Windows half of "the POSIX and Windows APIs" is not
    /// reachable at all. `WIN32_LEAN_AND_MEAN` is already trimming it. The interpreter pays once
    /// ever per axiom rather than per run, because its shim is content-cached.
    /// </para>
    /// <para>
    /// Re-including is free. The compiled backend has already pulled most of these in through the
    /// runtime header, and include guards make the second pass a no-op.
    /// </para>
    /// </remarks>
    public const string Headers =
"""
#define _GNU_SOURCE   /* expose POSIX regardless of -std, exactly as the runtime does */

/* Everywhere — C standard library, plus the POSIX headers mingw does ship. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <errno.h>
#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include <inttypes.h>
#include <limits.h>
#include <math.h>
#include <time.h>
#include <signal.h>
#include <fcntl.h>
#include <unistd.h>
#include <dirent.h>
#include <sys/types.h>
#include <sys/stat.h>

#if defined(_WIN32)
/* WIN32_LEAN_AND_MEAN keeps <windows.h> from pulling the ORIGINAL winsock in, which would then
   collide with winsock2 below; NOMINMAX stops min/max becoming macros over ordinary C. Both must
   be defined before the include, and winsock2 must precede windows.h — verified by compiling this
   exact order after <pthread.h>, which is what the concurrency runtime puts above it. */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <io.h>
#include <process.h>
#include <direct.h>
#else
/* POSIX-only: mingw has none of these, which is why the branch exists. Job control, raw terminal
   mode and sockets — the three things the FFI item names — all live here. */
#include <termios.h>
#include <poll.h>
#include <sys/wait.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>
#include <netdb.h>
#endif
""";

    /// <summary>
    /// The guard macro: is this C expression a whole number that crosses into a Cufet `number`
    /// without losing anything?
    /// </summary>
    /// <remarks>
    /// ★ `_Generic` on the expression's own type, so nothing has to be declared about the axiom.
    /// The writer does not name a C return type anywhere — C types live in C — and the compiler
    /// already knows the one thing that matters, which is what the expression's type actually is.
    ///
    /// ⚠ `unsigned long` and `unsigned long long` are deliberately ABSENT. They are the two types
    /// that can hold a value `long long` cannot, so passing one through the cast below could turn a
    /// large `size_t` into a negative number — silently, which is the failure this project keeps
    /// refusing. They are refused at C compile time instead, and the conversion that admits them is
    /// a later slice's work rather than a rounding of this one.
    ///
    /// ⚠ A `double` is refused here too, and for a different reason: `number` is base-10 and a
    /// `double` is base-2, so that conversion has to be written once, in C, and called by both
    /// backends. Until it is, truncating would be the only other option.
    /// </remarks>
    public const string GuardMacro =
"""
/* Is this C expression a whole number that fits a Cufet `number` exactly?
   Deliberately excludes unsigned long / unsigned long long (they can exceed long long) and every
   floating type (base-2 against a base-10 decimal). Both are refused rather than truncated. */
#define CUFET_C_WHOLE(x) _Generic((x), \
    _Bool: 1, char: 1, signed char: 1, unsigned char: 1, \
    short: 1, unsigned short: 1, int: 1, unsigned int: 1, \
    long: 1, long long: 1, default: 0)
""";

    /// <summary>The refusal a C compiler prints when an axiom produces something that cannot cross.</summary>
    public const string WholeGuardMessage =
        "this axiom is returned as a number, so it has to produce a C whole number that fits in a "
      + "long long (not a float, a double, or an unsigned 64-bit value)";

    /// <summary>The guard, as a statement to put at the top of a wrapped axiom's body.</summary>
    public static string GuardStatement(string source) =>
        $"    _Static_assert({WholeGuardName}({source}), \"{WholeGuardMessage}\");";

    /// <summary>The axiom's value, taken as the whole number both backends then convert.</summary>
    public static string WholeExpression(string source) => $"(long long)({source})";

    /// <summary>The parameter list a wrapped axiom's C function declares.</summary>
    /// <remarks>
    /// ★ Both backends declare the SAME list in the same order. The compiled side calls it
    /// directly and the shim unpacks an argument array into it, but what the foreign text sees is
    /// one set of C locals with one set of C types — which is the whole reason the wrapper is
    /// written once here rather than twice.
    /// </remarks>
    public static string ParameterList(IReadOnlyList<(CufetType Type, string Name)> parameters) =>
        parameters.Count == 0
            ? "void"
            : string.Join(", ", parameters.Select((p, i) => $"{ParameterCType(p.Type)} {ParameterName(i)}"));

    /// <summary>What every wrapped axiom's C function name starts with.</summary>
    /// <remarks>
    /// ⚠ Load-bearing beyond naming: it is how a gcc failure is told apart from a code-generator
    /// bug. Everything else in the generated C was written by cufet, so gcc objecting to it is this
    /// compiler's fault — but an axiom is the AUTHOR'S C, and blaming cufet for it would send
    /// someone hunting a bug that is theirs to fix. See GccInvoker.BuildFailureMessage.
    /// </remarks>
    public const string FunctionPrefix = "cufet_axiom_";

    /// <summary>The fixed entry point a whole-number shim exports, and the name gcc reports in it.</summary>
    public const string WholeEntryPoint = "cufet_shim_whole";

    /// <summary>Does this compiler complaint point at foreign source rather than generated code?</summary>
    public static bool BlamesForeignSource(string compilerOutput) =>
        compilerOutput.Contains(FunctionPrefix, StringComparison.Ordinal)
     || compilerOutput.Contains(WholeEntryPoint, StringComparison.Ordinal);

    /// <summary>Everything that makes one axiom distinct from another.</summary>
    /// <remarks>
    /// ⚠ The parameter TYPES are part of it, not just the text. Two axioms can share a body and
    /// differ only in what they are handed — `[write(the fd, the data, 1)]` over a number and over
    /// a text are different C functions — and keying on the source alone would wrap the first one
    /// and silently call it for the second.
    /// </remarks>
    public static string Identity(string language, string source,
                                  IReadOnlyList<(CufetType Type, string Name)> parameters)
        => $"{language}\0{ParameterList(parameters)}\0{Splice(source, parameters)}";

    /// <summary>A stable C identifier for one axiom, so the same axiom is wrapped once.</summary>
    public static string FunctionName(string language, string source,
                                      IReadOnlyList<(CufetType Type, string Name)> parameters)
    {
        var material = System.Text.Encoding.UTF8.GetBytes(Identity(language, source, parameters));
        var hash = System.Security.Cryptography.SHA256.HashData(material);
        return FunctionPrefix + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    // ── Parameters, and splicing them into the foreign text ──────────────────

    /// <summary>The C name a spliced parameter becomes. Positional, so a Cufet name never leaks.</summary>
    /// <remarks>
    /// ⚠ Positional and prefixed, deliberately. Using the writer's own name would put an arbitrary
    /// Cufet identifier into C's namespace, where it can collide with a macro, a typedef or a
    /// function the axiom is calling — `the count` next to a library's `count` is not a hypothetical.
    /// The writer never sees these names: they read `the path` and the substitution is invisible.
    /// </remarks>
    public static string ParameterName(int index) => $"cufet_p{index}";

    /// <summary>The C type a Cufet parameter arrives as.</summary>
    /// <remarks>
    /// ★ One number type survives the boundary, as it has survived everything else, so `number`
    /// arrives as `long long` and C narrows it wherever it wants an `int`. `text` arrives as the
    /// UTF-8 bytes Cufet already stores, valid for the length of the call and no longer.
    /// </remarks>
    public static string ParameterCType(CufetType type) => type switch
    {
        NumberType => "long long",
        FactType   => "int",
        TextType   => "const char*",
        _          => throw new TypeException(
                          $"a {TypeChecker.FormatType(type)} cannot be handed to foreign source yet — "
                        + "a number, a fact and a text can."),
    };

    /// <summary>Is this a type an axiom can be handed?</summary>
    public static bool CanPassToForeign(CufetType type) => type is NumberType or FactType or TextType;

    /// <summary>The foreign text with each `the &lt;parameter&gt;` replaced by its C name.</summary>
    /// <remarks>
    /// <para>
    /// ★ Only DECLARED parameters are substituted, and everything else is left exactly as written.
    /// The alternative — refusing every `the &lt;word&gt;` that is not a parameter — would refuse
    /// ordinary prose in a comment (`/* the caller owns this */`), and a genuine typo still fails
    /// loudly: `the paht` is not valid C, so the C compiler rejects it and the message is reported
    /// against the foreign source.
    /// </para>
    /// <para>
    /// ⚠ **Known edge, and it is the design's:** `the path` inside a foreign STRING literal is
    /// substituted too — `[printf("the path is %s", the path)]` has one hole and one piece of
    /// prose. Every candidate marker shared this, so it separated none of them, but it is real.
    /// </para>
    /// <para>
    /// ⚠ Longest name first. `the read` and `the read-only` can both be declared, and substituting
    /// the shorter one first would leave `cufet_p0-only` behind.
    /// </para>
    /// </remarks>
    public static string Splice(string source, IReadOnlyList<(CufetType Type, string Name)> parameters)
    {
        var byLength = parameters
            .Select((p, index) => (p.Name, index))
            .OrderByDescending(p => p.Name.Length)
            .ToList();

        foreach (var (name, index) in byLength)
            source = SplicePattern(name).Replace(source, ParameterName(index));
        return source;
    }

    /// <summary>Does the foreign text mention this parameter at all?</summary>
    public static bool Mentions(string source, string name) => SplicePattern(name).IsMatch(source);

    // `the <name>`, with the article case-insensitive and the name exact — Cufet names are
    // case-sensitive, the article is a word. The boundaries treat '-' as part of a name, because it
    // is one in Cufet: without that, `the read` would match inside `the read-only`.
    private static System.Text.RegularExpressions.Regex SplicePattern(string name) =>
        new($@"(?<![A-Za-z0-9_-])[Tt][Hh][Ee]\s+{System.Text.RegularExpressions.Regex.Escape(name)}(?![A-Za-z0-9_-])");

    // ── Handing a Cufet value TO foreign source ─────────────────────────────

    /// <summary>What a program is told when a `number` argument is not a whole number.</summary>
    /// <remarks>
    /// ★★ The messages live here, in one place, because both backends raise them: the compiled one
    /// through `cufet_foreign_ll` in the emitted runtime and the interpreter through
    /// `ToForeignWhole` below. Two spellings of one refusal is a divergence in what a program SAYS,
    /// which the oracle compares as strictly as it compares answers.
    /// </remarks>
    public const string WholeArgumentMessage =
        "Foreign source takes whole numbers, but got {0}. This happened on line {1}.";

    /// <summary>What a program is told when a `number` argument will not fit a 64-bit integer.</summary>
    public const string LargeArgumentMessage =
        "{0} is too large to hand to foreign source. This happened on line {1}.";

    /// <summary>A `number` on its way into foreign source — range-checked, never truncated.</summary>
    /// <remarks>
    /// ⚠ The C twin of this is `cufet_foreign_ll` in the emitted runtime, and the two have to
    /// refuse exactly the same values. It is a RANGE CHECK rather than a conversion, which is what
    /// makes that provable: a decimal either is a whole number inside 64 bits or it is not, and
    /// there is no rounding for the two to disagree about. Truncating instead would hand C a
    /// different number than the program said — silently, which is the failure this refuses.
    /// </remarks>
    public static long ToForeignWhole(decimal value, int line, Func<decimal, string> format)
    {
        if (decimal.Truncate(value) != value)
            throw new RuntimeException(string.Format(WholeArgumentMessage, format(value), line));
        if (value < long.MinValue || value > long.MaxValue)
            throw new RuntimeException(string.Format(LargeArgumentMessage, format(value), line));
        return (long)value;
    }

    /// <summary>The union one argument travels in, and the entry point's own signature.</summary>
    /// <remarks>
    /// ★ ONE slot shape for every argument, which is what keeps the interpreter's side to a single
    /// fixed delegate. Scalars and pointers all pass the same way — the design keeps it that way on
    /// purpose, since foreign pointers are opaque and structs therefore arrive AS pointers rather
    /// than by value. That is also the line where libffi would start earning its keep, and it is
    /// deliberately not crossed yet.
    /// </remarks>
    public const string ShimArgumentType =
"""
typedef union { long long whole; const char* text; } CufetShimArg;
""";

    /// <summary>The locals a shim unpacks its argument array into, one per declared parameter.</summary>
    /// <remarks>
    /// ★ Unpacked into NAMED locals with the wrapper's own C types, so what the foreign text sees
    /// is identical to what it sees compiled — same names, same types, same order. The array is an
    /// interpreter detail that stops at this line.
    /// </remarks>
    public static string ShimUnpack(IReadOnlyList<(CufetType Type, string Name)> parameters)
    {
        var lines = new System.Text.StringBuilder();
        for (int i = 0; i < parameters.Count; i++)
        {
            string slot = parameters[i].Type is TextType ? "text" : "whole";
            string cast = parameters[i].Type is FactType ? "(int)" : "";
            lines.AppendLine(
                $"    {ParameterCType(parameters[i].Type)} {ParameterName(i)} = {cast}cufet_args[{i}].{slot};");
        }
        return lines.ToString();
    }

    /// <summary>The comment that keeps the foreign text findable in generated output.</summary>
    public static string Banner(string language, string source)
    {
        // One line, and the source is already inside a C comment — so a `*/` in it would end the
        // comment early. Nothing else about the text is touched; it is reproduced below verbatim.
        var oneLine = source.Replace("\r", " ").Replace("\n", " ").Replace("*/", "* /");
        return $"/* {language} axiom: {oneLine} */";
    }
}
