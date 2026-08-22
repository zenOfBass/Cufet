namespace Cufet.Compiler;

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

    /// <summary>A stable C identifier for one axiom, so the same source is wrapped once.</summary>
    public static string FunctionName(string language, string source)
    {
        var material = System.Text.Encoding.UTF8.GetBytes($"{language}\0{source}");
        var hash = System.Security.Cryptography.SHA256.HashData(material);
        return FunctionPrefix + Convert.ToHexString(hash)[..12].ToLowerInvariant();
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
