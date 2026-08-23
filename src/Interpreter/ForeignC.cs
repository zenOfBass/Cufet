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
/// ⚠ Both backends take the value through the SAME guard and receive the SAME
/// <see cref="WholeResultCType"/> pair — the bits, and whether to read them as unsigned — so what
/// each one converts to a decimal is the identical integer. The decimal construction that follows
/// differs in spelling (C builds a CufetDec, C# builds a System.Decimal) and cannot differ in
/// result: a decimal holds every 64-bit integer, signed or unsigned, exactly, so neither side
/// rounds.
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
    /// ⚠ `unsigned long` and `unsigned long long` are IN, and they are the only two that need a
    /// second question asked about them: they are the types that can hold a value `long long`
    /// cannot, so a large `size_t` taken through a plain `(long long)` would read back negative —
    /// silently, which is the failure this project keeps refusing. `CUFET_C_UNSIGNED` is what makes
    /// admitting them safe: the boundary carries the bits AND how to read them, rather than
    /// guessing. Every other unsigned type (`unsigned int` and below) fits `long long` outright, so
    /// its value converts exactly either way and it does not need the flag.
    ///
    /// ⚠ A floating value is refused HERE and admitted elsewhere: it is returned as a
    /// `voidable number`, through <see cref="RealGuardName"/> and the one shared conversion. The
    /// two guards are disjoint on purpose, so declaring the wrong one of the two is refused rather
    /// than quietly converted.
    /// </remarks>
    public const string GuardMacro =
"""
/* Is this C expression a whole number that fits a Cufet `number` exactly?
   Every C integer type qualifies — a decimal holds all 64 bits of the widest of them exactly.
   Floating types are excluded (base-2 against a base-10 decimal) and refused rather than truncated.

   ⚠ VARIADIC, and that is not decoration. Foreign text can contain a top-level comma — the comma
   operator, `[open(the p, 0), 1]` — and a one-parameter macro splits on it before expanding, so
   the guard failed to compile on source that was perfectly good C. `__VA_ARGS__` puts it back
   together. */
#define CUFET_C_WHOLE(...) _Generic((__VA_ARGS__), \
    _Bool: 1, char: 1, signed char: 1, unsigned char: 1, \
    short: 1, unsigned short: 1, int: 1, unsigned int: 1, \
    long: 1, long long: 1, unsigned long: 1, unsigned long long: 1, default: 0)

/* Must these 64 bits be read as UNSIGNED? Only the two types that can exceed long long — every
   narrower unsigned type has a value long long holds outright, so its bits read back the same
   either way and flagging it would change nothing. `size_t` is `unsigned long` on Linux and
   `unsigned long long` on Windows, which is why both are here rather than one.

   ★ Like the guard, this never EVALUATES the expression: a generic selection leaves its
   controlling expression and every unselected association unevaluated, so an axiom with a side
   effect still runs exactly once however many of these it passes through. */
#define CUFET_C_UNSIGNED(...) _Generic((__VA_ARGS__), \
    unsigned long: 1, unsigned long long: 1, default: 0)

/* Is this C expression a floating-point value? The counterpart of CUFET_C_WHOLE, and the two are
   deliberately DISJOINT: a whole number is returned as a `number` and a floating one as a
   `voidable number`, so declaring the wrong one is refused here rather than quietly converted. */
#define CUFET_C_REAL(...) _Generic((__VA_ARGS__), \
    float: 1, double: 1, long double: 1, default: 0)

/* Is this C expression a string Cufet can copy out? Only the two spellings of one: a pointer to
   char. An `unsigned char*` is bytes rather than text, and everything else is not a string at all
   — both are refused here rather than reinterpreted. */
#define CUFET_C_TEXT(...) _Generic((__VA_ARGS__), char*: 1, const char*: 1, default: 0)
""";

    /// <summary>Names the macro that says whether a whole number's bits are unsigned.</summary>
    public const string UnsignedGuardName = "CUFET_C_UNSIGNED";

    /// <summary>The refusal a C compiler prints when an axiom produces something that cannot cross.</summary>
    public const string WholeGuardMessage =
        "this axiom is returned as a number, so it has to produce a C whole number "
      + "(not a float or a double)";

    /// <summary>The guard, as a statement to put at the top of a wrapped axiom's body.</summary>
    public static string GuardStatement(string source) =>
        $"    _Static_assert({WholeGuardName}({source}), \"{WholeGuardMessage}\");";

    /// <summary>The C type a whole-number axiom hands back: the bits, and how to read them.</summary>
    /// <remarks>
    /// <para>
    /// ★★ **A flag rather than a wider channel, because 64 bits is what a `long long` return has.**
    /// Admitting `size_t` means admitting values in [2^63, 2^64), which no signed 64-bit return can
    /// carry — and the alternatives were worse. Returning `__int128` puts the answer's ABI in
    /// question on a boundary the interpreter crosses through a delegate; refusing large values at
    /// run time would put a limit in the language for the convenience of the plumbing. Handing back
    /// the bits alongside the one bit that says how to read them keeps every `unsigned long long`
    /// exact, and both backends already build a decimal wide enough to hold it (the coefficient is
    /// a 128-bit integer in C and `System.Decimal` reaches ~7.9e28).
    /// </para>
    /// <para>
    /// ⚠ Both `bits` and `is_unsigned` are filled from the SAME spliced text, and that is safe for
    /// exactly one reason: `_Generic` does not evaluate its controlling expression, so the axiom
    /// runs once no matter how many times its text is written into the wrapper. The guard has
    /// always relied on this; the flag is the second user of it.
    /// </para>
    /// </remarks>
    public const string WholeResultType =
"""
/* What a whole-number axiom hands back. `bits` is the value converted to unsigned 64-bit, which is
   well-defined for every C integer type including negative ones; `is_unsigned` says which way to
   read it back. Both backends reconstruct the same decimal from the pair. */
typedef struct { unsigned long long bits; int is_unsigned; } CufetForeignWhole;
""";

    /// <summary>The name of the struct above, as C spells it.</summary>
    public const string WholeResultCType = "CufetForeignWhole";

    /// <summary>Names the macro that admits a C floating-point expression.</summary>
    public const string RealGuardName = "CUFET_C_REAL";

    /// <summary>The refusal a C compiler prints when a `voidable number` axiom is not floating.</summary>
    /// <remarks>
    /// ⚠ ASCII ONLY, like its two neighbours, and that is not a style preference. A guard message
    /// is a C string literal inside a `_Static_assert`, and gcc echoes it back byte by byte with
    /// anything non-ASCII escaped: an em-dash here printed as `\37777777742\37777777600\37777777624`
    /// in the middle of the sentence a reader is meant to act on.
    /// </remarks>
    public const string RealGuardMessage =
        "this axiom is returned as a voidable number, so it has to produce a C floating-point value "
      + "(a float, a double or a long double); a whole number is returned as a plain number";

    /// <summary>What a floating-point axiom hands back: a decimal, taken apart.</summary>
    /// <remarks>
    /// <para>
    /// ★★ **The conversion crosses as a DECIMAL's own parts, not as a `double`.** DESIGN requires
    /// this one conversion to be written once in C and called by both backends, because two
    /// separately-written base-2-to-base-10 conversions differ in the last place — the same reason
    /// the case table is shared. Handing back the `double` and converting on each side would be
    /// exactly the two implementations it forbids: .NET's own `(decimal)someDouble` rounds to 15
    /// significant digits, which is neither what C would do nor enough to round-trip.
    /// </para>
    /// <para>
    /// ★ So the shared C does the whole conversion and returns coefficient, scale and sign — the
    /// three numbers a decimal IS. The compiled backend feeds them to `cufet_dec_lit`, whose
    /// signature is already exactly this shape, and the interpreter to `decimal`'s own
    /// parts constructor. Neither converts anything; both assemble the same three numbers.
    /// </para>
    /// <para>
    /// ⚠ `ok` is 0 for NaN, ±infinity, and any value outside a decimal's range — all of which
    /// become void. That is not a new rule: `math`'s partial functions already answer this way
    /// (`square-root of (-4)` and `log of (0)` are both void today), and the test there is
    /// `!IsFinite` rather than `IsNaN` for the same reason.
    /// </para>
    /// </remarks>
    public const string RealResultType =
"""
/* A double, already converted to the three numbers a Cufet decimal is made of:
   value = (sign ? -1 : 1) * ((hi << 64) | lo) * 10^-scale, and `ok` is 0 when there is no such
   decimal (NaN, an infinity, or a magnitude no decimal can hold). */
typedef struct { unsigned long long hi, lo; int scale; int sign; int ok; } CufetForeignReal;
""";

    /// <summary>The name of the struct above, as C spells it.</summary>
    public const string RealResultCType = "CufetForeignReal";

    /// <summary>The one base-2-to-base-10 conversion, written once and compiled by both backends.</summary>
    /// <remarks>
    /// ★ Digits come from `snprintf("%.16e")` — 17 significant digits, which is exactly what a
    /// `double` needs to round-trip and what C's own guarantee is stated in. `%e` is used rather
    /// than `%g` because it never switches notation, so the parse below has one shape to read.
    /// </remarks>
    public const string RealConversion =
"""
/* The ONE double-to-decimal conversion. Both backends compile this function and neither writes
   another, so the last digit cannot disagree between them. */
static CufetForeignReal cufet_real_from_double(double cufet_v) {
    CufetForeignReal r; r.hi = 0; r.lo = 0; r.scale = 0; r.sign = 0; r.ok = 0;
    if (!isfinite(cufet_v)) return r;              /* NaN and ±infinity have no decimal — void */
    char b[64];
    snprintf(b, sizeof b, "%.16e", cufet_v);       /* -d.dddddddddddddddde±ddd — 17 significant */
    int i = 0;
    if (b[i] == '-') { r.sign = 1; i++; }
    unsigned __int128 coef = 0;
    int digits = 0;
    for (; b[i] && b[i] != 'e' && b[i] != 'E'; i++) {
        if (b[i] == '.') continue;
        coef = coef * 10 + (unsigned)(b[i] - '0');
        digits++;
    }
    int expo = (b[i] == 'e' || b[i] == 'E') ? atoi(b + i + 1) : 0;
    /* %.16e places the point after the first digit, so the value is coef * 10^(expo-(digits-1)). */
    int scale = (digits - 1) - expo;
    /* Trailing zeros are noise from the fixed 17 digits: 0.5 arrives as 5000000000000000e-16.
       Dropping them keeps the decimal the shortest one with this value, and buys headroom below. */
    while (scale > 0 && coef != 0 && coef % 10 == 0) { coef /= 10; scale--; }
    if (coef == 0) { r.ok = 1; r.sign = 0; return r; }   /* zero is zero, and never negative */
    /* A negative scale means the value is bigger than its digits: fold the exponent in, refusing
       rather than rounding if that will not fit the 96 bits a decimal's coefficient has. */
    while (scale < 0) {
        if (coef > ((((unsigned __int128)1 << 96) - 1) / 10)) return r;
        coef *= 10; scale++;
    }
    if (scale > 28) return r;                      /* below what a decimal can express — void */
    if ((coef >> 96) != 0) return r;               /* wider than a decimal's coefficient — void */
    r.lo = (unsigned long long)coef;
    r.hi = (unsigned long long)(coef >> 64);
    r.scale = scale;
    r.ok = 1;
    return r;
}
""";

    /// <summary>The axiom's value, as the bits-plus-signedness pair both backends then convert.</summary>
    public static string WholeExpression(string source) =>
        $"({WholeResultCType}){{ (unsigned long long)({source}), {UnsignedGuardName}({source}) }}";

    // ── What an axiom can give back ─────────────────────────────────────────

    /// <summary>Is this a result the boundary can bring back from foreign source?</summary>
    /// <remarks>
    /// ⚠ `voidable text` and not `text`. A `char*` from C is NULL whenever C had nothing to give —
    /// `getenv` on an unset name, `strerror` on nonsense — and NULL is C's universal failure
    /// signal, so it lands in the mechanism the language already has rather than in a new one. A
    /// plain `text` result is refused, because it would be a promise the C side cannot keep.
    /// </remarks>
    public static bool CanCrossBack(CufetType result) =>
        result is NumberType or FactType
     || result is VoidableType { Inner: TextType or NumberType };

    /// <summary>The C type a wrapped axiom hands back — the RAW one, before either backend converts.</summary>
    /// <remarks>
    /// ★★ Both backends compile the SAME wrapper function, byte for byte, and differ only in what
    /// they do with what it returns: the compiled side makes a `CufetDec` or an arena copy, and the
    /// shim widens it into a slot. Sharing the function rather than the idea is what stops the two
    /// drifting — the foreign text is spliced once, guarded once, and called once.
    /// </remarks>
    public static string ResultCType(CufetType result) => result switch
    {
        NumberType                          => WholeResultCType,
        FactType                            => "int",
        VoidableType { Inner: TextType }    => "const char*",
        VoidableType { Inner: NumberType }  => RealResultCType,
        _ => throw new TypeException(
                 $"That doesn't work: foreign source cannot give back a {TypeChecker.FormatType(result)}."),
    };

    /// <summary>The guard a wrapped axiom's body opens with, or nothing when C's own rules suffice.</summary>
    /// <remarks>
    /// ★ `fact` needs no guard: `(x) ? 1 : 0` is only valid C for something that has a truth value,
    /// so the C compiler already refuses a struct there and says so in its own words. A guard would
    /// be a second opinion on a question already answered.
    /// </remarks>
    public static string ResultGuard(CufetType result, string spliced) => result switch
    {
        NumberType => GuardStatement(spliced),
        VoidableType { Inner: TextType } =>
            $"    _Static_assert({TextGuardName}({spliced}), \"{TextGuardMessage}\");",
        VoidableType { Inner: NumberType } =>
            $"    _Static_assert({RealGuardName}({spliced}), \"{RealGuardMessage}\");",
        _ => "",
    };

    /// <summary>The expression a wrapped axiom returns, in the raw C type above.</summary>
    public static string ResultExpression(CufetType result, string spliced) => result switch
    {
        NumberType => WholeExpression(spliced),
        FactType   => $"({spliced}) ? 1 : 0",
        VoidableType { Inner: NumberType } => $"cufet_real_from_double((double)({spliced}))",
        _          => $"({spliced})",
    };

    public const string TextGuardName = "CUFET_C_TEXT";

    public const string TextGuardMessage =
        "this axiom is returned as a text, so it has to produce a C string (a char* or const char*)";

    /// <summary>The whole wrapped axiom, as one C function that both backends compile identically.</summary>
    public static string Wrapper(string cName, string qualifier, string language, string source,
                                 IReadOnlyList<(CufetType Type, string Name)> parameters,
                                 CufetType result)
    {
        // Spliced ONCE and used for both the guard and the body, so the two cannot disagree about
        // what the foreign text says.
        string spliced = Splice(source, parameters);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Banner(language, source));
        sb.AppendLine($"{qualifier}{ResultCType(result)} {cName}({ParameterList(parameters)}) {{");
        string guard = ResultGuard(result, spliced);
        if (guard.Length > 0) sb.AppendLine(guard);
        sb.AppendLine($"    return {ResultExpression(result, spliced)};");
        sb.AppendLine("}");
        return sb.ToString();
    }

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

    /// <summary>The fixed entry point a floating-point shim exports instead.</summary>
    public const string RealEntryPoint = "cufet_shim_real";

    /// <summary>Does this axiom come back through the floating-point conversion?</summary>
    public static bool IsRealResult(CufetType result) => result is VoidableType { Inner: NumberType };

    /// <summary>Does this compiler complaint point at foreign source rather than generated code?</summary>
    public static bool BlamesForeignSource(string compilerOutput) =>
        compilerOutput.Contains(FunctionPrefix, StringComparison.Ordinal)
     || compilerOutput.Contains(WholeEntryPoint, StringComparison.Ordinal)
     || compilerOutput.Contains(RealEntryPoint, StringComparison.Ordinal);

    /// <summary>Everything that makes one axiom distinct from another.</summary>
    /// <remarks>
    /// ⚠ The parameter TYPES are part of it, not just the text. Two axioms can share a body and
    /// differ only in what they are handed — `[write(the fd, the data, 1)]` over a number and over
    /// a text are different C functions — and keying on the source alone would wrap the first one
    /// and silently call it for the second.
    /// </remarks>
    public static string Identity(string language, string source,
                                  IReadOnlyList<(CufetType Type, string Name)> parameters,
                                  CufetType? result = null)
        => $"{language}\0{(result is null ? "" : ResultCType(result))}\0"
         + $"{ParameterList(parameters)}\0{Splice(source, parameters)}";

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
