using System.Text;
using Cufet.Interpreter;
using Cufet.Lexer;

namespace Cufet.Compiler;

public sealed class CodeGenerator
{
    private int _forCounter;
    private int _freshId;
    // Side-channel for pre-emit statements (e.g. series literal construction).
    // Callers must call FlushPreEmits before emitting the final statement line.
    private readonly List<string> _preEmits = new();
    // Full static type of each in-scope variable, so declarations, print dispatch,
    // and struct synthesis all key off the real Cufet type (not just a coarse tag).
    private readonly Dictionary<string, CufetType> _varTypes = new();
    // Return type of each top-level function (null = void), so CastExpression is typed.
    private readonly Dictionary<string, CufetType?> _funcReturnTypes = new();

    // Record struct registry: canonical shape signature → C struct name (cr_N).
    // Records are structural (anonymous), so each distinct shape gets one synthesized
    // C struct; the canonical signature dedups shapes that are structurally equal.
    private readonly Dictionary<string, string> _recordSig2Name = new();
    private readonly List<(string Name, RecordType Type)> _recordStructs = new();
    private int _recordCounter;

    // Voidable struct registry — one tagged struct `cvd_N { int has; T val; }` per distinct
    // inner type. Synthesized exactly like record structs (per-type, with _write/_eq helpers).
    private readonly Dictionary<string, string> _voidableSig2Name = new();
    private readonly List<(string Name, CufetType Inner)> _voidableStructs = new();
    private int _voidableCounter;

    // Map struct registry — one arena container `cmap_N { K* keys; V* vals; int len, cap; }`
    // per distinct (K,V), an association list with linear scan. Lookup returns voidable V.
    private readonly Dictionary<string, string> _mapSig2Name = new();
    private readonly List<(string Name, CufetType Key, CufetType Value)> _mapStructs = new();
    private int _mapCounter;

    // Series struct registry — one arena container `cser_N { T* data; int len, cap; }` per
    // distinct element type T. Generalizes the former number-only CufetSeries; synthesized like
    // maps (per-type, arena pointer / reference type, forward-declared + runtime after structs).
    private readonly Dictionary<string, string> _seriesSig2Name = new();
    private readonly List<(string Name, CufetType Elem)> _seriesStructs = new();
    private int _seriesCounter;

    // Failable struct registry — `cfl_N { int is_failure; T val; const char* message, category }`
    // per distinct inner T. A `T or failure` value: either a T (is_failure=0) or a failure.
    private readonly Dictionary<string, string> _failableSig2Name = new();
    private readonly List<(string Name, CufetType Inner)> _failableStructs = new();
    private int _failableCounter;

    // Inside a Try body: (handler label, the caught-failure C var, open-file depth at Try entry)
    // so a failing fallible call records the failure, closes files opened since the Try, and jumps
    // to the In-case-of-failure handler.
    private (string Label, string FailVar, int FileDepth)? _currentTryHandler;
    // Inside an In-case-of-failure handler: the CufetFailure C var that `the failure` refers to.
    private string? _currentFailVar;

    // Close-on-all-paths cleanup (slice 9B): open file handles (their `fclose(...)` C statements)
    // in open order. Files are closed on EVERY exit from their scope — normal end, return,
    // failure-goto, and loop break/continue — so a write is always flushed (no data loss).
    // (Arenas are deliberately NOT tracked here: the nonlocal-exit `arena_pop` is unsafe because
    // escaping return values and failure messages live in the arena — see project-design-decisions.)
    private readonly List<string> _openFiles = new();
    // The _openFiles depth at each enclosing loop's entry, so break/continue closes files opened
    // inside the loop body before jumping out of it.
    private readonly List<int> _loopFileDepths = new();

    // Set when the program uses `run`/pipe, so the POSIX subprocess runtime is emitted (only then).
    private bool _usesProcess;

    // Arc 1 (stdlib/books): books are BUILTIN + compile-time-resolved (no dynamic linking). A
    // `Pull a book on <name>` registers a compile-time alias (localName → canonical book name) so
    // `<book>'s <member>` dispatch routes to the right emission; the alias is scoped to the Pull body.
    private readonly Dictionary<string, string> _bookAliases = new();
    // Set when an exact-decimal math function (floor/ceiling/round/absolute value) is used, so the
    // math runtime is emitted (only then). pi/e are baked as decimal literals, not runtime funcs.
    private bool _usesMath;
    // Set when the program uses matrices (the collections book's introduced type), so the matrix
    // runtime is emitted (only then). A matrix is an arena reference type like series/maps.
    private bool _usesMatrix;
    // Set when the program uses the chance book's randomness nodes, so the PRNG runtime is emitted.
    // Per the settled fork: ANY C PRNG + invariant-testing — NOT bit-identity with System.Random
    // (unseeded .NET is xoshiro256**, nondeterministic-by-design; the seeded-port is a documented
    // follow-on in the gap audit). Seeded runs are self-consistent WITHIN a backend.
    private bool _usesChance;

    // CONC.E — set when the program uses interrupt constructs (`Yield.`, `an interrupt is requested`,
    // `Acknowledge the interrupt.`) or concurrency (blocked channel-waits are made interruptible). When
    // set, the SIGINT signal substrate is emitted + main installs the handler and a longjmp landing pad.
    private bool _usesSignals;

    // Concurrency (CONC.A+B): emitted only when tasks/channels are used. Generated task thread
    // functions accumulate in _taskFns; the enclosing rabbit's C var suffix for its thread + channel
    // lists (for the structured join + channel-free at Done.) is the top of _rabbitCtx.
    private bool _usesConcurrency;
    private readonly StringBuilder _taskFns = new();
    private int _taskCounter;
    private readonly List<string> _rabbitCtx = new();   // suffix N of cf_thr{N}/cf_chan{N} per open rabbit

    // CONC.C — named tasks + `the awaited result of`. Per named task in scope: the enclosing rabbit
    // suffix (Ctx), the C type of its heap-bridged result, and its inferred result type (which may be
    // a FailureType/VoidableType). The slot index / stored-result C vars are `cf_slot_<sfx>` /
    // `cf_tres_<sfx>` declared at the spawn site (sfx = name with '-'→'_').
    private readonly Dictionary<string, (string Ctx, string ResultCType, CufetType? ResultType)> _taskInfos = new();
    // While emitting a named task's body: the result C type (null ⇒ void/fire-and-forget), so a
    // `return <v>` heap-bridges the value and unwinds the task's arena instead of a plain C return.
    private (CufetType? ResultType, string? ResultCType)? _currentTaskReturn;
    // True while emitting a task body — awaiting a task's result from inside another task is deferred.
    private bool _inTaskBody;

    // The `fclose(...)` statements for files opened at or after `fromDepth`, innermost-first (LIFO),
    // as one inline C string. Used at return / failure-goto / propagate / break / continue. Does
    // NOT mutate _openFiles (nonlocal exits jump past the normal scope-exit that pops them).
    private string FileCleanupStmts(int fromDepth)
    {
        if (_openFiles.Count <= fromDepth) return "";
        var sb = new StringBuilder();
        for (int i = _openFiles.Count - 1; i >= fromDepth; i--) sb.Append(_openFiles[i]).Append(' ');
        return sb.ToString();
    }

    // The declared return type of the function/method/getter currently being emitted, so a
    // `return <T>` in a `voidable T` body widens the value into the voidable struct.
    private CufetType? _currentReturnType;

    // Flow-narrowed variables: inside an `is not void` branch a voidable variable is treated
    // as its inner T (reads emit `.val`), matching the interpreter's variable-level narrowing.
    private readonly Dictionary<string, CufetType> _narrowedVars = new();

    // Object definitions by name (nominal types), collected up front. Objects are also
    // C value structs (cd_<name>); methods become C functions taking a receiver pointer.
    private readonly Dictionary<string, ObjectDefinition> _objectDefs = new();
    // Inside a method body: the receiver's object type name (so `one` and its fields resolve).
    private string? _methodReceiverType;
    // Inside a setter body: the field name being set, so `one's <field> becomes X` writes raw
    // (bypasses the setter) — preventing infinite recursion, matching the interpreter's _inSetterFor.
    private string? _inSetterForField;

    // Cached type singletons (record-equality types, so `new NumberType() == Number`).
    private static readonly CufetType TNumber = new NumberType();
    private static readonly CufetType TFact   = new FactType();
    private static readonly CufetType TText    = new TextType();
    private static readonly CufetType TVoid    = new VoidType();
    private static readonly CufetType TFailMarker = new FailureMarkerType();

    // ── Emitted C runtime ─────────────────────────────────────────────────
    // Self-contained: compiles with plain `gcc file.c`, no external libraries.
    // The software decimal (CufetDec) is bit-identical to .NET System.Decimal:
    //   value = (sign ? -1 : 1) * coef * 10^-scale,  coef <= 2^96-1,  scale in [0,28]
    // Precision-overflow (multiply, division) rounds half-to-even, exactly as
    // measured against the interpreter's decimal. u256 (four 64-bit limbs) carries
    // the up-to-192-bit intermediate products and scaled division numerators.
    private const string RuntimePreamble =
"""
#define _GNU_SOURCE   /* expose POSIX (fileno, fork, execvp, poll…) regardless of -std */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <errno.h>
#include <sys/stat.h>

/* ───────── 256-bit unsigned helper (little-endian limbs) ───────── */
typedef struct { unsigned long long v[4]; } cufet_u256;

static void cufet_decimal_overflow(void) { fprintf(stderr, "decimal overflow\n"); exit(1); }

static cufet_u256 u256_zero(void) { cufet_u256 r = {{0,0,0,0}}; return r; }
static cufet_u256 u256_from_u128(unsigned __int128 x) {
    cufet_u256 r; r.v[0] = (unsigned long long)x; r.v[1] = (unsigned long long)(x >> 64); r.v[2] = 0; r.v[3] = 0; return r;
}
static int u256_is_zero(cufet_u256 a) { return (a.v[0] | a.v[1] | a.v[2] | a.v[3]) == 0ULL; }
static int u256_cmp(cufet_u256 a, cufet_u256 b) {
    for (int i = 3; i >= 0; i--) if (a.v[i] != b.v[i]) return a.v[i] < b.v[i] ? -1 : 1;
    return 0;
}
static cufet_u256 u256_add(cufet_u256 a, cufet_u256 b) {
    cufet_u256 r; unsigned __int128 c = 0;
    for (int i = 0; i < 4; i++) { unsigned __int128 s = (unsigned __int128)a.v[i] + b.v[i] + c; r.v[i] = (unsigned long long)s; c = s >> 64; }
    return r;
}
static cufet_u256 u256_sub(cufet_u256 a, cufet_u256 b) { /* assumes a >= b */
    cufet_u256 r; unsigned __int128 br = 0;
    for (int i = 0; i < 4; i++) { unsigned __int128 d = (unsigned __int128)a.v[i] - b.v[i] - br; r.v[i] = (unsigned long long)d; br = (d >> 64) & 1ULL; }
    return r;
}
static cufet_u256 u256_mul(cufet_u256 a, cufet_u256 b) {
    unsigned long long acc[8] = {0,0,0,0,0,0,0,0};
    for (int i = 0; i < 4; i++) {
        unsigned __int128 carry = 0;
        for (int j = 0; j < 4; j++) {
            unsigned __int128 cur = (unsigned __int128)acc[i+j] + (unsigned __int128)a.v[i] * b.v[j] + carry;
            acc[i+j] = (unsigned long long)cur; carry = cur >> 64;
        }
        acc[i+4] += (unsigned long long)carry;
    }
    if (acc[4] | acc[5] | acc[6] | acc[7]) cufet_decimal_overflow();
    cufet_u256 r; r.v[0]=acc[0]; r.v[1]=acc[1]; r.v[2]=acc[2]; r.v[3]=acc[3]; return r;
}
static cufet_u256 u256_mul_small(cufet_u256 a, unsigned long long m) {
    cufet_u256 r; unsigned __int128 c = 0;
    for (int i = 0; i < 4; i++) { unsigned __int128 t = (unsigned __int128)a.v[i] * m + c; r.v[i] = (unsigned long long)t; c = t >> 64; }
    if (c) cufet_decimal_overflow();
    return r;
}
static void u256_divmod(cufet_u256 num, cufet_u256 den, cufet_u256* quo, cufet_u256* rem) {
    cufet_u256 q = {{0,0,0,0}}, r = {{0,0,0,0}};
    for (int i = 255; i >= 0; i--) {
        unsigned long long carry = 0;                                   /* r <<= 1 */
        for (int k = 0; k < 4; k++) { unsigned long long nc = r.v[k] >> 63; r.v[k] = (r.v[k] << 1) | carry; carry = nc; }
        r.v[0] |= (num.v[i >> 6] >> (i & 63)) & 1ULL;                   /* bring down bit i */
        if (u256_cmp(r, den) >= 0) { r = u256_sub(r, den); q.v[i >> 6] |= (1ULL << (i & 63)); }
    }
    *quo = q; *rem = r;
}
static cufet_u256 u256_pow10(int e) { cufet_u256 r = u256_from_u128(1); for (int i = 0; i < e; i++) r = u256_mul_small(r, 10ULL); return r; }
static cufet_u256 u256_mul_u128(unsigned __int128 a, unsigned __int128 b) { return u256_mul(u256_from_u128(a), u256_from_u128(b)); }

/* ───────── Software decimal: bit-identical to .NET System.Decimal ───────── */
typedef struct { unsigned __int128 coef; int scale; int sign; } CufetDec;

/* 2^96 - 1 = decimal.MaxValue coefficient */
static const cufet_u256 CUFET_DEC_MAX = {{0xFFFFFFFFFFFFFFFFULL, 0x00000000FFFFFFFFULL, 0ULL, 0ULL}};

/* Reduce (coef, scale, sign) to canonical form, dropping low digits with round-half-even.
   'inexact' is the division sticky bit: a nonzero true remainder below coef's least digit. */
static CufetDec cufet_dec_reduce(cufet_u256 coef, int scale, int sign, int inexact) {
    for (;;) {
        int d = scale > 28 ? scale - 28 : 0;
        cufet_u256 p = u256_pow10(d), q, r;
        u256_divmod(coef, p, &q, &r);
        while (u256_cmp(q, CUFET_DEC_MAX) > 0) {                        /* drop more until coef fits 96 bits */
            d++; if (scale - d < 0) cufet_decimal_overflow();
            p = u256_mul_small(p, 10ULL); u256_divmod(coef, p, &q, &r);
        }
        int bumped = 0;
        if (d > 0) {                                                    /* round the dropped tail half-to-even */
            cufet_u256 two = u256_from_u128(2), half, dummy;
            u256_divmod(p, two, &half, &dummy);                        /* half = 10^d / 2, exact for d>=1 */
            int c = u256_cmp(r, half);
            if (c > 0 || (c == 0 && (inexact || (q.v[0] & 1ULL)))) { q = u256_add(q, u256_from_u128(1)); bumped = 1; }
        }
        scale -= d;
        if (bumped && u256_cmp(q, CUFET_DEC_MAX) > 0) { coef = q; inexact = 0; continue; }  /* e.g. 999..9 -> 1000..0 */
        CufetDec out;
        out.coef = ((unsigned __int128)q.v[1] << 64) | q.v[0];
        out.scale = scale;
        out.sign = u256_is_zero(q) ? 0 : sign;                         /* zero is unsigned */
        return out;
    }
}

static CufetDec cufet_dec_lit(unsigned long long hi, unsigned long long lo, int scale, int sign) {
    CufetDec d; d.coef = ((unsigned __int128)hi << 64) | lo; d.scale = scale; d.sign = (d.coef == 0) ? 0 : sign; return d;
}
static CufetDec cufet_dec_from_ll(long long v) {
    CufetDec d; d.scale = 0;
    if (v < 0) { d.sign = 1; d.coef = (unsigned __int128)(-(unsigned long long)v); }
    else       { d.sign = 0; d.coef = (unsigned __int128)(unsigned long long)v; }
    if (d.coef == 0) d.sign = 0;
    return d;
}
static int cufet_to_int(CufetDec d) {                                   /* truncate toward zero */
    unsigned __int128 c = d.coef; for (int s = d.scale; s > 0; s--) c /= 10;
    int v = (int)c; return d.sign ? -v : v;
}

static CufetDec cufet_add_signed(CufetDec a, CufetDec b) {
    int s = a.scale > b.scale ? a.scale : b.scale;
    cufet_u256 ca = u256_mul(u256_from_u128(a.coef), u256_pow10(s - a.scale));
    cufet_u256 cb = u256_mul(u256_from_u128(b.coef), u256_pow10(s - b.scale));
    cufet_u256 rc; int rsign;
    if (a.sign == b.sign) { rc = u256_add(ca, cb); rsign = a.sign; }
    else {
        int c = u256_cmp(ca, cb);
        if (c == 0)      { rc = u256_zero(); rsign = 0; }
        else if (c > 0)  { rc = u256_sub(ca, cb); rsign = a.sign; }
        else             { rc = u256_sub(cb, ca); rsign = b.sign; }
    }
    return cufet_dec_reduce(rc, s, rsign, 0);
}
static CufetDec cufet_add(CufetDec a, CufetDec b) { return cufet_add_signed(a, b); }
static CufetDec cufet_sub(CufetDec a, CufetDec b) { b.sign = (b.coef == 0) ? 0 : !b.sign; return cufet_add_signed(a, b); }
static CufetDec cufet_mul(CufetDec a, CufetDec b) {
    cufet_u256 rc = u256_mul_u128(a.coef, b.coef);
    int rsign = (a.coef == 0 || b.coef == 0) ? 0 : (a.sign ^ b.sign);
    return cufet_dec_reduce(rc, a.scale + b.scale, rsign, 0);
}
static CufetDec cufet_neg(CufetDec a) { a.sign = (a.coef == 0) ? 0 : !a.sign; return a; }
static int cufet_cmp(CufetDec a, CufetDec b) {
    if (a.coef == 0 && b.coef == 0) return 0;
    if (a.coef == 0) return b.sign ? 1 : -1;
    if (b.coef == 0) return a.sign ? -1 : 1;
    if (a.sign != b.sign) return a.sign ? -1 : 1;
    int s = a.scale > b.scale ? a.scale : b.scale;
    cufet_u256 ca = u256_mul(u256_from_u128(a.coef), u256_pow10(s - a.scale));
    cufet_u256 cb = u256_mul(u256_from_u128(b.coef), u256_pow10(s - b.scale));
    int c = u256_cmp(ca, cb);
    return a.sign ? -c : c;
}
static CufetDec cufet_div(CufetDec a, CufetDec b) {
    if (b.coef == 0) { fprintf(stderr, "Division by zero\n"); exit(1); }
    int e = (b.scale - a.scale) + 28;                                   /* compute value * 10^28, then reduce */
    cufet_u256 num = u256_from_u128(a.coef), den = u256_from_u128(b.coef);
    if (e >= 0) num = u256_mul(num, u256_pow10(e)); else den = u256_mul(den, u256_pow10(-e));
    cufet_u256 Q, R; u256_divmod(num, den, &Q, &R);
    int rsign = (a.coef == 0) ? 0 : (a.sign ^ b.sign);
    if (u256_cmp(Q, CUFET_DEC_MAX) <= 0) {
        /* Result fits at scale 28: round the sub-unit remainder half-to-even HERE,
           because cufet_dec_reduce only rounds when it must drop digits (d>0), and
           here there are none to drop. 2R vs den decides; tie -> even coefficient.
           (When Q does NOT fit, reduce drops digits and folds R in as a sticky bit.) */
        cufet_u256 twoR = u256_add(R, R);
        int c = u256_cmp(twoR, den);
        if (c > 0 || (c == 0 && (Q.v[0] & 1ULL))) Q = u256_add(Q, u256_from_u128(1));
        return cufet_dec_reduce(Q, 28, rsign, 0);
    }
    return cufet_dec_reduce(Q, 28, rsign, !u256_is_zero(R));
}
static CufetDec cufet_mod(CufetDec a, CufetDec b) {                     /* remainder, sign of dividend */
    if (b.coef == 0) { fprintf(stderr, "Modulo by zero\n"); exit(1); }
    int e = b.scale - a.scale;
    cufet_u256 num, den;
    if (e >= 0) { num = u256_mul(u256_from_u128(a.coef), u256_pow10(e)); den = u256_from_u128(b.coef); }
    else        { num = u256_from_u128(a.coef); den = u256_mul(u256_from_u128(b.coef), u256_pow10(-e)); }
    cufet_u256 Q, R; u256_divmod(num, den, &Q, &R);                    /* Q = floor(|a|/|b|) */
    CufetDec q; q.coef = ((unsigned __int128)Q.v[1] << 64) | Q.v[0]; q.scale = 0;
    q.sign = (a.sign ^ b.sign); if (q.coef == 0) q.sign = 0;
    return cufet_sub(a, cufet_mul(q, b));                               /* a - trunc(a/b)*b */
}

/* Format matches the interpreter: strip trailing zeros, then plain decimal digits. */
static void cufet_format_number(char* buf, size_t bufsz, CufetDec d) {
    unsigned __int128 c = d.coef; int scale = d.scale;
    while (scale > 0 && c % 10 == 0) { c /= 10; scale--; }
    if (c == 0) { snprintf(buf, bufsz, "0"); return; }
    char ds[40]; int n = 0; unsigned __int128 t = c;
    while (t > 0) { ds[n++] = (char)('0' + (int)(t % 10)); t /= 10; }   /* least-significant first */
    char out[64]; int p = 0;
    if (d.sign) out[p++] = '-';
    if (scale == 0) {
        for (int i = n - 1; i >= 0; i--) out[p++] = ds[i];
    } else if (n > scale) {
        for (int i = n - 1; i >= scale; i--) out[p++] = ds[i];         /* integer part */
        out[p++] = '.';
        for (int i = scale - 1; i >= 0; i--) out[p++] = ds[i];         /* fractional part */
    } else {
        out[p++] = '0'; out[p++] = '.';
        for (int z = 0; z < scale - n; z++) out[p++] = '0';            /* leading fractional zeros */
        for (int i = n - 1; i >= 0; i--) out[p++] = ds[i];
    }
    out[p] = '\0';
    snprintf(buf, bufsz, "%s", out);
}
/* write_ = format inline (no newline), for nested printing inside records/objects/series.
   print_ = write_ + newline, for a top-level State. */
static void cufet_write_number(CufetDec d) { char b[64]; cufet_format_number(b, sizeof(b), d); printf("%s", b); }
static void cufet_write_fact(int b) { printf("%s", b ? "true" : "false"); }
static void cufet_write_text(const char* s) { printf("%s", s); }
static void cufet_print_number(CufetDec d) { cufet_write_number(d); printf("\n"); }
static void cufet_print_fact(int b) { cufet_write_fact(b); printf("\n"); }
static void cufet_print_text(const char* s) { cufet_write_text(s); printf("\n"); }

/* A caught failure (in an In-case-of-failure handler) — T-agnostic, so one handler works
   regardless of which fallible call's T produced the failure. category NULL = absent. */
typedef struct { const char* message; const char* category; } CufetFailure;

""";

    // Text runtime. Text is `const char*` and immutable — every operation allocates a fresh
    // result in the current arena (freed at Done.); literals stay static. Case/trim/parse are
    // ASCII/invariant (matching the interpreter for ASCII input).
    private const string TextRuntime =
"""
static const char* cufet_str_concat(const char* a, const char* b) {
    size_t la = strlen(a), lb = strlen(b);
    char* r = (char*)cufet_arena_alloc(la + lb + 1);
    memcpy(r, a, la); memcpy(r + la, b, lb + 1);
    return r;
}
static const char* cufet_str_substr(const char* s, int from0, int len) {
    if (len < 0) len = 0;
    char* r = (char*)cufet_arena_alloc((size_t)len + 1);
    memcpy(r, s + from0, (size_t)len); r[len] = '\0';
    return r;
}
static const char* cufet_str_range(const char* s, int from1, int to1) {
    if (from1 <= 0) { fprintf(stderr, "a character position must be 1 or greater\n"); exit(1); }
    int len = (int)strlen(s);
    if (to1 < 0 || to1 > len) to1 = len;      /* to1 < 0 sentinel = to end; clamp high */
    int length = to1 - from1 + 1;              /* 1-based inclusive */
    if (length <= 0) return "";
    return cufet_str_substr(s, from1 - 1, length);
}
static const char* cufet_str_edge(const char* s, int count, int from_start) {
    int len = (int)strlen(s);
    int c = count < 0 ? 0 : (count > len ? len : count);
    return from_start ? cufet_str_substr(s, 0, c) : cufet_str_substr(s, len - c, c);
}
static const char* cufet_str_upper(const char* s) {
    size_t n = strlen(s); char* r = (char*)cufet_arena_alloc(n + 1);
    for (size_t i = 0; i < n; i++) r[i] = (char)toupper((unsigned char)s[i]);
    r[n] = '\0'; return r;
}
static const char* cufet_str_lower(const char* s) {
    size_t n = strlen(s); char* r = (char*)cufet_arena_alloc(n + 1);
    for (size_t i = 0; i < n; i++) r[i] = (char)tolower((unsigned char)s[i]);
    r[n] = '\0'; return r;
}
static const char* cufet_str_trim(const char* s) {
    const char* start = s;
    while (*start && isspace((unsigned char)*start)) start++;
    const char* end = s + strlen(s);
    while (end > start && isspace((unsigned char)end[-1])) end--;
    size_t n = (size_t)(end - start);
    char* r = (char*)cufet_arena_alloc(n + 1);
    memcpy(r, start, n); r[n] = '\0'; return r;
}
static int cufet_str_find(const char* text, const char* sub) {
    const char* p = strstr(text, sub);
    return p ? (int)(p - text) + 1 : 0;        /* 1-based; 0 = not found */
}
/* Splits s on each non-overlapping occurrence of delim, keeping empty parts (C# string.Split
   with StringSplitOptions.None): N hits -> N+1 arena-allocated substrings, written to *out.
   Delimiter-not-found -> one part (the whole string); "" -> one empty part. */
static int cufet_str_split(const char* s, const char* delim, const char*** out) {
    size_t dl = strlen(delim);
    if (dl == 0) { fprintf(stderr, "'split by' needs a non-empty delimiter\n"); exit(1); }
    int count = 1;
    for (const char* p = s; (p = strstr(p, delim)) != NULL; p += dl) count++;
    const char** arr = (const char**)cufet_arena_alloc((size_t)count * sizeof(const char*));
    int idx = 0; const char* start = s; const char* p;
    while ((p = strstr(start, delim)) != NULL) {
        size_t len = (size_t)(p - start);
        char* part = (char*)cufet_arena_alloc(len + 1);
        memcpy(part, start, len); part[len] = '\0';
        arr[idx++] = part;
        start = p + dl;
    }
    { size_t len = strlen(start); char* part = (char*)cufet_arena_alloc(len + 1);
      memcpy(part, start, len); part[len] = '\0'; arr[idx++] = part; }
    *out = arr;
    return count;
}
static const char* cufet_str_replace(const char* s, const char* olds, const char* news) {
    size_t lo = strlen(olds);
    if (lo == 0) { fprintf(stderr, "'replace' needs a non-empty target\n"); exit(1); }
    size_t ln = strlen(news), ls = strlen(s), count = 0;
    const char* p = s;
    while ((p = strstr(p, olds))) { count++; p += lo; }
    char* r = (char*)cufet_arena_alloc(ls + count * ln + 1);   /* upper bound */
    char* w = r; p = s; const char* q;
    while ((q = strstr(p, olds))) {
        memcpy(w, p, (size_t)(q - p)); w += (q - p);
        memcpy(w, news, ln); w += ln;
        p = q + lo;
    }
    strcpy(w, p);
    return r;
}
static const char* cufet_text_from_dec(CufetDec d) {
    char buf[64]; cufet_format_number(buf, sizeof(buf), d);
    size_t n = strlen(buf); char* r = (char*)cufet_arena_alloc(n + 1);
    memcpy(r, buf, n + 1); return r;
}
/* text -> number: trim, then accept -?\d+(\.\d+)? (mirrors the lexer + decimal.TryParse).
   Returns 1 and writes *out on success; 0 (unparseable) otherwise. */
static int cufet_parse_number(const char* s, CufetDec* out) {
    while (*s && isspace((unsigned char)*s)) s++;
    const char* end = s + strlen(s);
    while (end > s && isspace((unsigned char)end[-1])) end--;
    if (end == s) return 0;
    const char* p = s; int sign = 0;
    if (*p == '-') { sign = 1; p++; }
    if (p == end || *p < '0' || *p > '9') return 0;
    unsigned __int128 coef = 0; int scale = 0;
    while (p < end && *p >= '0' && *p <= '9') { coef = coef * 10 + (unsigned)(*p - '0'); p++; }
    if (p < end && *p == '.') {
        p++;
        if (p == end || *p < '0' || *p > '9') return 0;
        while (p < end && *p >= '0' && *p <= '9') { coef = coef * 10 + (unsigned)(*p - '0'); scale++; p++; }
    }
    if (p != end) return 0;
    if (scale > 28) return 0;
    unsigned __int128 max96 = (((unsigned __int128)0xFFFFFFFFu) << 64) | 0xFFFFFFFFFFFFFFFFull;
    if (coef > max96) return 0;                /* > decimal.MaxValue -> unparseable */
    out->coef = coef; out->scale = scale; out->sign = (coef == 0) ? 0 : sign;
    return 1;
}

""";

    // File I/O runtime (sub-slice A): whole-file read/write + path checks. Results are arena-
    // allocated (text buffers, line arrays) and freed at Done. OS errors (errno) become Cufet
    // failure values with a deterministic, path-templated message matching the interpreter.
    private const string FileRuntime =
"""
/* Arena-format a one-%s-arg message (deterministic; no host-specific strerror text). */
static const char* cufet_arena_msg(const char* fmt, const char* arg) {
    int n = snprintf(NULL, 0, fmt, arg);
    if (n < 0) n = 0;
    char* buf = (char*)cufet_arena_alloc((size_t)n + 1);
    snprintf(buf, (size_t)n + 1, fmt, arg);
    return buf;
}
/* errno -> Cufet failure (category + templated message), matching the interpreter's FileIoFailure:
   ENOENT -> not-found; EACCES/EPERM -> permission-denied; else -> deterministic disk-error. */
static CufetFailure cufet_file_failure(const char* path, int e) {
    CufetFailure f;
    if (e == ENOENT) {
        f.category = "not-found";
        f.message  = cufet_arena_msg("the file '%s' was not found", path);
    } else if (e == EACCES || e == EPERM) {
        f.category = "permission-denied";
        f.message  = cufet_arena_msg("permission denied accessing '%s'", path);
    } else {
        f.category = "disk-error";
        f.message  = cufet_arena_msg("accessing the file '%s' failed", path);
    }
    return f;
}
/* Reads the whole file into an arena buffer (binary — no newline translation, matching .NET
   ReadAllText's byte fidelity). NUL-terminates and reports the true byte length via *len. */
static int cufet_file_slurp(const char* path, char** buf, long* len, CufetFailure* err) {
    FILE* f = fopen(path, "rb");
    if (!f) { *err = cufet_file_failure(path, errno); return 0; }
    if (fseek(f, 0, SEEK_END) != 0) { *err = cufet_file_failure(path, errno); fclose(f); return 0; }
    long sz = ftell(f);
    if (sz < 0) { *err = cufet_file_failure(path, errno); fclose(f); return 0; }
    rewind(f);
    char* b = (char*)cufet_arena_alloc((size_t)sz + 1);
    size_t rd = fread(b, 1, (size_t)sz, f);
    if (ferror(f)) { *err = cufet_file_failure(path, errno); fclose(f); return 0; }
    b[rd] = '\0';
    fclose(f);
    *buf = b; *len = (long)rd;
    return 1;
}
static int cufet_file_read_all(const char* path, const char** out, CufetFailure* err) {
    char* b; long len;
    if (!cufet_file_slurp(path, &b, &len, err)) return 0;
    *out = b;
    return 1;
}
/* Splits into lines exactly like StreamReader.ReadLine / File.ReadAllLines: a line ends at
   \r, \n, or \r\n; the terminator is dropped; a trailing terminator does NOT yield an empty
   final line; empty input -> zero lines. (Deliberately NOT split-by-"\n", which keeps a trailing
   empty.) Emits arena-allocated substrings into an arena array; *count gets the line count. */
static int cufet_file_read_lines(const char* path, const char*** out, int* count, CufetFailure* err) {
    char* b; long len;
    if (!cufet_file_slurp(path, &b, &len, err)) return 0;
    int n = 0;
    for (long i = 0; i < len; ) {
        while (i < len && b[i] != '\n' && b[i] != '\r') i++;
        n++;
        if (i < len) { if (b[i] == '\r' && i + 1 < len && b[i+1] == '\n') i += 2; else i += 1; }
    }
    const char** arr = (const char**)cufet_arena_alloc((size_t)(n > 0 ? n : 1) * sizeof(const char*));
    int idx = 0;
    for (long i = 0; i < len; ) {
        long start = i;
        while (i < len && b[i] != '\n' && b[i] != '\r') i++;
        size_t ll = (size_t)(i - start);
        char* line = (char*)cufet_arena_alloc(ll + 1);
        memcpy(line, b + start, ll); line[ll] = '\0';
        arr[idx++] = line;
        if (i < len) { if (b[i] == '\r' && i + 1 < len && b[i+1] == '\n') i += 2; else i += 1; }
    }
    *out = arr; *count = idx;
    return 1;
}
static int cufet_file_write(const char* path, const char* text, int append, CufetFailure* err) {
    FILE* f = fopen(path, append ? "ab" : "wb");
    if (!f) { *err = cufet_file_failure(path, errno); return 0; }
    size_t len = strlen(text);
    size_t wr = fwrite(text, 1, len, f);
    if (wr != len || fclose(f) != 0) { *err = cufet_file_failure(path, errno); return 0; }
    return 1;
}
/* Path predicates via stat, matching File.Exists / Directory.Exists (exists = either kind). */
static int cufet_path_exists(const char* path)  { struct stat st; return stat(path, &st) == 0; }
static int cufet_path_is_dir(const char* path)  { struct stat st; return stat(path, &st) == 0 && S_ISDIR(st.st_mode); }
static int cufet_path_is_file(const char* path) { struct stat st; return stat(path, &st) == 0 && S_ISREG(st.st_mode); }

/* ── Streams (slice 9B): a stream is a FILE* (an opened file, or stdin). Read results are
   arena-allocated; the FILE* itself is closed by the With-block cleanup (not the arena). ── */
/* Reads one line, matching StreamReader.ReadLine: content up to \r, \n, or \r\n (terminator
   dropped and \r\n consumed together); NULL at end-of-stream with no content. */
static const char* cufet_stream_read_line(FILE* f) {
    int c = fgetc(f);
    if (c == EOF) return NULL;
    size_t cap = 16, len = 0;
    char* buf = (char*)malloc(cap);
    while (c != EOF && c != '\n' && c != '\r') {
        if (len + 1 >= cap) { cap *= 2; buf = (char*)realloc(buf, cap); }
        buf[len++] = (char)c;
        c = fgetc(f);
    }
    if (c == '\r') { int n = fgetc(f); if (n != '\n' && n != EOF) ungetc(n, f); }
    char* r = (char*)cufet_arena_alloc(len + 1);
    memcpy(r, buf, len); r[len] = '\0';
    free(buf);
    return r;
}
/* Reads the rest of the stream to end (ReadToEnd — "" at end-of-stream, never NULL). */
static const char* cufet_stream_read_all(FILE* f) {
    size_t cap = 256, len = 0;
    char* buf = (char*)malloc(cap);
    int c;
    while ((c = fgetc(f)) != EOF) {
        if (len + 1 >= cap) { cap *= 2; buf = (char*)realloc(buf, cap); }
        buf[len++] = (char)c;
    }
    char* r = (char*)cufet_arena_alloc(len + 1);
    memcpy(r, buf, len); r[len] = '\0';
    free(buf);
    return r;
}

""";

    // Subprocess runtime (slice 9C): POSIX fork/exec/pipe/waitpid — matches the interpreter's
    // no-shell direct exec (ProcessStartInfo.ArgumentList) with separate stdout/stderr + exit code.
    // Emitted ONLY when a program uses `run`/pipe (so non-run programs compile anywhere), and
    // #if-guarded to POSIX (a `run` program is Linux-targeted, like the OS-homework shell; on
    // Windows/mingw — which lacks fork — it simply won't link, which is correct).
    private const string ProcessRuntime =
"""
#if defined(__unix__) || defined(__APPLE__)
#include <unistd.h>
#include <sys/wait.h>
#include <poll.h>
#include <fcntl.h>

/* errno → Cufet launch failure, matching the interpreter's LaunchFailure. */
static CufetFailure cufet_launch_failure(const char* program, int e) {
    CufetFailure f;
    if (e == ENOENT) {
        f.category = "not-found";
        f.message  = cufet_arena_msg("the program '%s' was not found", program);
    } else if (e == EACCES || e == EPERM) {
        f.category = "permission-denied";
        f.message  = cufet_arena_msg("permission denied executing '%s'", program);
    } else {
        f.category = "io-error";
        f.message  = cufet_arena_msg("running the program '%s' failed", program);
    }
    return f;
}

/* Runs `program` with `argv` (NULL-terminated, no shell), optionally feeding `stdin_data`;
   captures stdout + stderr (arena strings) and the exit code. Returns 1 on a successful LAUNCH
   (the process ran — a nonzero exit is still success), 0 on a launch failure (*err set). The
   child is always reaped (waitpid) and all fds closed before returning, so no zombies / leaked
   fds outlive the call — process cleanup is atomic within the primitive, not a later concern. */
static int cufet_run_capture(const char* program, char* const argv[], const char* stdin_data,
                             const char** out_stdout, const char** out_stderr, int* out_exit,
                             CufetFailure* err) {
    int outp[2], errp[2], xp[2];
    if (pipe(outp) < 0 || pipe(errp) < 0 || pipe(xp) < 0) { *err = cufet_launch_failure(program, EIO); return 0; }
    fcntl(xp[1], F_SETFD, FD_CLOEXEC);   /* exec closes it → parent reads EOF = exec ok */
    FILE* infile = NULL; int infd = -1;
    if (stdin_data) { infile = tmpfile(); if (infile) { fputs(stdin_data, infile); fflush(infile); rewind(infile); infd = fileno(infile); } }
    pid_t pid = fork();
    if (pid < 0) {
        if (infile) fclose(infile);
        close(outp[0]); close(outp[1]); close(errp[0]); close(errp[1]); close(xp[0]); close(xp[1]);
        *err = cufet_launch_failure(program, EIO); return 0;
    }
    if (pid == 0) {
        if (infd >= 0) dup2(infd, 0);
        dup2(outp[1], 1); dup2(errp[1], 2);
        close(outp[0]); close(outp[1]); close(errp[0]); close(errp[1]); close(xp[0]);
        execvp(program, argv);
        int e = errno; ssize_t w = write(xp[1], &e, sizeof(e)); (void)w; _exit(127);
    }
    close(outp[1]); close(errp[1]); close(xp[1]);
    if (infile) fclose(infile);
    int child_errno = 0;
    ssize_t xn = read(xp[0], &child_errno, sizeof(child_errno));
    close(xp[0]);
    if (xn > 0) {   /* exec failed in the child → launch failure */
        int st; waitpid(pid, &st, 0);
        close(outp[0]); close(errp[0]);
        *err = cufet_launch_failure(program, child_errno);
        return 0;
    }
    /* Read stdout + stderr concurrently (poll) so neither pipe filling can deadlock the other. */
    char* ob = (char*)malloc(256); size_t oc = 256, ol = 0;
    char* eb = (char*)malloc(256); size_t ec = 256, el = 0;
    struct pollfd pfd[2]; pfd[0].fd = outp[0]; pfd[0].events = POLLIN; pfd[1].fd = errp[0]; pfd[1].events = POLLIN;
    int openfds = 2;
    while (openfds > 0) {
        if (poll(pfd, 2, -1) < 0) { if (errno == EINTR) continue; break; }
        for (int i = 0; i < 2; i++) {
            if (pfd[i].fd < 0) continue;
            if (pfd[i].revents & (POLLIN | POLLHUP | POLLERR)) {
                char tmp[4096]; ssize_t r = read(pfd[i].fd, tmp, sizeof(tmp));
                if (r > 0) {
                    char** b = (i == 0) ? &ob : &eb; size_t* cap = (i == 0) ? &oc : &ec; size_t* len = (i == 0) ? &ol : &el;
                    while (*len + (size_t)r + 1 > *cap) { *cap *= 2; *b = (char*)realloc(*b, *cap); }
                    memcpy(*b + *len, tmp, (size_t)r); *len += (size_t)r;
                } else { close(pfd[i].fd); pfd[i].fd = -1; openfds--; }
            }
        }
    }
    int st; waitpid(pid, &st, 0);
    *out_exit = WIFEXITED(st) ? WEXITSTATUS(st) : (WIFSIGNALED(st) ? 128 + WTERMSIG(st) : -1);
    char* os = (char*)cufet_arena_alloc(ol + 1); memcpy(os, ob, ol); os[ol] = '\0';
    char* es = (char*)cufet_arena_alloc(el + 1); memcpy(es, eb, el); es[el] = '\0';
    free(ob); free(eb);
    *out_stdout = os; *out_stderr = es;
    return 1;
}
#endif

""";

    // Exact-decimal math (Arc 1A — the `math` book's total functions). floor/ceiling/round/abs
    // operate DIRECTLY on CufetDec (no double) — bit-identical to the interpreter's decimal overloads
    // (Math.Floor/Ceiling/Round(AwayFromZero)/Abs on `decimal`). CufetDec = (-1)^sign · coef · 10^-scale.
    // Transcendentals (sqrt/log/power) are double-backed and live in a later slice (1B).
    private const string MathRuntime =
"""
static CufetDec cufet_math_abs(CufetDec x) { x.sign = 0; return x; }
static CufetDec cufet_math_floor(CufetDec x) {
    if (x.scale == 0) return x;                                  /* already an integer */
    unsigned __int128 p = 1; for (int s = 0; s < x.scale; s++) p *= 10;
    unsigned __int128 ip = x.coef / p, rem = x.coef % p;
    if (x.sign && rem != 0) ip += 1;                             /* negative non-integer floors more negative */
    CufetDec r; r.coef = ip; r.scale = 0; r.sign = (ip == 0) ? 0 : x.sign; return r;
}
static CufetDec cufet_math_ceiling(CufetDec x) {
    if (x.scale == 0) return x;
    unsigned __int128 p = 1; for (int s = 0; s < x.scale; s++) p *= 10;
    unsigned __int128 ip = x.coef / p, rem = x.coef % p;
    if (!x.sign && rem != 0) ip += 1;                            /* positive non-integer ceils up */
    CufetDec r; r.coef = ip; r.scale = 0; r.sign = (ip == 0) ? 0 : x.sign; return r;
}
static CufetDec cufet_math_round(CufetDec x) {                    /* half away-from-zero (NOT banker's) */
    if (x.scale == 0) return x;
    unsigned __int128 p = 1; for (int s = 0; s < x.scale; s++) p *= 10;
    unsigned __int128 ip = x.coef / p, rem = x.coef % p;
    if (rem * 2 >= p) ip += 1;                                   /* tie or above → away from zero */
    CufetDec r; r.coef = ip; r.scale = 0; r.sign = (ip == 0) ? 0 : x.sign; return r;
}

/* ── The decimal↔double bridge (1B) — bit-identical replicas of .NET 10's DecCalc conversions.
   The oracle (the interpreter) computes transcendentals as (decimal)Math.Sqrt((double)(decimal)x),
   so BOTH conversions must match .NET exactly — including .NET's 15-significant-digit rounding
   (half-to-even at the 15th digit) on the way back, which is NOT a naive cast. ── */
#include <math.h>
static const double cufet_pow10d[29] = {
    1e0,1e1,1e2,1e3,1e4,1e5,1e6,1e7,1e8,1e9,1e10,1e11,1e12,1e13,1e14,
    1e15,1e16,1e17,1e18,1e19,1e20,1e21,1e22,1e23,1e24,1e25,1e26,1e27,1e28
};

/* .NET VarR8FromDec: dbl = (Low64 + High*2^64) / 10^scale — same ops, same order → bit-identical. */
static double cufet_dec_to_dbl(CufetDec d) {
    double dbl = ((double)(unsigned long long)d.coef
                + (double)(unsigned long long)(d.coef >> 64) * 1.8446744073709552e19)
                / cufet_pow10d[d.scale];
    return d.sign ? -dbl : dbl;
}

/* .NET 10 VarDecFromR8, step-for-step (DBLBIAS 1022; power = 14 - ((exp*19728)>>16) ≈ 14-log10(x);
   double-arithmetic scaling incl. the power==-1 special case; ONE upward bump to reach 15 digits;
   round-half-to-EVEN at the 15th digit; power<0 → whole-number scale-up to 96 bits at scale 0;
   power>=0 → scale=power + trailing-zero strip). exp<-94 underflows to 0.0m (NOT void);
   exp>96 overflows → returns 0 (the caller yields void, matching MathPartial's catch). */
static int cufet_dec_from_dbl(double input, CufetDec* out) {
    unsigned long long bits; memcpy(&bits, &input, 8);
    int exp = (int)((bits >> 52) & 0x7FF) - 1022;
    if (exp < -94) { out->coef = 0; out->scale = 0; out->sign = 0; return 1; }
    if (exp > 96) return 0;
    int sign = 0;
    double dbl = input;
    if (dbl < 0) { dbl = -dbl; sign = 1; }
    int power = 14 - ((exp * 19728) >> 16);
    if (power >= 0) {
        if (power > 28) power = 28;
        dbl *= cufet_pow10d[power];
    } else if (power != -1 || dbl >= 1e15) dbl /= cufet_pow10d[-power];
    else power = 0;
    if (dbl < 1e14 && power < 28) { dbl *= 10; power++; }
    unsigned long long mant = (unsigned long long)(long long)dbl;
    double frac = dbl - (double)(long long)mant;
    if (frac > 0.5 || (frac == 0.5 && (mant & 1))) mant++;       /* half-to-even at digit 15 */
    if (mant == 0) { out->coef = 0; out->scale = 0; out->sign = 0; return 1; }
    unsigned __int128 coef;
    if (power < 0) {
        unsigned __int128 p = 1; for (int i = 0; i < -power; i++) p *= 10;
        coef = (unsigned __int128)mant * p;                       /* whole-number range, scale 0 */
        if (coef >> 96) return 0;                                 /* safety net (unreachable: exp<=96) */
        power = 0;
    } else {
        coef = mant;
        while (power > 0 && coef % 10 == 0) { coef /= 10; power--; }   /* minimal form, like .NET */
    }
    out->coef = coef; out->scale = power; out->sign = (coef == 0) ? 0 : sign;
    return 1;
}

/* math transcendentals — double-backed (the settled fork; the interpreter is Math.Sqrt-backed).
   MathPartial semantics: non-finite → void; decimal-overflow → void; else the 15-sig-digit decimal. */
static int cufet_math_sqrt(CufetDec x, CufetDec* out) {
    double d = sqrt(cufet_dec_to_dbl(x));
    if (!isfinite(d)) return 0;
    return cufet_dec_from_dbl(d, out);
}
static int cufet_math_log(CufetDec x, CufetDec* out) {
    double d = log(cufet_dec_to_dbl(x));
    if (!isfinite(d)) return 0;
    return cufet_dec_from_dbl(d, out);
}
static int cufet_math_power(CufetDec a, CufetDec b, CufetDec* out) {
    double d = pow(cufet_dec_to_dbl(a), cufet_dec_to_dbl(b));
    if (!isfinite(d)) return 0;
    return cufet_dec_from_dbl(d, out);
}

""";

    // Matrix runtime (Arc 1D — the collections book's introduced type). A matrix is an ARENA
    // REFERENCE type like series/maps (shared on assign — matches the interpreter, where MatrixValue
    // is never deep-copied; matrices are immutable after construction, so share-vs-copy is
    // unobservable anyway). All arithmetic is EXACT CufetDec (cufet_add/cufet_mul folds — no double
    // bridge). add/sub/mul return NULL on dimension mismatch: the EMIT SITE wraps that into the
    // fallible `matrix or failure` (the typechecker requires handling — dimension mismatch is a
    // Cufet FAILURE with category "dimension-mismatch", not a crash; messages match the interpreter).
    // Element order + the multiply's k-ascending accumulation from 0 replicate Interpreter.Matrix.cs
    // exactly, so results are bit-identical.
    private const string MatrixRuntime =
"""
typedef struct { int rows; int cols; CufetDec* data; } CufetMatrix;
static CufetMatrix* cufet_mat_new(int rows, int cols) {
    CufetMatrix* m = (CufetMatrix*)cufet_arena_alloc(sizeof(CufetMatrix));
    m->rows = rows; m->cols = cols;
    m->data = (CufetDec*)cufet_arena_alloc(sizeof(CufetDec) * (size_t)rows * (size_t)cols);
    memset(m->data, 0, sizeof(CufetDec) * (size_t)rows * (size_t)cols);   /* all-zero bytes == decimal 0 */
    return m;
}
/* 1-based access, bounds-checked — the messages mirror the interpreter's RuntimeException text. */
static CufetDec cufet_mat_get(CufetMatrix* m, long long r, long long c, int line) {
    if (r < 1 || r > m->rows) { fprintf(stderr, "Row index %lld is out of range — this matrix has %d row(s) (line %d).\n", r, m->rows, line); exit(1); }
    if (c < 1 || c > m->cols) { fprintf(stderr, "Column index %lld is out of range — this matrix has %d column(s) (line %d).\n", c, m->cols, line); exit(1); }
    return m->data[(r - 1) * m->cols + (c - 1)];
}
/* `a matrix of R by C [filled with F]` — runtime validation for non-literal dimensions
   (literals are rejected statically by the typechecker), matching the interpreter's messages. */
static CufetMatrix* cufet_mat_sized(CufetDec rd, CufetDec cd, CufetDec fill, int line) {
    long long r = cufet_to_int(rd), c = cufet_to_int(cd);
    if (cufet_cmp(rd, cufet_dec_from_ll(r)) != 0 || r < 1) { fprintf(stderr, "Matrix row count must be a positive whole number, but got %s (line %d).\n", cufet_text_from_dec(rd), line); exit(1); }
    if (cufet_cmp(cd, cufet_dec_from_ll(c)) != 0 || c < 1) { fprintf(stderr, "Matrix column count must be a positive whole number, but got %s (line %d).\n", cufet_text_from_dec(cd), line); exit(1); }
    CufetMatrix* m = cufet_mat_new((int)r, (int)c);
    if (cufet_cmp(fill, cufet_dec_from_ll(0)) != 0)   /* interpreter skips the fill when it equals 0 */
        for (long long i = 0; i < r * c; i++) m->data[i] = fill;
    return m;
}
static CufetMatrix* cufet_mat_add(CufetMatrix* a, CufetMatrix* b) {
    if (a->rows != b->rows || a->cols != b->cols) return NULL;
    CufetMatrix* m = cufet_mat_new(a->rows, a->cols);
    for (int i = 0; i < a->rows * a->cols; i++) m->data[i] = cufet_add(a->data[i], b->data[i]);
    return m;
}
static CufetMatrix* cufet_mat_sub(CufetMatrix* a, CufetMatrix* b) {
    if (a->rows != b->rows || a->cols != b->cols) return NULL;
    CufetMatrix* m = cufet_mat_new(a->rows, a->cols);
    for (int i = 0; i < a->rows * a->cols; i++) m->data[i] = cufet_sub(a->data[i], b->data[i]);
    return m;
}
static CufetMatrix* cufet_mat_mul(CufetMatrix* a, CufetMatrix* b) {   /* real matrix product, m×n · n×p */
    if (a->cols != b->rows) return NULL;
    CufetMatrix* m = cufet_mat_new(a->rows, b->cols);
    for (int r = 0; r < a->rows; r++)
        for (int c = 0; c < b->cols; c++) {
            CufetDec s = cufet_dec_from_ll(0);
            for (int k = 0; k < a->cols; k++)
                s = cufet_add(s, cufet_mul(a->data[r * a->cols + k], b->data[k * b->cols + c]));
            m->data[r * b->cols + c] = s;
        }
    return m;
}
static CufetMatrix* cufet_mat_transpose(CufetMatrix* a) {
    CufetMatrix* m = cufet_mat_new(a->cols, a->rows);
    for (int r = 0; r < a->rows; r++)
        for (int c = 0; c < a->cols; c++)
            m->data[c * a->rows + r] = a->data[r * a->cols + c];
    return m;
}
/* matrix((1, 2), (3, 4)) — matches the interpreter's FormatMatrix exactly. */
static void cufet_mat_write(CufetMatrix* m) {
    printf("matrix(");
    for (int r = 0; r < m->rows; r++) {
        if (r) printf(", ");
        printf("(");
        for (int c = 0; c < m->cols; c++) { if (c) printf(", "); cufet_write_number(m->data[r * m->cols + c]); }
        printf(")");
    }
    printf(")");
}

""";

    // Chance runtime (Arc 1E — the chance book). A small self-contained xorshift64* PRNG: seedable
    // via `Seed the chance with N` (truncated to integer, mixed, nonzero-forced), lazily time-seeded
    // on first use when unseeded (each run differs, like the interpreter's unseeded Random). The
    // observable GUARANTEE is per-backend: a seeded run is self-consistent (same seed → same
    // sequence within this backend); cross-backend sequences intentionally differ (settled fork —
    // invariants, not bit-identity). Single global state, matching the interpreter's one _rng.
    private const string ChanceRuntime =
"""
#include <time.h>
static unsigned long long cufet_rng_state;
static int cufet_rng_inited = 0;
static void cufet_rng_seed(long long s) {
    unsigned long long z = (unsigned long long)s + 0x9E3779B97F4A7C15ULL;   /* splitmix64 mix */
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    cufet_rng_state = z ^ (z >> 31);
    if (cufet_rng_state == 0) cufet_rng_state = 88172645463325252ULL;
    cufet_rng_inited = 1;
}
static unsigned long long cufet_rng_u64(void) {
    if (!cufet_rng_inited) cufet_rng_seed((long long)time(NULL) ^ ((long long)clock() << 20));
    unsigned long long x = cufet_rng_state;
    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
    cufet_rng_state = x;
    return x * 0x2545F4914F6CDD1DULL;
}
static long long cufet_rng_below(long long bound) {   /* uniform-ish in [0, bound); bound > 0 */
    return (long long)(cufet_rng_u64() % (unsigned long long)bound);
}
/* `a random number from L to H` — inclusive bounds ([lo, hi], matching .Next(lo, hi+1)); the
   decimal low>high check + message mirror the interpreter's RuntimeException. */
static CufetDec cufet_random_number(CufetDec low, CufetDec high, int line) {
    if (cufet_cmp(low, high) > 0) {
        fprintf(stderr, "Random number range is invalid: low (%s) is greater than high (%s) (line %d).\n",
                cufet_text_from_dec(low), cufet_text_from_dec(high), line);
        exit(1);
    }
    long long lo = cufet_to_int(low), hi = cufet_to_int(high);
    return cufet_dec_from_ll(lo + cufet_rng_below(hi - lo + 1));
}

""";

    // SIGINT signal substrate (CONC.E): true-preemptive interrupt. Emitted when the program uses
    // interrupt constructs OR concurrency (blocked channel-waits become interruptible). The handler is
    // MINIMAL + async-signal-safe (it only sets an atomic flag); all real work happens at cooperative
    // checkpoints (`Yield.`, channel-waits) in normal thread flow. An unhandled interrupt unwinds via a
    // per-thread longjmp landing pad — the main thread's pad tears down (pop all arenas, free channels,
    // flush) + exits; a worker's pad runs its local cleanup + returns (reaped by the structured join).
    // POSIX-guarded; on non-unix (mingw) it degrades to no-op stubs (Ctrl-C = default terminate).
    private const string SignalRuntime =
"""
#if defined(__unix__) || defined(__APPLE__)
#include <signal.h>
#include <setjmp.h>
static volatile sig_atomic_t cufet_interrupted = 0;
static _Thread_local sigjmp_buf cufet_thread_top;   /* this thread's interrupt landing pad */
static _Thread_local int cufet_pad_set = 0;          /* 1 once this thread has established its pad */
static void cufet_sigint_handler(int sig) { (void)sig; cufet_interrupted = 1; }   /* async-signal-safe */
static void cufet_install_sigint(void) {
    struct sigaction sa; memset(&sa, 0, sizeof(sa));
    sa.sa_handler = cufet_sigint_handler;
    sigaction(SIGINT, &sa, NULL);
}
/* Cooperative interrupt checkpoint: if an interrupt is pending and this thread has a landing pad,
   unwind to it. No-op if no pad (a raw task thread) — its caller handles the -1 recv sentinel. */
static void cufet_checkpoint(void) {
    if (cufet_interrupted && cufet_pad_set) siglongjmp(cufet_thread_top, 1);
}
#else
static volatile int cufet_interrupted = 0;
static void cufet_install_sigint(void) {}
static void cufet_checkpoint(void) {}
#endif

""";

    // Concurrency runtime (CONC.A+B): pthreads + a thread-safe channel (mutex + condvar). Emitted
    // only when tasks/channels are used; POSIX-guarded (Linux-targeted, WSL-verified). Channels are
    // number-only this slice (value type → the queue node IS the heap-bridged message; send mallocs
    // a node, recv copies the value out + frees it, cufet_chan_free frees un-received nodes).
    private const string ConcurrencyRuntime =
"""
#if defined(__unix__) || defined(__APPLE__)
#include <pthread.h>
#include <time.h>
#define CUFET_TASK_MAX 4096
typedef struct cufet_chan_node { CufetDec val; struct cufet_chan_node* next; } cufet_chan_node;
typedef struct { pthread_mutex_t m; pthread_cond_t c; cufet_chan_node* head; cufet_chan_node* tail; int closed; } cufet_chan;
/* Live-channel registry — so an interrupt unwind (CONC.E) can free channels the longjmp jumped past.
   A normal cufet_chan_free unregisters; the interrupt teardown frees whatever is still registered. */
static cufet_chan* cufet_live_chans[CUFET_TASK_MAX];
static int cufet_nlive = 0;
static pthread_mutex_t cufet_live_m = PTHREAD_MUTEX_INITIALIZER;
static cufet_chan* cufet_chan_new(void) {
    cufet_chan* ch = (cufet_chan*)malloc(sizeof(cufet_chan));
    pthread_mutex_init(&ch->m, NULL); pthread_cond_init(&ch->c, NULL);
    ch->head = ch->tail = NULL; ch->closed = 0;
    pthread_mutex_lock(&cufet_live_m); if (cufet_nlive < CUFET_TASK_MAX) cufet_live_chans[cufet_nlive++] = ch; pthread_mutex_unlock(&cufet_live_m);
    return ch;
}
static void cufet_chan_send(cufet_chan* ch, CufetDec v) {
    cufet_chan_node* n = (cufet_chan_node*)malloc(sizeof(cufet_chan_node)); /* heap bridge */
    n->val = v; n->next = NULL;
    pthread_mutex_lock(&ch->m);
    if (ch->tail) ch->tail->next = n; else ch->head = n; ch->tail = n;
    pthread_cond_signal(&ch->c); pthread_mutex_unlock(&ch->m);
}
/* Blocking receive → 1 with *out set if a value is available, 0 if the channel is empty-and-closed
   (→ Cufet void), -1 if a SIGINT arrived while blocked (CONC.E — the caller runs a checkpoint). The
   wait is a 50ms timed-wait loop so a blocked worker re-checks the interrupt flag (true-preemptive:
   a real pthread_cond_wait can now be woken by a signal, which the cooperative interpreter can't do).
   Frees the received node (the heap bridge) after copying the value out. */
static int cufet_chan_recv(cufet_chan* ch, CufetDec* out) {
    pthread_mutex_lock(&ch->m);
    while (!ch->head && !ch->closed) {
        if (cufet_interrupted) { pthread_mutex_unlock(&ch->m); return -1; }
        struct timespec ts; clock_gettime(CLOCK_REALTIME, &ts);
        ts.tv_nsec += 50000000L; if (ts.tv_nsec >= 1000000000L) { ts.tv_sec++; ts.tv_nsec -= 1000000000L; }
        pthread_cond_timedwait(&ch->c, &ch->m, &ts);
    }
    if (ch->head) {
        cufet_chan_node* n = ch->head; ch->head = n->next; if (!ch->head) ch->tail = NULL;
        pthread_mutex_unlock(&ch->m);
        *out = n->val; free(n); return 1;
    }
    pthread_mutex_unlock(&ch->m); return 0;
}
static void cufet_chan_close(cufet_chan* ch) {
    pthread_mutex_lock(&ch->m); ch->closed = 1; pthread_cond_broadcast(&ch->c); pthread_mutex_unlock(&ch->m);
}
static void cufet_chan_free(cufet_chan* ch) {   /* frees un-received message bridges (teardown/close-with-pending) */
    pthread_mutex_lock(&cufet_live_m);
    for (int i = 0; i < cufet_nlive; i++) if (cufet_live_chans[i] == ch) { cufet_live_chans[i] = cufet_live_chans[--cufet_nlive]; break; }
    pthread_mutex_unlock(&cufet_live_m);
    cufet_chan_node* n = ch->head; while (n) { cufet_chan_node* x = n->next; free(n); n = x; }
    pthread_mutex_destroy(&ch->m); pthread_cond_destroy(&ch->c); free(ch);
}
/* Interrupt-teardown helper: free every still-live channel (the unwind longjmp'd past their frees). */
static void cufet_free_all_chans(void) {
    pthread_mutex_lock(&cufet_live_m);
    while (cufet_nlive > 0) {
        cufet_chan* ch = cufet_live_chans[--cufet_nlive];
        cufet_chan_node* n = ch->head; while (n) { cufet_chan_node* x = n->next; free(n); n = x; }
        pthread_mutex_destroy(&ch->m); pthread_cond_destroy(&ch->c); free(ch);
    }
    pthread_mutex_unlock(&cufet_live_m);
}
/* Task pipes (CONC.D): each stage runs as its own thread connected by channels. `output <v>` and
   `for each … from the input` inside a stage read these THREAD-LOCAL implicit channels — mirroring
   the interpreter's per-stage _pipeOutputChan / _pipeInputChan, but thread-local so concurrent
   stages don't clash. A stage closes its output channel when its function returns → downstream sees
   the stream complete (recv returns void on empty-and-closed). All values cross the SAME heap-bridged
   channel boundary as A+B, so inter-stage streaming is race-free by the same construction. */
static _Thread_local cufet_chan* cufet_pipe_in;
static _Thread_local cufet_chan* cufet_pipe_out;
typedef struct { cufet_chan* in; cufet_chan* out; void (*fn)(void); } cufet_pipe_arg;
static void* cufet_pipe_stage(void* argp) {
    cufet_pipe_arg* a = (cufet_pipe_arg*)argp;
    cufet_pipe_in = a->in; cufet_pipe_out = a->out;
    cufet_arena_push();
    /* Interrupt landing pad (CONC.E): if a blocked recv inside the stage is interrupted, it unwinds
       to here and the stage tears down normally (arena pop + close output + reaped by the pipe join). */
    if (sigsetjmp(cufet_thread_top, 1) == 0) { cufet_pad_set = 1; a->fn(); }
    cufet_arena_pop();
    if (a->out) cufet_chan_close(a->out);      /* signal downstream: no more values */
    free(a);
    return NULL;
}
#endif

""";

    public string Generate(Program program)
    {
        var sb = new StringBuilder();

        // Concurrency is discovered up front (not during the body pass) because a rabbit's header
        // must emit its thread/channel tracking arrays before its body is walked.
        _usesConcurrency = ProgramUsesConcurrency(program.Statements);
        // SIGINT substrate (CONC.E) is likewise discovered up front — main's top installs the handler
        // + landing pad before its body, so it must know whether interrupt handling is in play.
        _usesSignals = ProgramUsesSignals(program.Statements);

        // ── Runtime: includes + software decimal + print helpers ──────────
        sb.AppendLine(RuntimePreamble);

        // ── Arena allocator ───────────────────────────────────────────────
        // Pull a rabbit → cufet_arena_push(); body; Done. → cufet_arena_pop().
        // Arena is a tracked-pointer list: every cufet_arena_alloc() registers
        // the pointer; cufet_arena_pop() frees all of them in one shot.
        // When a series data buffer grows, the old buffer stays in ptrs
        // (wasted but harmless — freed at pop). No use-after-free, no leak.
        sb.AppendLine("#define CUFET_ARENA_MAX_DEPTH 64");
        sb.AppendLine("typedef struct { void** ptrs; int len; int cap; } CufetArena;");
        // Thread-local: each pthread bump-allocates in its OWN arena stack (no cross-thread arena
        // contention — sound because nothing mutable is shared; values cross threads via heap copy).
        sb.AppendLine("static _Thread_local CufetArena cufet_arenas[CUFET_ARENA_MAX_DEPTH];");
        sb.AppendLine("static _Thread_local int cufet_arena_top = -1;");
        sb.AppendLine();
        sb.AppendLine("static void cufet_arena_push(void) {");
        sb.AppendLine("    ++cufet_arena_top;");
        sb.AppendLine("    cufet_arenas[cufet_arena_top].ptrs = NULL;");
        sb.AppendLine("    cufet_arenas[cufet_arena_top].len  = 0;");
        sb.AppendLine("    cufet_arenas[cufet_arena_top].cap  = 0;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void* cufet_arena_alloc(size_t size) {");
        sb.AppendLine("    void* p = malloc(size);");
        sb.AppendLine("    CufetArena* a = &cufet_arenas[cufet_arena_top];");
        sb.AppendLine("    if (a->len == a->cap) {");
        sb.AppendLine("        a->cap  = a->cap == 0 ? 8 : a->cap * 2;");
        sb.AppendLine("        a->ptrs = (void**)realloc(a->ptrs, (size_t)a->cap * sizeof(void*));");
        sb.AppendLine("    }");
        sb.AppendLine("    a->ptrs[a->len++] = p;");
        sb.AppendLine("    return p;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_arena_pop(void) {");
        sb.AppendLine("    CufetArena* a = &cufet_arenas[cufet_arena_top];");
        sb.AppendLine("    for (int i = 0; i < a->len; i++) free(a->ptrs[i]);");
        sb.AppendLine("    free(a->ptrs);");
        sb.AppendLine("    a->ptrs = NULL;");
        sb.AppendLine("    a->len  = 0;");
        sb.AppendLine("    a->cap  = 0;");
        sb.AppendLine("    --cufet_arena_top;");
        sb.AppendLine("}");
        sb.AppendLine();

        // ── Series runtime ────────────────────────────────────────────────
        // Generalized to per-element-type structs (cser_N) synthesized like maps: forward-declared
        // here-adjacent (EmitSeriesForwardDecls, before the value structs) and fully defined after
        // (EmitSeriesRuntime). Nothing series-specific is emitted in the preamble now — a series of
        // number is just cser_<number>, one of many element types.

        // ── Text runtime (immutable strings; results arena-allocated) ─────
        sb.AppendLine(TextRuntime);
        sb.AppendLine(FileRuntime);

        // Object definitions are nominal types — collect them all up front (they may be
        // top-level or nested in Pull blocks) so literals and field access resolve.
        CollectObjectDefs(program.Statements);
        MergeUntoMethods(program.Statements);   // fold 'Bind ... unto <type>' methods into their type
        foreach (var def in _objectDefs.Values)
            ValidateObjectSupported(def);

        // Method + function bodies + main are emitted into a separate buffer FIRST. That
        // pass discovers every record struct shape used (via TypeOf / EmitCType), so struct
        // declarations — which C requires before any use — can be assembled ahead of them.
        var body = new StringBuilder();

        // Free functions and named constructors (a constructor is just a function whose
        // return type is the object type — its ReturnType already carries that). 'unto'
        // methods are excluded here — they were merged into their object's method list.
        var topFuncs = program.Statements
            .OfType<BindStatement>()
            .Where(b => b.UntoType == null)
            .ToList();

        // Binds declared DIRECTLY inside a top-level `Pull a book` body are HOISTED and compiled as
        // ordinary free functions — the book scope is compile-time, so a Pull-body bind is morally
        // top-level (and matrix-typed functions can ONLY live inside a collections pull, since the
        // type isn't in scope outside it). Their book aliases are re-activated while their bodies
        // emit so book members resolve. Captured pull-scope locals are the closures gap (best-effort
        // clean throw via the task-capture walker).
        var pullBinds = new List<(BindStatement Bind, List<(string Local, string Book)> Aliases)>();
        void CollectPullBinds(IReadOnlyList<IStatement> stmts, List<(string Local, string Book)> aliases)
        {
            foreach (var st in stmts)
                if (st is PullStatement ps)
                {
                    var inner = new List<(string Local, string Book)>(aliases);
                    foreach (var (bookName, localName) in ps.Books) inner.Add((localName, bookName.ToLowerInvariant()));
                    foreach (var s2 in ps.Body)
                        if (s2 is BindStatement pb && pb.UntoType == null) pullBinds.Add((pb, inner));
                    CollectPullBinds(ps.Body, inner);   // nested pulls (the walker only matches PullStatement)
                }
        }
        CollectPullBinds(program.Statements, new List<(string, string)>());

        foreach (var bind in topFuncs)
            _funcReturnTypes[bind.Name] = bind.ReturnType;
        foreach (var (bind, _) in pullBinds)
            _funcReturnTypes[bind.Name] = bind.ReturnType;

        // Object method / getter / setter bodies (each a C function taking a receiver pointer).
        foreach (var def in _objectDefs.Values)
        {
            foreach (var method in def.Methods) EmitMethod(body, def, method);
            foreach (var g in def.Getters)      EmitGetter(body, def, g);
            foreach (var s in def.Setters)      EmitSetter(body, def, s);
        }

        foreach (var bind in topFuncs)
            EmitBind(body, bind);

        foreach (var (bind, aliases) in pullBinds)
        {
            // Best-effort capture check: a hoisted bind must not reference pull-scope locals
            // (params + its own defines + functions + book aliases are fine — anything else is
            // a closure capture, the deferred gap).
            var refs = new HashSet<string>(); var defs = new HashSet<string>();
            foreach (var s in bind.Body) CollectRefsDefs(s, refs, defs);
            var known = new HashSet<string>(bind.Parameters.Select(p => p.Name)
                .Concat(defs).Concat(_funcReturnTypes.Keys).Concat(aliases.Select(a => a.Local)));
            var captured = refs.Where(r => !known.Contains(r) && r != "it" && r != "input" && r != "the failure").ToList();
            if (captured.Count > 0)
                throw new CompilerException(
                    $"function '{bind.Name}' (inside a Pull-book block) captures '{captured[0]}' from the pull scope — closures are not yet supported by the compiler.");
            foreach (var (local, book) in aliases) _bookAliases[local] = book;
            EmitBind(body, bind);
            foreach (var (local, _) in aliases) _bookAliases.Remove(local);
        }

        // ── main() ────────────────────────────────────────────────────────
        // A global arena is pushed so series created at top level (outside an
        // explicit Pull) are safely tracked and freed at program exit.
        body.AppendLine("int main(void) {");
        // SIGINT landing pad (CONC.E): install the handler + establish main's interrupt pad. On an
        // unhandled interrupt a checkpoint siglongjmps here; we tear down (pop all arenas — nested
        // included — free any live channels, flush) and exit 130 (128+SIGINT). Guarded so a non-signal
        // program is unchanged, and #if'd so mingw (no sigaction) degrades to default Ctrl-C.
        if (_usesSignals || _usesConcurrency)
        {
            body.AppendLine("#if defined(__unix__) || defined(__APPLE__)");
            body.AppendLine("    cufet_install_sigint();");
            body.AppendLine("    if (sigsetjmp(cufet_thread_top, 1)) {");
            body.AppendLine("        while (cufet_arena_top >= 0) cufet_arena_pop();");
            if (_usesConcurrency)
                body.AppendLine("        cufet_free_all_chans();");
            body.AppendLine("        fflush(stdout); return 130;");
            body.AppendLine("    }");
            body.AppendLine("    cufet_pad_set = 1;");
            body.AppendLine("#endif");
        }
        body.AppendLine("    cufet_arena_push();");
        foreach (var stmt in program.Statements)
        {
            if (stmt is BindStatement) continue;       // emitted above
            if (stmt is ObjectDefinition) continue;    // declarations, handled up front
            EmitStatement(body, stmt, "    ");
        }
        body.AppendLine("    cufet_arena_pop();");
        body.AppendLine("    return 0;");
        body.AppendLine("}");

        // ── Series + map struct forward declarations (so value structs can hold their pointers) ──
        EmitSeriesForwardDecls(sb);
        EmitMapForwardDecls(sb);

        // ── Matrix runtime (before EmitStructs: failable/record/series structs may hold CufetMatrix*) ──
        if (_usesMatrix) sb.AppendLine(MatrixRuntime);

        // ── Struct declarations (records + objects + voidables) + write/eq helpers ──
        EmitStructs(sb);

        // ── Series + map container structs + helpers (need element/K/V structs above) ──
        EmitSeriesRuntime(sb);
        EmitMapRuntime(sb);

        // ── Exact-decimal math runtime (only when a math total function is used) ──
        if (_usesMath) sb.AppendLine(MathRuntime);

        // ── Chance runtime (only when randomness is used) ──
        if (_usesChance) sb.AppendLine(ChanceRuntime);

        // ── Subprocess runtime (only when `run`/pipe is used; discovered during the body pass) ──
        if (_usesProcess) sb.AppendLine(ProcessRuntime);

        // ── SIGINT substrate (CONC.E) — before the concurrency runtime, which uses the interrupt flag
        //    + checkpoint in its channel-wait. Emitted when interrupt constructs OR concurrency is used. ──
        if (_usesSignals || _usesConcurrency)
            sb.AppendLine(SignalRuntime);

        // ── Concurrency runtime + generated task thread functions (only when tasks/channels used) ──
        if (_usesConcurrency)
        {
            sb.AppendLine(ConcurrencyRuntime);
            sb.Append(_taskFns);
        }

        // ── Forward declarations: object methods/getters/setters, then free functions ──
        foreach (var def in _objectDefs.Values)
        {
            foreach (var method in def.Methods) sb.AppendLine($"{MethodSignature(def, method)};");
            foreach (var g in def.Getters)      sb.AppendLine($"{GetterSignature(def, g)};");
            foreach (var s in def.Setters)      sb.AppendLine($"{SetterSignature(def, s)};");
        }
        foreach (var bind in topFuncs)
            sb.AppendLine($"{EmitFunctionSignature(bind)};");
        foreach (var (bind, _) in pullBinds)
            sb.AppendLine($"{EmitFunctionSignature(bind)};");
        sb.AppendLine();

        sb.Append(body);
        return sb.ToString();
    }

    // Nominal C struct name for an object type.
    private static string ObjStructName(string objectName) => "cd_" + objectName.Replace('-', '_');

    // Walks all statements (including nested block bodies) collecting ObjectDefinitions.
    private void CollectObjectDefs(IEnumerable<IStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case ObjectDefinition od: _objectDefs[od.Name] = od; break;
                case PullRabbitStatement p: CollectObjectDefs(p.Body); break;
                case IfStatement iff:
                    foreach (var arm in iff.Arms) CollectObjectDefs(arm.Body);
                    if (iff.ElseBody != null) CollectObjectDefs(iff.ElseBody);
                    break;
                case WhileStatement w:      CollectObjectDefs(w.Body); break;
                case RepeatUntilStatement r: CollectObjectDefs(r.Body); break;
                case ForEachStatement fe:   CollectObjectDefs(fe.Body); break;
            }
        }
    }

    // Objects this slice: plain data + methods with direct dispatch. Everything fancier
    // (embedding, interface conformance/dispatch, getters/setters, named constructors,
    // destructors) is deferred — reject cleanly rather than miscompile.
    private void ValidateObjectSupported(ObjectDefinition def)
    {
        if (def.EmbeddedTypeName != null && !_objectDefs.ContainsKey(def.EmbeddedTypeName))
            throw new CompilerException($"object '{def.Name}': embeds '{def.EmbeddedTypeName}', which isn't a plain object type (interface embedding not supported yet).");
        if (def.ConformedInterfaces.Count > 0)
            throw new CompilerException($"object '{def.Name}': interface conformance is not yet supported by the compiler.");
    }

    // ── Record struct synthesis ───────────────────────────────────────────
    // A canonical structural signature dedups shapes; each distinct shape becomes
    // one C struct `cr_N` (positional fields p0.., named fields cv_<name> sorted by
    // name to match the interpreter's canonical print order). Records are C VALUE
    // structs — assignment copies, which reproduces the interpreter's value semantics
    // exactly (nested records/objects copy deeply; series fields are shared pointers).
    private string TypeSig(CufetType t) => t switch
    {
        NumberType => "N",
        FactType   => "F",
        TextType   => "T",
        SeriesType s => "S(" + TypeSig(s.ElementType) + ")",
        RecordType r => "R(" + string.Join(",", r.PositionalTypes.Select(TypeSig)) + "|" +
                        string.Join(",", r.NamedFields.Select(f => f.Name + ":" + TypeSig(f.Type))) + ")",
        ObjectType o => "O:" + o.Name,   // nominal — identity is the name
        VoidableType v => "V(" + TypeSig(v.Inner) + ")",
        MapType m => "M(" + TypeSig(m.KeyType) + "," + TypeSig(m.ValueType) + ")",
        FailureType f => "F(" + TypeSig(f.Inner) + ")",
        MatrixType => "MX",   // one fixed runtime struct (CufetMatrix*) — identity is the type itself
        _ => throw new CompilerException(
                 $"'{t.GetType().Name}' is not yet supported by the compiler (slice 5B: records + objects + text).")
    };

    // Ensures a struct exists for this record shape (and, recursively, for any nested
    // record shapes in its fields). Returns the C struct name.
    private string RegisterRecordStruct(RecordType rt)
    {
        string sig = TypeSig(rt);
        if (_recordSig2Name.TryGetValue(sig, out var name)) return name;

        name = $"cr_{_recordCounter++}";
        _recordSig2Name[sig] = name;
        _recordStructs.Add((name, rt));
        foreach (var t in rt.PositionalTypes) RegisterNestedRecords(t);
        foreach (var (_, t) in rt.NamedFields)  RegisterNestedRecords(t);
        return name;
    }

    private void RegisterNestedRecords(CufetType t)
    {
        switch (t)
        {
            case RecordType rt:   RegisterRecordStruct(rt); break;
            case SeriesType st:   RegisterSeriesStruct(st); break;
            case VoidableType vt: RegisterVoidableStruct(vt); break;
            case MapType mt:      RegisterMapStruct(mt); break;
            case FailureType ft:  RegisterFailableStruct(ft); break;
        }
    }

    // Ensures a series container struct exists for `series of T` (and T's nested structs).
    // Returns the C struct name. Series is a reference type (arena pointer, shared on assign).
    private string RegisterSeriesStruct(SeriesType st)
    {
        string sig = TypeSig(st);
        if (_seriesSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cser_{_seriesCounter++}";
        _seriesSig2Name[sig] = name;
        _seriesStructs.Add((name, st.ElementType));
        RegisterNestedRecords(st.ElementType);
        return name;
    }

    // The C series-struct name for a series-typed expression (used to pick the per-type ops).
    private string SeriesStructOf(IExpression seriesExpr) =>
        TypeOf(seriesExpr) is SeriesType st
            ? RegisterSeriesStruct(st)
            : throw new CompilerException("series operation on a non-series value.");

    // Ensures a tagged struct exists for `<inner> or failure` (and the inner's nested structs).
    private string RegisterFailableStruct(FailureType ft)
    {
        string sig = TypeSig(ft);
        if (_failableSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cfl_{_failableCounter++}";
        _failableSig2Name[sig] = name;
        _failableStructs.Add((name, ft.Inner));
        RegisterNestedRecords(ft.Inner);
        return name;
    }

    // Ensures a map container struct exists for `map from K to V` (and the K/V nested structs,
    // plus the voidable-V struct that lookups return). Returns the C struct name.
    private string RegisterMapStruct(MapType mt)
    {
        // Voidable-valued maps need lookup-flattening (voidable voidable V → voidable V) — defer.
        if (mt.ValueType is VoidableType)
            throw new CompilerException("maps with voidable values are not yet supported by the compiler.");
        string sig = TypeSig(mt);
        if (_mapSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cmap_{_mapCounter++}";
        _mapSig2Name[sig] = name;
        _mapStructs.Add((name, mt.KeyType, mt.ValueType));
        RegisterNestedRecords(mt.KeyType);
        RegisterNestedRecords(mt.ValueType);
        RegisterVoidableStruct(new VoidableType(mt.ValueType));   // lookup returns voidable V
        return name;
    }

    // Ensures a tagged struct exists for `voidable <inner>` (and its inner's nested structs).
    private string RegisterVoidableStruct(VoidableType vt)
    {
        string sig = TypeSig(vt);
        if (_voidableSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cvd_{_voidableCounter++}";
        _voidableSig2Name[sig] = name;
        _voidableStructs.Add((name, vt.Inner));
        RegisterNestedRecords(vt.Inner);
        return name;
    }

    private IEnumerable<string> NestedRecordDeps(RecordType rt)
    {
        foreach (var t in rt.PositionalTypes)
            if (t is RecordType nrt) yield return RegisterRecordStruct(nrt);
        foreach (var (_, t) in rt.NamedFields)
            if (t is RecordType nrt) yield return RegisterRecordStruct(nrt);
    }

    // One uniform C field spec: CField is the C member name (p0.. positional, cv_x named);
    // Label is the original field name for named fields (printed "name: value"), null for
    // positionals (printed as just the value).
    private readonly record struct FieldSpec(string CField, string? Label, CufetType Type);

    private List<FieldSpec> RecordFields(RecordType rt)
    {
        var f = new List<FieldSpec>();
        for (int i = 0; i < rt.PositionalTypes.Count; i++) f.Add(new($"p{i}", null, rt.PositionalTypes[i]));
        foreach (var (n, t) in rt.NamedFields) f.Add(new(MangleName(n), n, t));
        return f;
    }

    private List<FieldSpec> ObjectFields(ObjectDefinition def)
    {
        var f = new List<FieldSpec>();
        for (int i = 0; i < def.PositionalTypes.Count; i++) f.Add(new($"p{i}", null, def.PositionalTypes[i]));
        foreach (var (n, t) in def.NamedFields.OrderBy(x => x.FieldName, StringComparer.Ordinal)) f.Add(new(MangleName(n), n, t));
        // Embedding: the embedded object is stored as a bare value-struct field (name = the
        // embedded type). Printed without a "name:" label, appended last — matching FormatObject.
        if (def.EmbeddedTypeName != null)
            f.Add(new(MangleName(def.EmbeddedTypeName), null, ObjType(def.EmbeddedTypeName)));
        return f;
    }

    // Emits all synthesized value structs — records, objects, and voidable tagged structs —
    // in dependency order (a struct's nested value-struct fields / a voidable's inner type
    // must be declared before it; the graph is a DAG), each with `_write` and `_eq` helpers.
    private void EmitStructs(StringBuilder sb)
    {
        var specs = new Dictionary<string, (List<FieldSpec> Fields, string WritePrefix)>();
        foreach (var (name, rt) in _recordStructs) specs[name] = (RecordFields(rt), "record");
        foreach (var def in _objectDefs.Values)    specs[ObjStructName(def.Name)] = (ObjectFields(def), def.Name);
        var voidables = new Dictionary<string, CufetType>();
        foreach (var (name, inner) in _voidableStructs) voidables[name] = inner;
        // Failable tagged structs (cfl_N): like voidables but 4-field, and no write/eq
        // (a `T or failure` value is never printed or compared — it's consumed at the call site).
        var failables = new Dictionary<string, CufetType>();
        foreach (var (name, inner) in _failableStructs) failables[name] = inner;
        if (specs.Count == 0 && voidables.Count == 0 && failables.Count == 0) return;

        string? DepName(CufetType t) => t switch
        {
            RecordType rt   => RegisterRecordStruct(rt),
            ObjectType ot   => ObjStructName(ot.Name),
            VoidableType vt => RegisterVoidableStruct(vt),
            FailureType ft  => RegisterFailableStruct(ft),
            _ => null
        };
        bool Known(string? d) => d != null && (specs.ContainsKey(d) || voidables.ContainsKey(d) || failables.ContainsKey(d));

        var emitted = new HashSet<string>();
        var order   = new List<string>();
        void Visit(string cname)
        {
            if (!emitted.Add(cname)) return;
            if (voidables.TryGetValue(cname, out var vInner))       { if (Known(DepName(vInner))) Visit(DepName(vInner)!); }
            else if (failables.TryGetValue(cname, out var fInner))  { if (Known(DepName(fInner))) Visit(DepName(fInner)!); }
            else
                foreach (var fs in specs[cname].Fields)
                {
                    var d = DepName(fs.Type);
                    if (Known(d)) Visit(d!);
                }
            order.Add(cname);
        }
        foreach (var cname in specs.Keys.Concat(voidables.Keys).Concat(failables.Keys).ToList()) Visit(cname);

        sb.AppendLine("// ── Record / object / voidable shapes (value structs) ──");
        foreach (var cname in order)
        {
            if (voidables.TryGetValue(cname, out var inner))
            {
                sb.AppendLine($"typedef struct {{ int has; {EmitCType(inner)} val; }} {cname};");
                continue;
            }
            if (failables.TryGetValue(cname, out var fInner))
            {
                sb.AppendLine($"typedef struct {{ int is_failure; {EmitCType(fInner)} val; const char* message; const char* category; }} {cname};");
                continue;
            }
            sb.AppendLine("typedef struct {");
            foreach (var fs in specs[cname].Fields)
                sb.AppendLine($"    {EmitCType(fs.Type)} {fs.CField};");
            sb.AppendLine($"}} {cname};");
        }
        sb.AppendLine();

        foreach (var cname in order)
        {
            if (failables.ContainsKey(cname)) continue;   // fallible values are never printed
            if (voidables.TryGetValue(cname, out var inner))
            {
                sb.AppendLine($"static void {cname}_write({cname} v) {{ if (v.has) {WriteCall("v.val", inner)}; else printf(\"void\"); }}");
                continue;
            }
            var (fields, prefix) = specs[cname];
            sb.AppendLine($"static void {cname}_write({cname} v) {{");
            sb.AppendLine($"    printf(\"{prefix}(\");");
            bool first = true;
            foreach (var fs in fields)
            {
                if (!first) sb.AppendLine("    printf(\", \");");
                first = false;
                if (fs.Label != null) sb.AppendLine($"    printf(\"{fs.Label}: \");");
                sb.AppendLine($"    {WriteCall($"v.{fs.CField}", fs.Type)};");
            }
            sb.AppendLine("    printf(\")\");");
            sb.AppendLine("}");
        }
        sb.AppendLine();

        // Value equality (records: structural; objects: nominal same-type; voidables: both-void
        // equal, both-present compare inner) — matching the interpreter's ValuesEqual.
        foreach (var cname in order)
        {
            if (failables.ContainsKey(cname)) continue;   // fallible values are never compared
            if (voidables.TryGetValue(cname, out var inner))
            {
                sb.AppendLine($"static int {cname}_eq({cname} a, {cname} b) {{ if (a.has != b.has) return 0; if (!a.has) return 1; return {EqCall("a.val", "b.val", inner)}; }}");
                continue;
            }
            var fields = specs[cname].Fields;
            string cond = fields.Count == 0 ? "1"
                : string.Join(" && ", fields.Select(fs => EqCall($"a.{fs.CField}", $"b.{fs.CField}", fs.Type)));
            sb.AppendLine($"static int {cname}_eq({cname} a, {cname} b) {{ return {cond}; }}");
        }
        sb.AppendLine();
    }

    // Forward-declares each series container (`typedef struct cser_N_s cser_N;`) so record/object
    // value structs (and other series/maps) can hold a `cser_N*` field before its full definition.
    private void EmitSeriesForwardDecls(StringBuilder sb)
    {
        if (_seriesStructs.Count == 0) return;
        foreach (var (name, _) in _seriesStructs) sb.AppendLine($"typedef struct {name}_s {name};");
        // `_write` AND `_eq` are forward-declared so a struct/voidable/map whose field or element is
        // a series can call them from its own `_write`/`_eq` (emitted in EmitStructs, before the
        // series runtime's full definitions). Unlike maps (pointer equality), series equality is a
        // real element-wise function call, so `_eq` needs the forward declaration too.
        foreach (var (name, _) in _seriesStructs) sb.AppendLine($"static void {name}_write({name}* s);");
        foreach (var (name, _) in _seriesStructs) sb.AppendLine($"static int {name}_eq({name}* a, {name}* b);");
        sb.AppendLine();
    }

    // Emits each series container's full struct + helpers: an arena-allocated growable array of T
    // (T stored by value — a value struct copies into the slot, a reference type stores its pointer,
    // matching the interpreter). By-value ops (remove-by-value, equality) use the element's own
    // equality. Generalizes the former number-only CufetSeries to every element type.
    private void EmitSeriesRuntime(StringBuilder sb)
    {
        if (_seriesStructs.Count == 0) return;
        sb.AppendLine("// ── Series containers (arena growable arrays; per element type) ──");
        foreach (var (name, elem) in _seriesStructs)
        {
            string ec = EmitCType(elem);
            sb.AppendLine($"struct {name}_s {{ {ec}* data; int len; int cap; }};");
            sb.AppendLine($"static {name}* {name}_new(void) {{ {name}* s = ({name}*)cufet_arena_alloc(sizeof({name})); s->data = NULL; s->len = 0; s->cap = 0; return s; }}");
            sb.AppendLine($"static void {name}_ensure({name}* s) {{");
            sb.AppendLine($"    if (s->len >= s->cap) {{");
            sb.AppendLine($"        int nc = s->cap == 0 ? 4 : s->cap * 2;");
            sb.AppendLine($"        {ec}* nd = ({ec}*)cufet_arena_alloc((size_t)nc * sizeof({ec}));");
            sb.AppendLine($"        if (s->len > 0) memcpy(nd, s->data, (size_t)s->len * sizeof({ec}));");
            sb.AppendLine($"        s->data = nd; s->cap = nc;");
            sb.AppendLine($"    }}");
            sb.AppendLine($"}}");
            sb.AppendLine($"static void {name}_append({name}* s, {ec} v) {{ {name}_ensure(s); s->data[s->len++] = v; }}");
            sb.AppendLine($"static void {name}_prepend({name}* s, {ec} v) {{ {name}_ensure(s); if (s->len > 0) memmove(s->data + 1, s->data, (size_t)s->len * sizeof({ec})); s->data[0] = v; s->len++; }}");
            // after1: the correct 0-based insertion index (1-based position it inserts after).
            sb.AppendLine($"static void {name}_insert({name}* s, int after1, {ec} v) {{ {name}_ensure(s); int pos = after1; if (s->len > pos) memmove(s->data + pos + 1, s->data + pos, (size_t)(s->len - pos) * sizeof({ec})); s->data[pos] = v; s->len++; }}");
            // idx1: 1-based; pass -1 for "last".
            sb.AppendLine($"static void {name}_remove_at({name}* s, int idx1) {{ int idx = (idx1 < 0) ? s->len - 1 : idx1 - 1; if (s->len - idx - 1 > 0) memmove(s->data + idx, s->data + idx + 1, (size_t)(s->len - idx - 1) * sizeof({ec})); s->len--; }}");
            sb.AppendLine($"static void {name}_remove_value({name}* s, {ec} v) {{ for (int i = 0; i < s->len; i++) {{ if ({EqCall("s->data[i]", "v", elem)}) {{ if (s->len - i - 1 > 0) memmove(s->data + i, s->data + i + 1, (size_t)(s->len - i - 1) * sizeof({ec})); s->len--; return; }} }} }}");
            // Writes as (e1, e2, ...) — no trailing newline, so it nests inside record/object fields.
            sb.AppendLine($"static void {name}_write({name}* s) {{");
            sb.AppendLine($"    printf(\"(\");");
            sb.AppendLine($"    for (int i = 0; i < s->len; i++) {{ if (i > 0) printf(\", \"); {WriteCall("s->data[i]", elem)}; }}");
            sb.AppendLine($"    printf(\")\");");
            sb.AppendLine($"}}");
            // Element-wise, in-order value equality (series are ordered sequences — no canonicalization).
            sb.AppendLine($"static int {name}_eq({name}* a, {name}* b) {{ if (a->len != b->len) return 0; for (int i = 0; i < a->len; i++) if (!({EqCall("a->data[i]", "b->data[i]", elem)})) return 0; return 1; }}");
        }
        sb.AppendLine();
    }

    // Forward-declares each map container (`typedef struct cmap_N_s cmap_N;`) so record/object
    // value structs can hold a `cmap_N*` field before the full definition appears below.
    private void EmitMapForwardDecls(StringBuilder sb)
    {
        if (_mapStructs.Count == 0) return;
        foreach (var (name, _, _) in _mapStructs) sb.AppendLine($"typedef struct {name}_s {name};");
        // `_write` is forward-declared: a record/object/voidable whose field is a map calls it
        // from its own `_write`, which is emitted before the map runtime's full definitions.
        foreach (var (name, _, _) in _mapStructs) sb.AppendLine($"static void {name}_write({name}* m);");
        sb.AppendLine();
    }

    // Emits each map container's full struct + helpers: an arena-allocated association list
    // (parallel key/value arrays) with linear scan. Keys compared by value; get returns voidable V.
    private void EmitMapRuntime(StringBuilder sb)
    {
        if (_mapStructs.Count == 0) return;
        sb.AppendLine("// ── Map containers (arena association lists; linear scan; keys by value) ──");
        foreach (var (name, k, v) in _mapStructs)
        {
            string kc = EmitCType(k), vc = EmitCType(v);
            string cvd = RegisterVoidableStruct(new VoidableType(v));
            sb.AppendLine($"struct {name}_s {{ {kc}* keys; {vc}* vals; int len; int cap; }};");
            sb.AppendLine($"static {name}* {name}_new(void) {{ {name}* m = ({name}*)cufet_arena_alloc(sizeof({name})); m->keys = NULL; m->vals = NULL; m->len = 0; m->cap = 0; return m; }}");
            sb.AppendLine($"static void {name}_ensure({name}* m) {{");
            sb.AppendLine($"    if (m->len >= m->cap) {{");
            sb.AppendLine($"        int nc = m->cap == 0 ? 4 : m->cap * 2;");
            sb.AppendLine($"        {kc}* nk = ({kc}*)cufet_arena_alloc((size_t)nc * sizeof({kc}));");
            sb.AppendLine($"        {vc}* nv = ({vc}*)cufet_arena_alloc((size_t)nc * sizeof({vc}));");
            sb.AppendLine($"        if (m->len > 0) {{ memcpy(nk, m->keys, (size_t)m->len * sizeof({kc})); memcpy(nv, m->vals, (size_t)m->len * sizeof({vc})); }}");
            sb.AppendLine($"        m->keys = nk; m->vals = nv; m->cap = nc;");
            sb.AppendLine($"    }}");
            sb.AppendLine($"}}");
            sb.AppendLine($"static int {name}_index({name}* m, {kc} k) {{ for (int i = 0; i < m->len; i++) if ({EqCall("m->keys[i]", "k", k)}) return i; return -1; }}");
            sb.AppendLine($"static void {name}_put({name}* m, {kc} k, {vc} v) {{ int i = {name}_index(m, k); if (i >= 0) {{ m->vals[i] = v; return; }} {name}_ensure(m); m->keys[m->len] = k; m->vals[m->len] = v; m->len++; }}");
            sb.AppendLine($"static {cvd} {name}_get({name}* m, {kc} k) {{ {cvd} r = {{0}}; int i = {name}_index(m, k); if (i >= 0) {{ r.has = 1; r.val = m->vals[i]; }} return r; }}");
            sb.AppendLine($"static int {name}_has({name}* m, {kc} k) {{ return {name}_index(m, k) >= 0; }}");
            sb.AppendLine($"static void {name}_write({name}* m) {{");
            sb.AppendLine($"    printf(\"map {{\");");
            sb.AppendLine($"    for (int i = 0; i < m->len; i++) {{");
            sb.AppendLine($"        if (i > 0) printf(\", \");");
            sb.AppendLine($"        {WriteCall("m->keys[i]", k)}; printf(\": \"); {WriteCall("m->vals[i]", v)};");
            sb.AppendLine($"    }}");
            sb.AppendLine($"    printf(\"}}\");");
            sb.AppendLine($"}}");
        }
        sb.AppendLine();
    }

    // The C boolean expression comparing two values of type `t` by value.
    // Three-way comparison (<0 / 0 / >0) for sort keys — numbers by decimal compare, text by ordinal.
    private static string CmpCall(string a, string b, CufetType t) => t switch
    {
        NumberType => $"cufet_cmp({a}, {b})",
        TextType   => $"strcmp({a}, {b})",
        _ => throw new CompilerException($"sorting by a '{t.GetType().Name}' key is not supported — sort keys must be number or text."),
    };

    // `<series> sorted [in reverse] [by <field>]` — a NEW series (non-mutating), stably sorted.
    // A stable insertion sort (equal keys keep original order) matches the interpreter's stable
    // OrderBy exactly; C's qsort is NOT stable, so we don't use it. Natural order (number/text) or
    // by a named record/object field. Numbers compare via cufet_cmp, text via ordinal strcmp.
    private string EmitSort(SortExpression sort)
    {
        var st       = (SeriesType)TypeOf(sort.Series);
        string ser   = RegisterSeriesStruct(st);
        var elemType = st.ElementType;
        var keyType  = sort.ByField == null ? elemType : FieldType(elemType, sort.ByField);
        string src   = EmitExpr(sort.Series);
        int id = _freshId++;
        string ssrc = $"cf_ss{id}", dst = $"cf_srt{id}";
        // Key of an element expr: the element itself (natural), or its named field (by-field).
        string KeyOf(string e) => sort.ByField == null ? e : $"({e}).{MangleName(sort.ByField)}";
        string cmp = CmpCall(KeyOf($"{dst}->data[cf_j{id}]"), KeyOf($"cf_k{id}"), keyType);
        string outOfOrder = sort.Reverse ? $"({cmp}) < 0" : $"({cmp}) > 0";
        var b = new StringBuilder();
        b.Append($"{ser}* {ssrc} = {src}; {ser}* {dst} = {ser}_new(); ");
        b.Append($"for (int cf_i{id} = 0; cf_i{id} < {ssrc}->len; cf_i{id}++) {ser}_append({dst}, {ssrc}->data[cf_i{id}]); ");
        b.Append($"for (int cf_a{id} = 1; cf_a{id} < {dst}->len; cf_a{id}++) {{ ");
        b.Append($"{EmitCType(elemType)} cf_k{id} = {dst}->data[cf_a{id}]; int cf_j{id} = cf_a{id} - 1; ");
        b.Append($"while (cf_j{id} >= 0 && {outOfOrder}) {{ {dst}->data[cf_j{id} + 1] = {dst}->data[cf_j{id}]; cf_j{id}--; }} ");
        b.Append($"{dst}->data[cf_j{id} + 1] = cf_k{id}; }}");
        _preEmits.Add(b.ToString());
        return dst;
    }

    // ── Chance (Arc 1E) ───────────────────────────────────────────────────────

    private string EmitRandomNumber(RandomNumber rn)
    {
        _usesChance = true;
        string lo = EmitExpr(rn.Low);
        string hi = EmitExpr(rn.High);
        return $"cufet_random_number({lo}, {hi}, {rn.Line})";
    }

    private string EmitRandomGuess()
    {
        _usesChance = true;
        return "(cufet_rng_below(2) == 1)";   // a fact, uniform over {true, false}
    }

    // `a random item from xs` → voidable element: void on an empty series (matches the interpreter),
    // else a uniform pick. Element-type-general (the series-of-T payoff).
    private string EmitRandomItem(RandomItem ri)
    {
        _usesChance = true;
        var st = (SeriesType)TypeOf(ri.Series);
        string cvd = RegisterVoidableStruct(new VoidableType(st.ElementType));
        string src = EmitExpr(ri.Series);
        int id = _freshId++;
        _preEmits.Add(
            $"{RegisterSeriesStruct(st)}* cf_ri{id} = {src}; int cf_rh{id} = cf_ri{id}->len > 0; " +
            $"{EmitCType(st.ElementType)} cf_rv{id}; " +
            $"if (cf_rh{id}) cf_rv{id} = cf_ri{id}->data[cufet_rng_below(cf_ri{id}->len)];");
        return $"(cf_rh{id} ? ({cvd}){{ .has = 1, .val = cf_rv{id} }} : ({cvd}){{ .has = 0 }})";
    }

    // `randomly shuffled xs` → a NEW arena series (non-mutating, like sorted/unique), Fisher-Yates
    // downward with j in [0, i] — the interpreter's exact procedure (over a different PRNG).
    private string EmitRandomlyShuffled(RandomlyShuffled rs)
    {
        _usesChance = true;
        var st = (SeriesType)TypeOf(rs.Series);
        string ser = RegisterSeriesStruct(st);
        string src = EmitExpr(rs.Series);
        int id = _freshId++;
        _preEmits.Add(
            $"{ser}* cf_sh{id} = {src}; {ser}* cf_sd{id} = {ser}_new(); " +
            $"for (int cf_i{id} = 0; cf_i{id} < cf_sh{id}->len; cf_i{id}++) {ser}_append(cf_sd{id}, cf_sh{id}->data[cf_i{id}]); " +
            $"for (int cf_i{id} = cf_sd{id}->len - 1; cf_i{id} > 0; cf_i{id}--) {{ " +
            $"long long cf_j{id} = cufet_rng_below(cf_i{id} + 1); " +
            $"{EmitCType(st.ElementType)} cf_t{id} = cf_sd{id}->data[cf_i{id}]; " +
            $"cf_sd{id}->data[cf_i{id}] = cf_sd{id}->data[cf_j{id}]; cf_sd{id}->data[cf_j{id}] = cf_t{id}; }}");
        return $"cf_sd{id}";
    }

    // ── Matrix (Arc 1D) ───────────────────────────────────────────────────────

    private string MatrixCType() { _usesMatrix = true; return "CufetMatrix*"; }

    // A +/−/× whose operands are both matrices — routed through the FALLIBLE machinery (dimension
    // mismatch is a Cufet failure the typechecker requires handling for).
    private bool IsMatrixOp(BinaryExpression b) =>
        b.Op is TokenType.Plus or TokenType.Minus or TokenType.Star
        && TypeOf(b.Left) is MatrixType && TypeOf(b.Right) is MatrixType;

    // The raw `matrix or failure` (cfl) for a matrix binary op: the runtime fn returns NULL on a
    // dimension mismatch; the emit site wraps that into the cfl with the interpreter's exact
    // deterministic message + "dimension-mismatch" category.
    private string EmitMatrixOpRaw(BinaryExpression b)
    {
        _usesMatrix = true;
        string cfl = RegisterFailableStruct(new FailureType(MatrixType.Instance));
        string l = EmitExpr(b.Left);
        string r = EmitExpr(b.Right);
        (string fn, string msg) = b.Op switch
        {
            TokenType.Plus  => ("cufet_mat_add", "matrices must have equal dimensions for addition"),
            TokenType.Minus => ("cufet_mat_sub", "matrices must have equal dimensions for subtraction"),
            _               => ("cufet_mat_mul", "left matrix columns must equal right matrix rows for matrix product"),
        };
        int id = _freshId++;
        _preEmits.Add(
            $"{cfl} cf_mx{id}; {{ CufetMatrix* cf_mr{id} = {fn}({l}, {r}); " +
            $"if (cf_mr{id}) {{ cf_mx{id}.is_failure = 0; cf_mx{id}.val = cf_mr{id}; cf_mx{id}.message = 0; cf_mx{id}.category = 0; }} " +
            $"else {{ cf_mx{id}.is_failure = 1; cf_mx{id}.message = \"{msg}\"; cf_mx{id}.category = \"dimension-mismatch\"; }} }}");
        return $"cf_mx{id}";
    }

    private string EmitMatrixSized(MatrixSized ms)
    {
        _usesMatrix = true;
        string r = EmitExpr(ms.Rows);
        string c = EmitExpr(ms.Cols);
        string f = ms.Fill != null ? EmitExpr(ms.Fill) : "cufet_dec_from_ll(0)";
        return $"cufet_mat_sized({r}, {c}, {f}, {ms.Line})";
    }

    private string EmitMatrixAccess(MatrixAccess ma)
    {
        _usesMatrix = true;
        string m = EmitExpr(ma.Matrix);
        string r = EmitExpr(ma.Row);
        string c = EmitExpr(ma.Column);
        return $"cufet_mat_get({m}, cufet_to_int({r}), cufet_to_int({c}), {ma.Line})";
    }

    // `a matrix with ((1, 2), (3, 4))` — dimensions are literal-known; elements evaluated row-major
    // (the interpreter's order, so side effects and preemits sequence identically).
    private string EmitMatrixLiteral(MatrixLiteral ml)
    {
        _usesMatrix = true;
        int rows = ml.Rows.Count, cols = ml.Rows[0].Count;
        int id = _freshId++;
        string tmp = $"cf_mt{id}";
        _preEmits.Add($"CufetMatrix* {tmp} = cufet_mat_new({rows}, {cols});");
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                string el = EmitExpr(ml.Rows[r][c]);
                _preEmits.Add($"{tmp}->data[{r * cols + c}] = {el};");
            }
        return tmp;
    }

    private string EqCall(string a, string b, CufetType t) => t switch
    {
        NumberType => $"cufet_cmp({a}, {b}) == 0",
        FactType   => $"({a} == {b})",
        TextType   => $"strcmp({a}, {b}) == 0",
        SeriesType st => $"{RegisterSeriesStruct(st)}_eq({a}, {b})",
        RecordType rt => $"{RegisterRecordStruct(rt)}_eq({a}, {b})",
        ObjectType ot => $"{ObjStructName(ot.Name)}_eq({a}, {b})",
        VoidableType vt => $"{RegisterVoidableStruct(vt)}_eq({a}, {b})",
        MapType => $"({a} == {b})",   // maps: reference (pointer) equality, like the interpreter
        MatrixType => $"({a} == {b})",   // matrices: reference equality (interpreter ValuesEqual fallthrough)
        _ => throw new CompilerException($"equality on a '{t.GetType().Name}' is not yet supported by the compiler.")
    };

    // The C expression that writes `valExpr` inline (no trailing newline), dispatching
    // on its static type — used by record/object write helpers and by State.
    private string WriteCall(string valExpr, CufetType t) => t switch
    {
        NumberType => $"cufet_write_number({valExpr})",
        FactType   => $"cufet_write_fact({valExpr})",
        TextType   => $"cufet_write_text({valExpr})",
        SeriesType st => $"{RegisterSeriesStruct(st)}_write({valExpr})",
        RecordType rt => $"{RegisterRecordStruct(rt)}_write({valExpr})",
        ObjectType ot => $"{ObjStructName(ot.Name)}_write({valExpr})",
        VoidableType vt => $"{RegisterVoidableStruct(vt)}_write({valExpr})",
        MapType mt => $"{RegisterMapStruct(mt)}_write({valExpr})",
        MatrixType => $"cufet_mat_write({valExpr})",
        _ => throw new CompilerException(
                 $"printing a '{t.GetType().Name}' is not yet supported by the compiler.")
    };

    private void EmitBlock(StringBuilder sb, IReadOnlyList<IStatement> body, string indent)
    {
        // Guard narrowing (mirrors the interpreter's type checker): after an exiting guard —
        // a single-arm `if (cond) { … return … }` with no else — the statements that follow run
        // only when `cond` was false, so a voidable var proven non-void by ¬cond reads as `.val`
        // for the rest of the block. Undone at block end so it never leaks to a sibling block.
        var guardNarrowed = new List<(string Name, CufetType? Prev, bool Had)>();
        foreach (var stmt in body)
        {
            EmitStatement(sb, stmt, indent);
            if (stmt is IfStatement { Arms.Count: 1, ElseBody: null } guard
                && BlockAlwaysExits(guard.Arms[0].Body))
            {
                foreach (var (name, inner) in GuardNarrowings(guard.Arms[0].Condition))
                {
                    bool had = _narrowedVars.TryGetValue(name, out var prev);
                    guardNarrowed.Add((name, had ? prev : null, had));
                    _narrowedVars[name] = inner;
                }
            }
        }
        for (int i = guardNarrowed.Count - 1; i >= 0; i--)
        {
            var (name, prev, had) = guardNarrowed[i];
            if (had) _narrowedVars[name] = prev!; else _narrowedVars.Remove(name); // had ⇒ prev non-null
        }
    }

    // Voidable narrowings implied by the negation of a guard condition (fall-through path):
    //   `x is void`   → x non-void → (x, inner);   `A or B` (¬ = ¬A ∧ ¬B) → collect from each.
    // `and` is not recursed: ¬(A ∧ B) narrows neither side. Only voidable narrowing is emitted —
    // that's all the compiler's `.val` access mechanism supports (and all the docs' idioms need).
    private IEnumerable<(string Name, CufetType Inner)> GuardNarrowings(IExpression cond)
    {
        if (cond is BinaryExpression { Op: TokenType.Or } orE)
        {
            foreach (var g in GuardNarrowings(orE.Left))  yield return g;
            foreach (var g in GuardNarrowings(orE.Right)) yield return g;
            yield break;
        }
        if (cond is BinaryExpression { Op: TokenType.Equal } b)
        {
            var varSide = b.Left is VoidLiteral ? b.Right : b.Left;
            var other   = b.Left is VoidLiteral ? b.Left  : b.Right;
            if (other is VoidLiteral && varSide is VariableReference vr && TypeOf(vr) is VoidableType vt)
                yield return (vr.Name, vt.Inner);
        }
    }

    // Every path through `body` ends at a return. Mirrors the type checker's DefinitelyReturns
    // (loops don't count — they may run zero times). Used to recognize exiting guards.
    private static bool BlockAlwaysExits(IReadOnlyList<IStatement> body)
    {
        foreach (var s in body)
        {
            if (s is ReturnStatement) return true;
            if (s is IfStatement { ElseBody: not null } ifs
                && ifs.Arms.All(a => BlockAlwaysExits(a.Body)) && BlockAlwaysExits(ifs.ElseBody))
                return true;
        }
        return false;
    }

    private void EmitStatement(StringBuilder sb, IStatement stmt, string indent)
    {
        switch (stmt)
        {
            case StateStatement s:
            {
                string valExpr = EmitExpr(s.Value);
                FlushPreEmits(sb, indent);
                var t = TypeOf(s.Value);
                string printStmt = t switch
                {
                    NumberType    => $"cufet_print_number({valExpr})",
                    FactType      => $"cufet_print_fact({valExpr})",
                    TextType      => $"cufet_print_text({valExpr})",
                    SeriesType st => $"{RegisterSeriesStruct(st)}_write({valExpr}); printf(\"\\n\")",
                    RecordType rt   => $"{RegisterRecordStruct(rt)}_write({valExpr}); printf(\"\\n\")",
                    ObjectType ot   => $"{ObjStructName(ot.Name)}_write({valExpr}); printf(\"\\n\")",
                    VoidableType vt => $"{RegisterVoidableStruct(vt)}_write({valExpr}); printf(\"\\n\")",
                    MapType mt      => $"{RegisterMapStruct(mt)}_write({valExpr}); printf(\"\\n\")",
                    MatrixType      => $"cufet_mat_write({valExpr}); printf(\"\\n\")",
                    _ => throw new CompilerException($"State of a '{t.GetType().Name}' is not yet supported by the compiler.")
                };
                sb.AppendLine($"{indent}{printStmt};");
                break;
            }

            case DefineStatement d:
            {
                string valExpr = EmitExpr(d.Value);
                FlushPreEmits(sb, indent);
                var vt = TypeOf(d.Value);
                _varTypes[d.Name] = vt;
                // 'permanently' fixes the binding — const on the value's C type. Series/maps
                // are arena pointers; leave those non-const (const applies to value types).
                bool constable = vt is NumberType or FactType or TextType or RecordType;
                string decl = (d.Permanent && constable) ? "const " + EmitCType(vt) : EmitCType(vt);
                sb.AppendLine($"{indent}{decl} {MangleName(d.Name)} = {valExpr};");
                break;
            }

            case BecomesStatement b:
            {
                // Coerce so `x becomes 5` / `x becomes void` widens into x's voidable type.
                _varTypes.TryGetValue(b.Name, out var targetType);
                string valExpr = EmitAsType(b.Value, targetType);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{MangleName(b.Name)} = {valExpr};");
                _narrowedVars.Remove(b.Name);   // reassignment clears any active narrowing
                break;
            }

            case ReturnStatement ret:
                if (_currentTaskReturn is { } tctx)
                {
                    // Inside a named task's thread function: a return heap-bridges the value across the
                    // task→awaiter boundary (the task's arena is torn down below, so the result must be
                    // copied to the heap first), then unwinds the task arena and frees the arg struct.
                    // The result C type is a self-contained value struct this slice (number/fact +
                    // voidable/failable), so the heap copy is a deep copy — no arena pointers inside.
                    if (ret.Value != null && tctx.ResultCType != null)
                    {
                        string retExpr = EmitAsType(ret.Value, _currentReturnType);
                        FlushPreEmits(sb, indent);
                        int rid = _freshId++;
                        sb.AppendLine($"{indent}{tctx.ResultCType}* cf_tret{rid} = ({tctx.ResultCType}*)malloc(sizeof({tctx.ResultCType}));");
                        sb.AppendLine($"{indent}*cf_tret{rid} = {retExpr};");
                        sb.AppendLine($"{indent}{FileCleanupStmts(0)}cufet_arena_pop(); free(cf_a); return cf_tret{rid};");
                    }
                    else
                    {
                        // A bare `return.` (or a value dropped by a fire-and-forget task): no result.
                        sb.AppendLine($"{indent}{FileCleanupStmts(0)}cufet_arena_pop(); free(cf_a); return NULL;");
                    }
                    break;
                }
                if (ret.Value == null)
                    sb.AppendLine($"{indent}{FileCleanupStmts(0)}return;");
                else
                {
                    // Coerce so `return <T>` / `return void` widens into a voidable return type.
                    // Value is materialized first (preemits), THEN open files close (a returned
                    // arena value never references a FILE*), THEN return.
                    string retExpr = EmitAsType(ret.Value, _currentReturnType);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}{FileCleanupStmts(0)}return {retExpr};");
                }
                break;

            case CastStatement cs:
            {
                // Void free-function call or void method dispatch (statement position).
                string call = EmitCall(cs.Function, cs.Args);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{call};");
                break;
            }

            case FileWriteStatement fw:
                EmitFileWrite(sb, fw, indent);
                break;

            case WithOpenStatement wos:
                EmitWithOpen(sb, wos, indent);
                break;

            case PipeExpression pipeStmt when !FlattenPipeAll(pipeStmt).TrueForAll(s => s is RunExpression):
                // A TASK pipe (function stages connected by channels) — distinct from a subprocess
                // pipe. Spawns each stage as a thread streaming through channels (CONC.D).
                EmitTaskPipe(sb, FlattenPipeAll(pipeStmt), indent);
                break;

            case PipeExpression pipeStmt:
            {
                // Bare `run X | run Y.` statement — run the pipeline, write its final stdout to
                // stdout and aggregated stderr to stderr (the shell pattern). A launch failure
                // routes to the enclosing Try, or aborts.
                string raw = EmitRunRaw(pipeStmt);
                FlushPreEmits(sb, indent);
                string cr = RegisterRecordStruct(RunResultRecordType);
                string fOut = MangleName("output"), fErr = MangleName("errors");
                if (_currentTryHandler is { } h)
                    sb.AppendLine($"{indent}if ({raw}.is_failure) {{ {h.FailVar}.message = {raw}.message; {h.FailVar}.category = {raw}.category; {FileCleanupStmts(h.FileDepth)}goto {h.Label}; }}");
                else
                    sb.AppendLine($"{indent}if ({raw}.is_failure) {{ fprintf(stderr, \"%s\\n\", {raw}.message); exit(1); }}");
                sb.AppendLine($"{indent}fputs({raw}.val.{fErr}, stderr); fputs({raw}.val.{fOut}, stdout);");
                break;
            }

            case WriteToStreamStatement wts:
            {
                // write <text> to <stream> — incremental, no newline added (fputs); flushed at close.
                string v = EmitExpr(wts.Value);
                FlushPreEmits(sb, indent);
                string strm = EmitExpr(wts.Stream);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}fputs({v}, {strm});");
                break;
            }

            case OutputStatement os:
            {
                // `output <v>` inside a pipe stage → send into the stage's (thread-local) output
                // channel — the same heap-bridged channel send as A+B. Number-only this slice.
                if (TypeOf(os.Value) is not NumberType)
                    throw new CompilerException("task pipes stream numbers this slice (text/reference elements are the channel-of-T follow-on).");
                _usesConcurrency = true;
                string v = EmitExpr(os.Value);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_chan_send(cufet_pipe_out, {v});");
                break;
            }

            case ForEachFromInputStatement fi:
                EmitForEachFromInput(sb, fi, indent);
                break;

            case BindStatement:
                throw new CompilerException("Nested function declarations and closures are not yet supported by the compiler.");

            case PullRabbitStatement prs:
            {
                // A rabbit is an arena scope AND the structured-concurrency boundary: it tracks
                // the pthreads + channels created inside it, joins all tasks and frees all channels
                // at Done. (before arena_pop) — so tasks provably can't outlive their rabbit.
                var inner = indent + "    ";
                string n = (_taskCounter++).ToString();
                sb.AppendLine($"{indent}cufet_arena_push();");
                sb.AppendLine($"{indent}{{");
                if (_usesConcurrency)
                {
                    sb.AppendLine($"{inner}pthread_t cf_thr{n}[CUFET_TASK_MAX]; int cf_nthr{n} = 0;");
                    sb.AppendLine($"{inner}cufet_chan* cf_chan{n}[CUFET_TASK_MAX]; int cf_nchan{n} = 0;");
                    // cf_jflag[k] marks a task already joined at its await site (CONC.C), so Done.
                    // does not re-join it (double pthread_join is undefined). Zero = not yet joined.
                    sb.AppendLine($"{inner}int cf_jflag{n}[CUFET_TASK_MAX] = {{0}};");
                    sb.AppendLine($"{inner}(void)cf_thr{n}; (void)cf_chan{n}; (void)cf_jflag{n};");
                    _rabbitCtx.Add(n);
                }
                EmitBlock(sb, prs.Body, inner);
                if (_usesConcurrency)
                {
                    _rabbitCtx.RemoveAt(_rabbitCtx.Count - 1);
                    // Structured join: reap every not-yet-awaited task, freeing its result heap-bridge
                    // (fire-and-forget tasks return NULL → free(NULL) is a no-op; a named-but-never-
                    // awaited task returns its malloc'd result → freed here, so no leak). Awaited tasks
                    // (cf_jflag set) were already joined + freed at their await site. Then free channels.
                    sb.AppendLine($"{inner}for (int cf_ji = 0; cf_ji < cf_nthr{n}; cf_ji++) if (!cf_jflag{n}[cf_ji]) {{ void* cf_jr = NULL; pthread_join(cf_thr{n}[cf_ji], &cf_jr); free(cf_jr); }}");
                    sb.AppendLine($"{inner}for (int cf_ci = 0; cf_ci < cf_nchan{n}; cf_ci++) cufet_chan_free(cf_chan{n}[cf_ci]);");
                }
                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}cufet_arena_pop();");
                break;
            }

            case SendStatement snd:
            {
                if (TypeOf(snd.Value) is not NumberType)
                    throw new CompilerException("channels are number-only in this concurrency slice (text/reference elements are deferred).");
                _usesConcurrency = true;
                string val = EmitExpr(snd.Value);
                FlushPreEmits(sb, indent);
                string ch = EmitExpr(snd.Channel);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_chan_send({ch}, {val});");
                break;
            }

            case CloseStatement cls:
            {
                _usesConcurrency = true;
                string ch = EmitExpr(cls.Channel);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_chan_close({ch});");
                break;
            }

            case LaunchTaskStatement lts:
                EmitLaunchTask(sb, lts, indent);
                break;

            case YieldStatement:
                // `Yield.` — a cooperative interrupt checkpoint (CONC.E). In the interpreter it also
                // hands the scheduler a turn; natively the OS scheduler does that, so this is purely
                // the interrupt check: if a SIGINT is pending, unwind to this thread's landing pad.
                _usesSignals = true;
                sb.AppendLine($"{indent}cufet_checkpoint();");
                break;

            case AcknowledgeInterruptStatement:
                // `Acknowledge the interrupt.` — clear the flag, so a cooperatively-handled interrupt
                // does not later unwind at a checkpoint (mirrors the interpreter's _interruptRequested=false).
                _usesSignals = true;
                sb.AppendLine($"{indent}cufet_interrupted = 0;");
                break;

            case SeedChanceStatement ss:
            {
                // `Seed the chance with N.` — reseed the PRNG (seed truncated to integer, like the
                // interpreter's (int)(decimal) cast). Guarantee: self-consistent WITHIN this backend.
                _usesChance = true;
                string seed = EmitExpr(ss.Seed);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_rng_seed(cufet_to_int({seed}));");
                break;
            }

            case PullStatement ps:
            {
                // `Pull a book on <name>. … Done.` — books are BUILTIN + compile-time-resolved, so this
                // is purely a scope: register each alias (localName → book) for member-dispatch routing,
                // emit the body in a C block (scopes body-locals like the interpreter's EnterScope), then
                // unregister. No arena push (books allocate nothing), no runtime book value, no linking.
                var added = new List<string>();
                foreach (var (bookName, localName) in ps.Books)
                {
                    _bookAliases[localName] = bookName.ToLowerInvariant();
                    added.Add(localName);
                }
                sb.AppendLine($"{indent}{{");
                // Binds in the body were HOISTED to free functions at Generate time — skip them here.
                EmitBlock(sb, ps.Body.Where(s => s is not BindStatement).ToList(), indent + "    ");
                sb.AppendLine($"{indent}}}");
                foreach (var l in added) _bookAliases.Remove(l);
                break;
            }

            case SeriesAddStatement sa:
            {
                string ser = SeriesStructOf(sa.Series);
                string valExpr = EmitExpr(sa.Value);
                FlushPreEmits(sb, indent);
                string serExpr = EmitExpr(sa.Series);
                FlushPreEmits(sb, indent);
                if (sa.ToStart)
                    sb.AppendLine($"{indent}{ser}_prepend({serExpr}, {valExpr});");
                else if (sa.AfterIndex == null)
                    sb.AppendLine($"{indent}{ser}_append({serExpr}, {valExpr});");
                else
                {
                    string idxExpr = EmitExpr(sa.AfterIndex);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}{ser}_insert({serExpr}, cufet_to_int({idxExpr}), {valExpr});");
                }
                break;
            }

            case SeriesRemoveAtStatement sra:
            {
                string ser = SeriesStructOf(sra.Series);
                string serExpr = EmitExpr(sra.Series);
                FlushPreEmits(sb, indent);
                if (sra.Index == null)
                    sb.AppendLine($"{indent}{ser}_remove_at({serExpr}, -1);");
                else
                {
                    string idxExpr = EmitExpr(sra.Index);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}{ser}_remove_at({serExpr}, cufet_to_int({idxExpr}));");
                }
                break;
            }

            case SeriesRemoveValueStatement srv:
            {
                string ser = SeriesStructOf(srv.Series);
                string valExpr = EmitExpr(srv.Value);
                FlushPreEmits(sb, indent);
                string serExpr = EmitExpr(srv.Series);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{ser}_remove_value({serExpr}, {valExpr});");
                break;
            }

            case SeriesSetStatement ss when TypeOf(ss.Series) is RecordType or ObjectType:
            {
                // Positional field assignment on a record/object: the Nth of x becomes v.
                string baseExpr = EmitExpr(ss.Series);
                string valExpr  = EmitExpr(ss.Value);
                FlushPreEmits(sb, indent);
                int idx0 = TypeOf(ss.Series) is ObjectType sot
                    ? ObjectPositionalIndex(sot.Name, ss.Index)
                    : LiteralIndex(ss.Index) - 1;
                sb.AppendLine($"{indent}({baseExpr}).p{idx0} = {valExpr};");
                break;
            }

            case SeriesSetStatement ss:
            {
                string serExpr = EmitExpr(ss.Series);
                FlushPreEmits(sb, indent);
                string valExpr = EmitExpr(ss.Value);
                FlushPreEmits(sb, indent);
                if (ss.Index == null)
                    sb.AppendLine($"{indent}{serExpr}->data[{serExpr}->len - 1] = {valExpr};");
                else
                {
                    string idxExpr = EmitExpr(ss.Index);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}{serExpr}->data[cufet_to_int({idxExpr}) - 1] = {valExpr};");
                }
                break;
            }

            case RecordNamedSetStatement rns:
                // the <field> of <record/object> becomes <value> — routes through a setter
                // if the member has one, else a raw in-place field write (value semantics).
                EmitMemberSet(sb, indent, rns.Record, rns.FieldName, rns.Value);
                break;

            case PossessiveSetStatement pss:
                // alice's age becomes 31 / one's age becomes 31 — same, setter-aware.
                EmitMemberSet(sb, indent, pss.Target, pss.Member, pss.Value);
                break;

            case MapSetStatement mss:
            {
                // In m, the entry for k becomes v — scan-update-or-append (cmap put).
                string name    = MapName(mss.Map);
                var    valType = ((MapType)TypeOf(mss.Map)).ValueType;
                string mapExpr = EmitExpr(mss.Map);
                string keyExpr = EmitExpr(mss.Key);
                string valExpr = EmitAsType(mss.Value, valType);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{name}_put({mapExpr}, {keyExpr}, {valExpr});");
                break;
            }

            case ObjectDefinition:
            case GetterDeclaration:
            case SetterDeclaration:
                break;   // declarations — structs, methods, getters, setters emitted in the prelude

            case IfStatement ifStmt:
                EmitIf(sb, ifStmt, indent);
                break;

            case WhileStatement ws:
                sb.AppendLine($"{indent}while ({EmitExpr(ws.Condition)}) {{");
                EmitLoopBody(sb, ws.Body, indent + "    ");
                sb.AppendLine($"{indent}}}");
                break;

            case RepeatUntilStatement ru:
                sb.AppendLine($"{indent}do {{");
                EmitLoopBody(sb, ru.Body, indent + "    ");
                sb.AppendLine($"{indent}}} while (!({EmitExpr(ru.Condition)}));");
                break;

            case StopStatement:
                sb.AppendLine($"{indent}{FileCleanupStmts(CurrentLoopFileDepth())}break;");
                break;

            case SkipStatement:
                sb.AppendLine($"{indent}{FileCleanupStmts(CurrentLoopFileDepth())}continue;");
                break;

            case TryStatement ts:
                EmitTryStatement(sb, ts, indent);
                break;

            case SuppressStatement:
                throw new CompilerException("'Suppress' is part of exception handling (runtime signals), not yet supported by the compiler.");

            case ForEachStatement fe when fe.Series is RangeExpression range:
                EmitForEachRange(sb, fe, range, indent);
                break;

            case ForEachStatement fe when TypeOf(fe.Series) is MapType:
                EmitForEachMap(sb, fe, indent);
                break;

            case ForEachStatement fe:
                EmitForEachSeries(sb, fe, indent);
                break;

            default:
                throw new CompilerException(
                    $"'{stmt.GetType().Name}' is not yet supported by the compiler.");
        }
    }

    private void EmitIf(StringBuilder sb, IfStatement ifStmt, string indent)
    {
        var inner = indent + "    ";
        var first = ifStmt.Arms[0];
        // Condition is emitted BEFORE narrowing so `x is not void` reads x's voidable form.
        sb.AppendLine($"{indent}if ({EmitExpr(first.Condition)}) {{");
        EmitNarrowedBlock(sb, NotVoidNarrow(first.Condition), first.Body, inner);

        for (int i = 1; i < ifStmt.Arms.Count; i++)
        {
            var arm = ifStmt.Arms[i];
            sb.AppendLine($"{indent}}} else if ({EmitExpr(arm.Condition)}) {{");
            EmitNarrowedBlock(sb, NotVoidNarrow(arm.Condition), arm.Body, inner);
        }

        if (ifStmt.ElseBody != null)
        {
            sb.AppendLine($"{indent}}} else {{");
            EmitBlock(sb, ifStmt.ElseBody, inner);
        }

        sb.AppendLine($"{indent}}}");
    }

    // Emits a block with an optional voidable variable narrowed to its inner type inside it.
    private void EmitNarrowedBlock(StringBuilder sb, (string Name, CufetType Inner)? narrow,
                                   IReadOnlyList<IStatement> body, string indent)
    {
        if (narrow is not var (name, inner) || narrow is null) { EmitBlock(sb, body, indent); return; }
        bool had = _narrowedVars.TryGetValue(name, out var prev);
        _narrowedVars[name] = inner;
        EmitBlock(sb, body, indent);
        if (had) _narrowedVars[name] = prev!; else _narrowedVars.Remove(name);  // had ⇒ prev non-null
    }

    // `x is not void` (x a voidable variable) → (x, inner); narrows x in the then-branch only.
    // (The interpreter narrows the `is not void` then-branch, not the `is void` else-branch.)
    private (string Name, CufetType Inner)? NotVoidNarrow(IExpression cond)
    {
        if (cond is not BinaryExpression { Op: TokenType.NotEqual } b) return null;
        var (varSide, other) = b.Left is VoidLiteral ? (b.Right, b.Left) : (b.Left, b.Right);
        if (other is VoidLiteral && varSide is VariableReference vr && TypeOf(vr) is VoidableType vt)
            return (vr.Name, vt.Inner);
        return null;
    }

    // Range semantics mirror the interpreter exactly:
    //   - inclusive both bounds
    //   - ascending when start <= end, descending otherwise
    //   - step is a positive magnitude; direction determined by start/end
    // The loop counter and bounds are decimals so fractional ranges stay exact.
    // cf_ temporaries (not cv_) avoid collision with user-declared variables.
    private void EmitForEachRange(StringBuilder sb, ForEachStatement fe, RangeExpression range, string indent)
    {
        var inner      = indent + "    ";
        var loopIndent = inner  + "    ";
        int id = _forCounter++;
        string s  = $"cf_s{id}";
        string e  = $"cf_e{id}";
        string st = $"cf_st{id}";
        string d  = $"cf_d{id}";
        string iterName = MangleName(fe.IteratorName ?? "it");

        sb.AppendLine($"{indent}{{");
        string startExpr = EmitExpr(range.Start); FlushPreEmits(sb, inner);
        sb.AppendLine($"{inner}CufetDec {s}  = {startExpr};");
        string endExpr = EmitExpr(range.End); FlushPreEmits(sb, inner);
        sb.AppendLine($"{inner}CufetDec {e}  = {endExpr};");
        string stepExpr = range.Step != null ? EmitExpr(range.Step) : "cufet_dec_from_ll(1)";
        FlushPreEmits(sb, inner);
        sb.AppendLine($"{inner}CufetDec {st} = {stepExpr};");
        sb.AppendLine($"{inner}int {d}  = cufet_cmp({s}, {e}) <= 0 ? 1 : -1;");
        sb.AppendLine($"{inner}for (CufetDec {iterName} = {s}; {d} > 0 ? cufet_cmp({iterName}, {e}) <= 0 : cufet_cmp({iterName}, {e}) >= 0; {iterName} = {d} > 0 ? cufet_add({iterName}, {st}) : cufet_sub({iterName}, {st})) {{");
        // Track the loop variable's type (a number) so it resolves in the body — and so a task
        // spawned in the body can capture it (consistent with the series/map foreach).
        string raw = fe.IteratorName ?? "it";
        var saved = _varTypes.TryGetValue(raw, out var prev) ? prev : null;
        _varTypes[raw] = TNumber;
        EmitLoopBody(sb, fe.Body, loopIndent);
        if (saved != null) _varTypes[raw] = saved; else _varTypes.Remove(raw);
        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");
    }

    // For each loop over a materialized series (non-range).
    // cf_ temporaries avoid collisions with user variables.
    private void EmitForEachSeries(StringBuilder sb, ForEachStatement fe, string indent)
    {
        var inner      = indent + "    ";
        var loopIndent = inner  + "    ";
        var st         = (SeriesType)TypeOf(fe.Series);
        string name    = RegisterSeriesStruct(st);
        var elem       = st.ElementType;
        int id = _forCounter++;
        string ser = $"cf_ser{id}";
        string idx = $"cf_i{id}";
        string rawName  = fe.IteratorName ?? "it";
        string iterName = MangleName(rawName);

        string serExpr = EmitExpr(fe.Series);
        FlushPreEmits(sb, indent);

        // The iterator's type is the element type — track it so the body's TypeOf resolves
        // print/access/equality correctly (mirrors the map-pair foreach).
        var savedType = _varTypes.TryGetValue(rawName, out var prev) ? prev : null;
        _varTypes[rawName] = elem;

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}{name}* {ser} = {serExpr};");
        sb.AppendLine($"{inner}int {ser}_n = {ser}->len;");
        sb.AppendLine($"{inner}for (int {idx} = 0; {idx} < {ser}_n; {idx}++) {{");
        sb.AppendLine($"{loopIndent}{EmitCType(elem)} {iterName} = {ser}->data[{idx}];");
        EmitLoopBody(sb, fe.Body, loopIndent);
        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");

        if (savedType != null) _varTypes[rawName] = savedType; else _varTypes.Remove(rawName);
    }

    // For each pair in a map — iterates the association list in insertion order, binding the
    // pair's key/value to cv_<pair>_key / cv_<pair>_value (see EmitMemberAccess for MappingType).
    private void EmitForEachMap(StringBuilder sb, ForEachStatement fe, string indent)
    {
        var inner      = indent + "    ";
        var loopIndent = inner  + "    ";
        var mt = (MapType)TypeOf(fe.Series);
        string name = RegisterMapStruct(mt);
        int id = _forCounter++;
        string m = $"cf_m{id}", idx = $"cf_i{id}";
        string pair = MangleName(fe.IteratorName ?? "it");

        string mapExpr = EmitExpr(fe.Series);
        FlushPreEmits(sb, indent);

        var savedType = _varTypes.TryGetValue(fe.IteratorName ?? "it", out var prev) ? prev : null;
        _varTypes[fe.IteratorName ?? "it"] = new MappingType(mt.KeyType, mt.ValueType);

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}{name}* {m} = {mapExpr};");
        sb.AppendLine($"{inner}int {m}_n = {m}->len;");
        sb.AppendLine($"{inner}for (int {idx} = 0; {idx} < {m}_n; {idx}++) {{");
        sb.AppendLine($"{loopIndent}{EmitCType(mt.KeyType)} {pair}_key = {m}->keys[{idx}];");
        sb.AppendLine($"{loopIndent}{EmitCType(mt.ValueType)} {pair}_value = {m}->vals[{idx}];");
        EmitLoopBody(sb, fe.Body, loopIndent);
        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");

        if (savedType != null) _varTypes[fe.IteratorName ?? "it"] = savedType;
        else _varTypes.Remove(fe.IteratorName ?? "it");
    }

    // Try to: <body>. In case of failure: <handler>. — value-level failure handling.
    // A failing fallible call in the body records the failure and gotos the handler; the
    // handler binds `the failure`. In case of exception (runtime signals) is deferred.
    private void EmitTryStatement(StringBuilder sb, TryStatement trySt, string indent)
    {
        if (trySt.ExceptionHandler != null)
            throw new CompilerException("'In case of exception' (runtime-signal handling → sigaction) is not yet supported by the compiler.");
        if (trySt.FailureHandler == null)
            throw new CompilerException("a Try block needs an 'In case of failure' handler.");

        int id = _forCounter++;
        string label = $"try{id}_handler", end = $"try{id}_end", failVar = $"cf_fail{id}";
        var inner = indent + "    ";

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}CufetFailure {failVar};");

        var savedHandler = _currentTryHandler;
        _currentTryHandler = (label, failVar, _openFiles.Count);   // files opened in the body close on unwind
        EmitBlock(sb, trySt.Body, inner);
        _currentTryHandler = savedHandler;

        sb.AppendLine($"{inner}goto {end};");
        sb.AppendLine($"{inner}{label}:;");

        var savedFailVar = _currentFailVar;
        var savedType    = _varTypes.TryGetValue("the failure", out var prev) ? prev : null;
        _currentFailVar = failVar;
        _varTypes["the failure"] = TFailMarker;
        EmitBlock(sb, trySt.FailureHandler, inner);
        _currentFailVar = savedFailVar;
        if (savedType != null) _varTypes["the failure"] = savedType; else _varTypes.Remove("the failure");

        sb.AppendLine($"{inner}{end}:;");
        sb.AppendLine($"{indent}}}");
    }

    // Emits all accumulated pre-emit lines (series literal constructions, etc.)
    // then clears the list. Must be called before emitting the statement that
    // uses the expression returned from EmitExpr.
    private void FlushPreEmits(StringBuilder sb, string indent)
    {
        foreach (var line in _preEmits)
            sb.AppendLine($"{indent}{line}");
        _preEmits.Clear();
    }

    // Full static type of an expression. The program already type-checked, so this is
    // a straightforward re-derivation (no error handling) used to pick C declaration
    // types, print helpers, comparison strategy, and to discover record shapes.
    private CufetType TypeOf(IExpression expr) => expr switch
    {
        NumberLiteral         => TNumber,
        BooleanLiteral        => TFact,
        StringLiteral         => TText,
        RangeExpression       => new SeriesType(TNumber),
        SeriesLiteral sl      => new SeriesType(SeriesElementType(sl)),
        SeriesLength          => TNumber,
        SeriesAccess sa       => SeriesAccessType(sa),
        UnaryExpression u     => u.Op == TokenType.Not ? TFact : TNumber,
        // A matrix binary op's VALUE type is matrix (the raw `matrix or failure` is seen only by
        // FallibleReturnType — same unwrap convention as fallible calls).
        BinaryExpression b    => IsMatrixOp(b) ? MatrixType.Instance : IsArithmeticOp(b.Op) ? TNumber : TFact,
        MatrixLiteral         => MatrixType.Instance,
        MatrixSized           => MatrixType.Instance,
        MatrixAccess          => TNumber,
        MatrixRows            => TNumber,
        MatrixColumns         => TNumber,
        VariableReference vr  => vr.Name == "input" ? new ReadableStreamType(TText)   // `the input` = stdin
                               : _narrowedVars.TryGetValue(vr.Name, out var nt) ? nt
                               : _varTypes.TryGetValue(vr.Name, out var t) ? t : TNumber,
        CastExpression c      => CastReturnType(c),
        RecordLiteral rl      => new RecordType(
                                     rl.PositionalFields.Select(TypeOf).ToList(),
                                     rl.NamedFields.Select(f => (f.Name, TypeOf(f.Value))).ToList()),
        RecordNamedAccess rna => FieldType(TypeOf(rna.Record), rna.FieldName),
        ObjectLiteral ol      => ObjType(ol.TypeName),
        PossessiveAccess pa   => pa.Target is VariableReference bvr && _bookAliases.TryGetValue(bvr.Name, out var bn)
                                     ? BookConstantType(bn, pa.Member)                    // math's pi / e
                                     : FieldType(TypeOf(pa.Target), pa.Member),
        VoidLiteral           => TVoid,
        ButVoidDefault bvd    => TypeOf(bvd.Voidable) is VoidableType vt ? vt.Inner : TypeOf(bvd.Default),
        MapLiteral ml         => MapLiteralType(ml),
        MapLookup mlk         => new VoidableType(MapValueType(mlk.Map)),
        MapHasKey             => TFact,
        MapHasEntry           => TFact,
        MapSize               => TNumber,
        FailureLiteral        => TFailMarker,
        // The operand's raw `T or failure` type comes from FallibleReturnType (TypeOf already
        // unwraps a fallible expr to its inner T, so `TypeOf(...) is FailureType` would never hit).
        FailureFallback ff    => FallibleReturnType(ff.Fallible) is { } ft ? ft.Inner : TypeOf(ff.Default),
        FailurePropagate fp   => FallibleReturnType(fp.Fallible) is { } ft2 ? ft2.Inner : TypeOf(fp.Fallible),
        TextJoin or TextConvert or TextSubstringRange or TextSubstringEdge
            or TextReplace or TextCase or TextTrim => TText,
        TextSplit             => new SeriesType(TText),
        NumberConvert or TextFind => new VoidableType(TNumber),
        TextLength            => TNumber,
        TextContains          => TFact,
        // A file read is fallible; its post-check VALUE type is the inner success type (the
        // raw `T or failure` is only seen by FallibleReturnType, for Try / but-on-failure / propagate).
        FileReadExpression fr => FileReadSuccessType(fr),
        PathCheckExpression   => TFact,
        // Stream reads (infallible): read a line → voidable text (void at EOF); read all → text;
        // read all lines → series of text.
        ReadExpression re     => re.Form switch
        {
            ReadForm.Line     => new VoidableType(TText),
            ReadForm.AllLines => new SeriesType(TText),
            _                 => TText,
        },
        // run / subprocess pipe are fallible; post-check VALUE type is the run-result record.
        RunExpression or PipeExpression => RunResultRecordType,
        ChannelCreation cc    => new ChannelType(cc.ElementType),
        DeliveryExpression    => new VoidableType(TNumber),   // channels are number-only this slice
        InterruptRequestedExpression => TFact,                // `an interrupt is requested` → fact
        // A named task's awaited result — inner success type (raw `T or failure` is seen only by
        // FallibleReturnType, for Try / but-on-failure / propagate), exactly like a fallible call.
        AwaitedResultExpression are => AwaitedResultInnerType(are),
        SortExpression sort   => TypeOf(sort.Series),           // sorted is element-type-preserving
        RandomNumber          => TNumber,
        RandomGuess           => TFact,
        RandomItem ri         => new VoidableType(((SeriesType)TypeOf(ri.Series)).ElementType),   // void on empty
        RandomlyShuffled rs   => TypeOf(rs.Series),             // element-type-preserving, like sorted
        _ => throw new CompilerException(
                 $"'{expr.GetType().Name}' expressions are not yet supported by the compiler.")
    };

    // A book member's declared type (for TypeOf on a `<book>'s <member> of (...)` cast). math totals
    // → number; transcendentals → voidable number; collections aggregates → voidable number (void on
    // empty), except `unique` which is element-type-preserving (the arg's own series type).
    private CufetType BookMemberReturnType(string bookName, string member, IReadOnlyList<IExpression> args)
    {
        string m = member.ToLowerInvariant();
        if (bookName == "math" && m is "floor" or "ceiling" or "round" or "absolute value") return TNumber;
        if (bookName == "math" && m is "square root" or "log" or "power") return new VoidableType(TNumber);
        if (bookName == "collections" && m is "minimum" or "maximum" or "average") return new VoidableType(TNumber);
        if (bookName == "collections" && m == "unique" && args.Count > 0) return TypeOf(args[0]);
        if (bookName == "collections" && m == "transpose") return MatrixType.Instance;
        return TNumber;   // conservative default (unresolved books surface at emit)
    }

    private static CufetType BookConstantType(string bookName, string member) => TNumber;   // math pi / e

    private CufetType MapLiteralType(MapLiteral ml) =>
        ml.KeyType != null && ml.ValueType != null
            ? new MapType(ml.KeyType, ml.ValueType)
            : new MapType(TypeOf(ml.Pairs[0].Key), TypeOf(ml.Pairs[0].Value));

    private CufetType MapValueType(IExpression mapExpr) =>
        TypeOf(mapExpr) is MapType mt ? mt.ValueType
            : throw new CompilerException("map operation on a non-map value.");

    // Minimal nominal ObjectType (fields looked up from _objectDefs by name when needed).
    private static CufetType ObjType(string name) =>
        new ObjectType(name, Array.Empty<CufetType>(), Array.Empty<(string, CufetType)>(),
                       Array.Empty<(string, FunctionType)>());

    private CufetType SeriesElementType(SeriesLiteral sl) =>
        sl.Annotation ?? (sl.Elements.Count > 0 ? TypeOf(sl.Elements[0]) : TNumber);

    private CufetType SeriesAccessType(SeriesAccess sa)
    {
        var tt = TypeOf(sa.Target);
        if (tt is SeriesType st) return st.ElementType;
        if (tt is RecordType rt) return rt.PositionalTypes[LiteralIndex(sa.Index) - 1];
        if (tt is ObjectType ot) return _objectDefs[ot.Name].PositionalTypes[ObjectPositionalIndex(ot.Name, sa.Index)];
        throw new CompilerException("positional access on this type is not yet supported by the compiler.");
    }

    private CufetType CastReturnType(CastExpression c)
    {
        // A fallible call's VALUE (after the call-site failure check) is the inner success T;
        // the raw `T or failure` type is only seen by but-on-failure / propagate / a Try.
        var rt = RawCastReturnType(c);
        return rt is FailureType ft ? ft.Inner : rt;
    }

    private CufetType RawCastReturnType(CastExpression c)
    {
        if (c.Function is VariableReference vr)
        {
            if (_funcReturnTypes.TryGetValue(vr.Name, out var rt)) return rt ?? TNumber;   // free function
            if (c.Args.Count > 0 && TypeOf(c.Args[0]) is ObjectType ot)                    // method dispatch
                return MethodReturnType(ot.Name, vr.Name);
        }
        if (c.Function is PossessiveAccess bpa && bpa.Target is VariableReference bref
            && _bookAliases.TryGetValue(bref.Name, out var bookName))                      // book member call
            return BookMemberReturnType(bookName, bpa.Member, c.Args);
        if (c.Function is PossessiveAccess pa && TypeOf(pa.Target) is ObjectType pot)
            return MethodReturnType(pot.Name, pa.Member);
        return TNumber;
    }

    // If `expr` is a fallible operation (a call to a fallible fn/method, or a fallible I/O op),
    // its `T or failure` return type; else null. Fallible I/O composes with Try / but-on-failure /
    // propagate through exactly the same machinery as a fallible call.
    private FailureType? FallibleReturnType(IExpression expr) => expr switch
    {
        CastExpression c when RawCastReturnType(c) is FailureType ft => ft,
        FileReadExpression fr => new FailureType(FileReadSuccessType(fr)),
        RunExpression or PipeExpression => new FailureType(RunResultRecordType),
        AwaitedResultExpression are when AwaitedRawResultType(are) is FailureType aft => aft,
        BinaryExpression b when IsMatrixOp(b) => new FailureType(MatrixType.Instance),
        _ => null,
    };

    // A named task's declared/inferred result type as tracked at its spawn (raw — keeps FailureType).
    private CufetType AwaitedRawResultType(AwaitedResultExpression are) =>
        are.Task is VariableReference vr && _taskInfos.TryGetValue(vr.Name, out var info)
            ? info.ResultType ?? TNumber
            : TNumber;

    // The awaited result's post-check VALUE type — inner success T when the task is fallible.
    private CufetType AwaitedResultInnerType(AwaitedResultExpression are) =>
        AwaitedRawResultType(are) is FailureType ft ? ft.Inner : AwaitedRawResultType(are);

    private CufetType FileReadSuccessType(FileReadExpression fr) =>
        fr.Form == FileReadForm.AllLines ? new SeriesType(TText) : TText;

    // The `run`/pipe success record — (errors: text, exit-code: number, output: text), named fields
    // alphabetical, matching the interpreter's RunResultType exactly. A launch failure is the `or
    // failure`; a command that runs and exits nonzero is a SUCCESS record with that exit-code.
    private static readonly RecordType RunResultRecordType = new RecordType(
        Array.Empty<CufetType>(),
        new (string, CufetType)[] { ("errors", TText), ("exit-code", TNumber), ("output", TText) });

    private CufetType MethodReturnType(string objName, string methodName)
    {
        var (owner, _) = ResolveMethodLevel(objName, methodName);
        return _objectDefs[owner].Methods.First(m => m.Name == methodName).ReturnType ?? TNumber;
    }

    // Finds which level of the embed chain owns a method, and the C access suffix (a chain of
    // .cv_<embed>) to reach the receiver object at that level.
    private (string ObjName, string Suffix) ResolveMethodLevel(string objName, string method)
    {
        var def = _objectDefs[objName];
        if (def.Methods.Any(m => m.Name == method)) return (objName, "");
        if (def.EmbeddedTypeName != null)
        {
            var (owner, suffix) = ResolveMethodLevel(def.EmbeddedTypeName, method);
            return (owner, $".{MangleName(def.EmbeddedTypeName)}{suffix}");
        }
        throw new CompilerException($"'{objName}' has no method '{method}'.");
    }

    private CufetType FieldType(CufetType t, string fieldName)
    {
        // the message of the failure → text; the category of the failure → voidable text.
        if (t is FailureMarkerType) return fieldName == "message" ? TText : new VoidableType(TText);
        if (t is MappingType mp) return fieldName == "key" ? mp.KeyType : mp.ValueType;   // the key/value of pair
        if (t is RecordType rt) return rt.NamedFields.First(f => f.Name == fieldName).Type;
        if (t is ObjectType ot) return ObjectMemberType(ot.Name, fieldName);
        throw new CompilerException($"field access on '{t.GetType().Name}' is not yet supported by the compiler.");
    }

    // Static type of an object member, walking the embed chain (getter → own field →
    // embed handle → promoted field).
    private CufetType ObjectMemberType(string objName, string member)
    {
        var def = _objectDefs[objName];
        if (GetterFor(objName, member) is { } g) return g.ReturnType;
        var nf = def.NamedFields.FirstOrDefault(f => f.FieldName == member);
        if (nf.FieldName == member) return nf.FieldType;
        if (def.EmbeddedTypeName == member) return ObjType(member);   // embed handle → embedded object
        if (def.EmbeddedTypeName != null) return ObjectMemberType(def.EmbeddedTypeName, member);
        throw new CompilerException($"'{objName}' has no member '{member}'.");
    }

    // 0-based positional index for an object, guarding the named-field case (which the
    // interpreter rejects too — a named-field object has no positional slots).
    private int ObjectPositionalIndex(string objName, IExpression? index)
    {
        int i = LiteralIndex(index);
        var pos = _objectDefs[objName].PositionalTypes;
        if (i < 1 || i > pos.Count)
            throw new CompilerException($"'{objName}' has no positional field {i} — access named-field objects by name (the <field> of ...).");
        return i - 1;
    }

    // A compile-time positional index from an ordinal literal (the first → 1, ...).
    private static int LiteralIndex(IExpression? index) =>
        index is NumberLiteral n ? (int)n.Value
            : throw new CompilerException("positional record access needs a constant index (the first/second/... of).");

    private static bool IsArithmeticOp(TokenType op) =>
        op is TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent;

    // Emits the C signature of a top-level function. Reference-type params/returns are
    // now supported: records/objects pass by value (value semantics fall out of C struct
    // copy), text as const char*, series as an arena pointer (its region is the caller's).
    private string EmitFunctionSignature(BindStatement bind)
    {
        if (bind.UntoType != null)
            throw new CompilerException($"Object methods declared with 'unto' are not yet supported by the compiler.");
        // Named constructors are ordinary functions here — bind.ReturnType is the object
        // type (or a FailureType for 'or failure', which EmitCType defers cleanly).
        var paramsStr = string.Join(", ", bind.Parameters.Select(p => $"{EmitCType(p.Type)} {MangleName(p.Name)}"));
        return $"{EmitCType(bind.ReturnType)} {MangleName(bind.Name)}({paramsStr})";
    }

    // Folds 'Bind <ret> to <name> unto <type> ...' methods into their target object's
    // method list — an unto method is identical to a nested one, only its declaration
    // site differs, so once merged the normal method emission + dispatch handle it.
    private void MergeUntoMethods(IEnumerable<IStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case BindStatement { UntoType: { } target } b:
                    _objectDefs[target] = UntoTargetDef(target, "methods") with { Methods = UntoTargetDef(target, "methods").Methods.Append(b).ToList() };
                    break;
                case GetterDeclaration { UntoType: { } target } g:
                    _objectDefs[target] = UntoTargetDef(target, "getters") with { Getters = UntoTargetDef(target, "getters").Getters.Append(g).ToList() };
                    break;
                case SetterDeclaration { UntoType: { } target } s:
                    _objectDefs[target] = UntoTargetDef(target, "setters") with { Setters = UntoTargetDef(target, "setters").Setters.Append(s).ToList() };
                    break;
                case PullRabbitStatement p: MergeUntoMethods(p.Body); break;
                case IfStatement iff:
                    foreach (var arm in iff.Arms) MergeUntoMethods(arm.Body);
                    if (iff.ElseBody != null) MergeUntoMethods(iff.ElseBody);
                    break;
                case WhileStatement w:       MergeUntoMethods(w.Body); break;
                case RepeatUntilStatement r: MergeUntoMethods(r.Body); break;
                case ForEachStatement fe:    MergeUntoMethods(fe.Body); break;
            }
        }
    }

    private ObjectDefinition UntoTargetDef(string target, string kind) =>
        _objectDefs.TryGetValue(target, out var def) ? def
            : throw new CompilerException($"'unto {target}': {kind} on '{target}' are not yet supported by the compiler (not a plain object type).");

    private void EmitBind(StringBuilder sb, BindStatement bind)
    {
        // Save and restore _varTypes so function-local names don't pollute
        // the outer scope's type map (and vice versa).
        var saved = new Dictionary<string, CufetType>(_varTypes);
        var savedRet = _currentReturnType;
        _varTypes.Clear();
        _currentReturnType = bind.ReturnType;
        foreach (var (pType, pName) in bind.Parameters)
            _varTypes[pName] = pType;

        sb.AppendLine($"{EmitFunctionSignature(bind)} {{");
        EmitBlock(sb, bind.Body, "    ");
        sb.AppendLine("}");
        sb.AppendLine();

        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _currentReturnType = savedRet;
    }

    // C function names: methods cm_, getters cg_, setters cst_ (cst_ avoids the cs_ series-temp prefix).
    private static string MethodCName(string objName, string methodName) =>
        "cm_" + objName.Replace('-', '_') + "_" + methodName.Replace('-', '_');
    private static string GetterCName(string objName, string name) =>
        "cg_" + objName.Replace('-', '_') + "_" + name.Replace('-', '_');
    private static string SetterCName(string objName, string name) =>
        "cst_" + objName.Replace('-', '_') + "_" + name.Replace('-', '_');

    private GetterDeclaration? GetterFor(string objName, string member) =>
        _objectDefs.TryGetValue(objName, out var d) ? d.Getters.FirstOrDefault(g => g.Name == member) : null;
    private SetterDeclaration? SetterFor(string objName, string member) =>
        _objectDefs.TryGetValue(objName, out var d) ? d.Setters.FirstOrDefault(s => s.Name == member) : null;

    private void EmitGetter(StringBuilder sb, ObjectDefinition def, GetterDeclaration g)
    {
        var saved = new Dictionary<string, CufetType>(_varTypes);
        var savedRecv = _methodReceiverType;
        var savedRet = _currentReturnType;
        _varTypes.Clear();
        _methodReceiverType = def.Name;
        _currentReturnType = g.ReturnType;
        _varTypes["one"] = ObjType(def.Name);
        sb.AppendLine($"{GetterSignature(def, g)} {{");
        EmitBlock(sb, g.Body, "    ");
        sb.AppendLine("}");
        sb.AppendLine();
        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _methodReceiverType = savedRecv;
        _currentReturnType = savedRet;
    }

    private void EmitSetter(StringBuilder sb, ObjectDefinition def, SetterDeclaration s)
    {
        var saved = new Dictionary<string, CufetType>(_varTypes);
        var savedRecv = _methodReceiverType;
        var savedSetter = _inSetterForField;
        _varTypes.Clear();
        _methodReceiverType = def.Name;
        _inSetterForField   = s.Name;                 // one's <name> becomes X → raw write
        _varTypes["one"] = ObjType(def.Name);
        _varTypes[s.ParamName] = s.ParamType;
        sb.AppendLine($"{SetterSignature(def, s)} {{");
        EmitBlock(sb, s.Body, "    ");
        sb.AppendLine("}");
        sb.AppendLine();
        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _methodReceiverType = savedRecv;
        _inSetterForField   = savedSetter;
    }

    private string GetterSignature(ObjectDefinition def, GetterDeclaration g) =>
        $"{EmitCType(g.ReturnType)} {GetterCName(def.Name, g.Name)}({ObjStructName(def.Name)}* cv_one)";
    private string SetterSignature(ObjectDefinition def, SetterDeclaration s) =>
        $"void {SetterCName(def.Name, s.Name)}({ObjStructName(def.Name)}* cv_one, {EmitCType(s.ParamType)} {MangleName(s.ParamName)})";

    // Object member READ: getter dispatch, own field, embed handle, or a promoted field
    // reached by walking the embed chain — all resolved statically.
    private string EmitMemberAccess(IExpression target, string member)
    {
        // `<book>'s <const>` — a book constant (math's pi / e), baked as a decimal literal.
        if (target is VariableReference bvr && _bookAliases.TryGetValue(bvr.Name, out var bookName))
            return EmitBookConstant(bookName, member);
        return TypeOf(target) switch
        {
            ObjectType ot     => EmitObjectMemberRead(EmitExpr(target), ot.Name, member),
        MappingType       => $"{EmitExpr(target)}_{member}",   // the key/value of pair → cv_pair_key/_value
            FailureMarkerType => member == "message" ? $"({EmitExpr(target)}).message" : EmitFailureCategory(EmitExpr(target)),
            _                 => $"({EmitExpr(target)}).{MangleName(member)}"   // record field
        };
    }

    // A book constant (math's pi / e) — baked as the exact decimal literal. Using (decimal)Math.PI in
    // the compiler (itself .NET) produces the bit-identical CufetDec the interpreter stores.
    private static string EmitBookConstant(string bookName, string member)
    {
        string m = member.ToLowerInvariant();
        if (bookName == "math" && m == "pi") return EmitNumberLiteral((decimal)Math.PI);
        if (bookName == "math" && m == "e")  return EmitNumberLiteral((decimal)Math.E);
        throw new CompilerException($"book '{bookName}' has no constant '{member}' supported by the compiler.");
    }

    // the category of the failure → voidable text (NULL category → void).
    private string EmitFailureCategory(string failExpr)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(TText));
        return $"(({failExpr}).category ? ({cvd}){{ .has = 1, .val = ({failExpr}).category }} : ({cvd}){{ .has = 0 }})";
    }

    private string EmitObjectMemberRead(string baseExpr, string objName, string member)
    {
        var def = _objectDefs[objName];
        if (GetterFor(objName, member) is not null)
            return $"{GetterCName(objName, member)}(&({baseExpr}))";
        if (def.NamedFields.Any(f => f.FieldName == member) || def.EmbeddedTypeName == member)
            return $"({baseExpr}).{MangleName(member)}";   // own field, or the embed handle
        if (def.EmbeddedTypeName != null)
            return EmitObjectMemberRead($"({baseExpr}).{MangleName(def.EmbeddedTypeName)}", def.EmbeddedTypeName, member);
        throw new CompilerException($"'{objName}' has no member '{member}'.");
    }

    // Object member WRITE: setter dispatch (unless inside that setter for the same field on
    // `one` — the bypass), own field, or a promoted field reached by walking the embed chain.
    private void EmitMemberSet(StringBuilder sb, string indent, IExpression target, string member, IExpression value)
    {
        string baseExpr = EmitExpr(target);
        string val      = EmitExpr(value);
        FlushPreEmits(sb, indent);
        string stmt = TypeOf(target) is ObjectType ot
            ? EmitObjectMemberSet(baseExpr, ot.Name, member, val, target is VariableReference { Name: "one" })
            : $"({baseExpr}).{MangleName(member)} = {val};";   // record field
        sb.AppendLine($"{indent}{stmt}");
    }

    private string EmitObjectMemberSet(string baseExpr, string objName, string member, string val, bool isReceiver)
    {
        var def = _objectDefs[objName];
        if (SetterFor(objName, member) is not null && !(isReceiver && _inSetterForField == member))
            return $"{SetterCName(objName, member)}(&({baseExpr}), {val});";
        if (def.NamedFields.Any(f => f.FieldName == member) || def.EmbeddedTypeName == member)
            return $"({baseExpr}).{MangleName(member)} = {val};";
        if (def.EmbeddedTypeName != null)
            return EmitObjectMemberSet($"({baseExpr}).{MangleName(def.EmbeddedTypeName)}", def.EmbeddedTypeName, member, val, false);
        throw new CompilerException($"'{objName}' has no member '{member}'.");
    }

    // A method's C signature: takes the receiver as a pointer (so mutations to `one`
    // are visible on the caller's object — value-struct-in-place), then its params.
    private string MethodSignature(ObjectDefinition def, BindStatement method)
    {
        var ps = new List<string> { $"{ObjStructName(def.Name)}* cv_one" };
        ps.AddRange(method.Parameters.Select(p => $"{EmitCType(p.Type)} {MangleName(p.Name)}"));
        return $"{EmitCType(method.ReturnType)} {MethodCName(def.Name, method.Name)}({string.Join(", ", ps)})";
    }

    private void EmitMethod(StringBuilder sb, ObjectDefinition def, BindStatement method)
    {
        var saved = new Dictionary<string, CufetType>(_varTypes);
        var savedRecv = _methodReceiverType;
        var savedRet = _currentReturnType;
        _varTypes.Clear();
        _methodReceiverType = def.Name;               // `one` → (*cv_one), resolves fields
        _currentReturnType = method.ReturnType;
        _varTypes["one"] = ObjType(def.Name);
        foreach (var (pType, pName) in method.Parameters)
            _varTypes[pName] = pType;

        sb.AppendLine($"{MethodSignature(def, method)} {{");
        EmitBlock(sb, method.Body, "    ");
        sb.AppendLine("}");
        sb.AppendLine();

        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _methodReceiverType = savedRecv;
        _currentReturnType = savedRet;
    }

    private string EmitExpr(IExpression expr) => expr switch
    {
        NumberLiteral n       => EmitNumberLiteral(n.Value),
        BooleanLiteral bl     => bl.Value ? "1" : "0",
        StringLiteral s       => EscapeStringLiteral(s.Value),   // text-as-stored-data: static C string
        UnaryExpression u     => EmitUnary(u),
        BinaryExpression b    => EmitBinary(b),
        // `the failure` (in a handler) → the caught CufetFailure; `one` (in a method) → the
        // deref'd receiver; a flow-narrowed voidable var reads as its inner value (`.val`).
        VariableReference v   => v.Name == "the failure" && _currentFailVar != null ? _currentFailVar
                               : v.Name == "one" && _methodReceiverType != null ? "(*cv_one)"
                               : v.Name == "input" ? "stdin"   // `the input` = the stdin stream
                               : _narrowedVars.ContainsKey(v.Name) ? $"({MangleName(v.Name)}).val"
                               : MangleName(v.Name),
        CastExpression cast   => EmitCastExpr(cast),
        SeriesLiteral sl      => EmitSeriesLiteral(sl),
        SeriesLength sl2      => $"cufet_dec_from_ll(({EmitExpr(sl2.Series)})->len)",
        SeriesAccess sa       => EmitSeriesAccess(sa),
        RecordLiteral rl      => EmitRecordLiteral(rl),
        RecordNamedAccess rna => EmitMemberAccess(rna.Record, rna.FieldName),
        ObjectLiteral ol      => EmitObjectLiteral(ol),
        PossessiveAccess pa   => EmitMemberAccess(pa.Target, pa.Member),
        ButVoidDefault bvd    => EmitButVoidDefault(bvd),
        MapLiteral ml         => EmitMapLiteral(ml),
        MapLookup mlk         => $"{MapName(mlk.Map)}_get({EmitExpr(mlk.Map)}, {EmitExpr(mlk.Key)})",
        MapHasKey mhk         => $"{MapName(mhk.Map)}_has({EmitExpr(mhk.Map)}, {EmitExpr(mhk.Key)})",
        MapHasEntry mhe       => $"{MapName(mhe.Map)}_has({EmitExpr(mhe.Map)}, {EmitExpr(mhe.Key)})",
        MapSize ms            => $"cufet_dec_from_ll(({EmitExpr(ms.Map)})->len)",
        FailureFallback ff    => EmitFailureFallback(ff),
        FailurePropagate fp   => EmitFailurePropagate(fp),
        TextJoin tj           => $"cufet_str_concat({EmitExpr(tj.Left)}, {EmitExpr(tj.Right)})",
        TextConvert tc        => EmitTextConvert(tc),
        NumberConvert nc      => EmitNumberConvert(nc),
        TextLength tl         => $"cufet_dec_from_ll((long long)strlen({EmitExpr(tl.Target)}))",
        TextContains tcn      => $"(strstr({EmitExpr(tcn.Text)}, {EmitExpr(tcn.Substring)}) != NULL)",
        TextFind tf           => EmitTextFind(tf),
        TextSubstringRange r  => $"cufet_str_range({EmitExpr(r.Text)}, cufet_to_int({EmitExpr(r.From)}), {(r.To != null ? $"cufet_to_int({EmitExpr(r.To)})" : "-1")})",
        TextSubstringEdge e   => $"cufet_str_edge({EmitExpr(e.Text)}, cufet_to_int({EmitExpr(e.Count)}), {(e.FromStart ? "1" : "0")})",
        TextReplace rp        => $"cufet_str_replace({EmitExpr(rp.Text)}, {EmitExpr(rp.Old)}, {EmitExpr(rp.New)})",
        TextCase tcs          => tcs.Uppercase ? $"cufet_str_upper({EmitExpr(tcs.Text)})" : $"cufet_str_lower({EmitExpr(tcs.Text)})",
        TextTrim tt           => $"cufet_str_trim({EmitExpr(tt.Text)})",
        TextSplit ts          => EmitTextSplit(ts),
        FileReadExpression fr => EmitFileRead(fr),
        RunExpression or PipeExpression => EmitFallibleCheckGoto(EmitRunRaw(expr), RegisterFailableStruct(new FailureType(RunResultRecordType))),
        ChannelCreation cc    => EmitChannelCreation(cc),
        DeliveryExpression de => EmitDelivery(de),
        AwaitedResultExpression are => EmitAwaitedResult(are),
        InterruptRequestedExpression => EmitInterruptRequested(),
        SortExpression sort   => EmitSort(sort),
        MatrixLiteral ml      => EmitMatrixLiteral(ml),
        MatrixSized ms        => EmitMatrixSized(ms),
        MatrixAccess ma       => EmitMatrixAccess(ma),
        MatrixRows mr         => $"cufet_dec_from_ll(({EmitExpr(mr.Target)})->rows)",
        MatrixColumns mc      => $"cufet_dec_from_ll(({EmitExpr(mc.Target)})->cols)",
        RandomNumber rn       => EmitRandomNumber(rn),
        RandomGuess           => EmitRandomGuess(),
        RandomItem ri         => EmitRandomItem(ri),
        RandomlyShuffled rs   => EmitRandomlyShuffled(rs),
        ReadExpression re     => EmitReadExpr(re),
        PathCheckExpression pc => pc.Kind switch
        {
            PathCheckKind.Exists      => $"cufet_path_exists({EmitExpr(pc.Path)})",
            PathCheckKind.IsDirectory => $"cufet_path_is_dir({EmitExpr(pc.Path)})",
            PathCheckKind.IsFile      => $"cufet_path_is_file({EmitExpr(pc.Path)})",
            _ => throw new CompilerException($"unknown path check {pc.Kind}"),
        },
        // A bare `void` only has meaning where a voidable is expected (return/becomes/args,
        // handled by EmitAsType) or as an `is void` operand (handled in EmitBinary).
        VoidLiteral           => throw new CompilerException("'void' is only valid where a voidable value is expected."),
        // A failure literal only has meaning where a T-or-failure is expected (return/coercion).
        FailureLiteral        => throw new CompilerException("'a failure' is only valid where a 'T or failure' is expected (e.g. a return)."),
        _ => throw new CompilerException(
                 $"'{expr.GetType().Name}' expressions are not yet supported by the compiler.")
    };

    // Coerces `expr` to `target`, performing the language's one implicit coercion: widening
    // a plain T (or a bare `void`) into a voidable tagged struct when the target is voidable.
    private string EmitAsType(IExpression expr, CufetType? target)
    {
        if (target is VoidableType vt)
        {
            string cvd = RegisterVoidableStruct(vt);
            if (expr is VoidLiteral)          return $"(({cvd}){{ .has = 0 }})";
            if (TypeOf(expr) is VoidableType) return EmitExpr(expr);                     // already voidable
            return $"(({cvd}){{ .has = 1, .val = {EmitExpr(expr)} }})";                  // widen T → voidable
        }
        if (target is FailureType ft)
        {
            string cfl = RegisterFailableStruct(ft);
            if (expr is FailureLiteral fl)   // a failure "msg" [of category "cat"]
            {
                string msg = EmitExpr(fl.Message);
                string cat = fl.Category != null ? EmitExpr(fl.Category) : "NULL";
                return $"(({cfl}){{ .is_failure = 1, .message = {msg}, .category = {cat} }})";
            }
            var et = TypeOf(expr);
            if (et is FailureType) return EmitExpr(expr);                                // already failable
            if (et is FailureMarkerType)                                                // re-propagate `the failure`
            {
                string f = EmitExpr(expr);
                return $"(({cfl}){{ .is_failure = 1, .message = ({f}).message, .category = ({f}).category }})";
            }
            return $"(({cfl}){{ .is_failure = 0, .val = {EmitExpr(expr)} }})";           // widen T → success
        }
        return EmitExpr(expr);
    }

    // `<voidable> but void is <default>` → the value if present, else the default. The
    // voidable is bound to a temp (single eval); the default is lazy (only in the else arm).
    private string EmitButVoidDefault(ButVoidDefault bvd)
    {
        var vt = (VoidableType)TypeOf(bvd.Voidable);
        string cvd = RegisterVoidableStruct(vt);
        string voidableExpr = EmitExpr(bvd.Voidable);
        string tmp = $"cf_bv{_freshId++}";
        _preEmits.Add($"{cvd} {tmp} = {voidableExpr};");
        return $"({tmp}.has ? {tmp}.val : {EmitExpr(bvd.Default)})";
    }

    // <fallible> but on failure <default> — the success value, else the default (lazy).
    private string EmitFailureFallback(FailureFallback ff)
    {
        var (cflName, rawExpr) = EmitFallibleRaw(ff.Fallible, TypeOf(ff));
        string tmp = $"cf_ff{_freshId++}";
        _preEmits.Add($"{cflName} {tmp} = {rawExpr};");
        return $"({tmp}.is_failure ? {EmitExpr(ff.Default)} : {tmp}.val)";
    }

    // Flattens `run A | run B | run C` into its stages (all must be `run` — the interpreter only
    // handles all-subprocess pipes in expression position; task pipes are the concurrency arc).
    private List<RunExpression> FlattenPipeStages(PipeExpression pipe)
    {
        var stages = new List<RunExpression>();
        void Walk(IExpression e)
        {
            if (e is PipeExpression p) { Walk(p.Left); Walk(p.Right); }
            else if (e is RunExpression r) stages.Add(r);
            else throw new CompilerException("only 'run … | run …' subprocess pipes are supported as a value (task pipes are deferred to the concurrency arc).");
        }
        Walk(pipe);
        return stages;
    }

    // Flattens a pipe into ALL its stage expressions (left-associative), without the all-`run`
    // restriction — used to tell a task pipe (function stages) from a subprocess pipe.
    private static List<IExpression> FlattenPipeAll(PipeExpression pipe)
    {
        var stages = new List<IExpression>();
        void Walk(IExpression e)
        {
            if (e is PipeExpression p) { Walk(p.Left); Walk(p.Right); }
            else stages.Add(e);
        }
        Walk(pipe);
        return stages;
    }

    // `for each <name> from the input: … Done.` — a pipe-stage consumer loop. Drains the stage's
    // (thread-local) input channel until it is closed-and-empty (recv → void), binding each value
    // to the iterator. Same shape as the delivery loop, over the implicit `cufet_pipe_in`. Numbers
    // only this slice. Stop → break, Skip → continue (the loop is a plain C `for(;;)`).
    private void EmitForEachFromInput(StringBuilder sb, ForEachFromInputStatement fi, string indent)
    {
        _usesConcurrency = true;
        string raw   = fi.IteratorName;
        string it    = MangleName(raw);
        string inner = indent + "    ";
        int id = _freshId++;
        sb.AppendLine($"{indent}for (;;) {{");
        sb.AppendLine($"{inner}CufetDec cf_pv{id}; int cf_ph{id} = cufet_chan_recv(cufet_pipe_in, &cf_pv{id});");
        sb.AppendLine($"{inner}if (cf_ph{id} < 0) {{ cufet_checkpoint(); break; }}");   // interrupted while blocked
        sb.AppendLine($"{inner}if (cf_ph{id} == 0) break;");                            // stream closed → done
        sb.AppendLine($"{inner}CufetDec {it} = cf_pv{id};");
        var saved = _varTypes.TryGetValue(raw, out var st) ? st : null;
        _varTypes[raw] = TNumber;
        _loopFileDepths.Add(_openFiles.Count);   // so Stop/Skip close files opened in the loop body
        EmitBlock(sb, fi.Body, inner);
        _loopFileDepths.RemoveAt(_loopFileDepths.Count - 1);
        if (saved != null) _varTypes[raw] = saved; else _varTypes.Remove(raw);
        sb.AppendLine($"{indent}}}");
    }

    // A bare `s0 | s1 | … | sN.` task pipe (function stages). Each stage runs as its own thread,
    // adjacent stages share a channel (stage i's output = stage i+1's input); a stage closes its
    // output on return, so completion cascades down the pipe. Self-contained + structured: the pipe
    // spawns every stage, JOINS them all, then frees the channels — no enclosing rabbit needed (the
    // interpreter's task pipes are top-level too). Values stream FIFO, so a linear pipe's observable
    // output is deterministic and matches the interpreter's buffered-sequential order.
    private void EmitTaskPipe(StringBuilder sb, List<IExpression> stages, string indent)
    {
        _usesConcurrency = true;
        // Resolve each stage to a compiled function symbol. Direct function names only this slice —
        // function-valued variables / lambdas are the FunctionType-escape (closures) gap.
        var fns = new List<string>();
        foreach (var st in stages)
        {
            if (st is VariableReference vr && _funcReturnTypes.ContainsKey(vr.Name))
                fns.Add(MangleName(vr.Name));
            else
                throw new CompilerException("a task-pipe stage must be a named function this slice (function-valued variables / lambdas are the closures gap).");
        }

        int n = stages.Count;
        int id = _freshId++;
        string inner = indent + "    ";
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}cufet_chan* cf_pch{id}[{n - 1}];");
        sb.AppendLine($"{inner}for (int cf_pi = 0; cf_pi < {n - 1}; cf_pi++) cf_pch{id}[cf_pi] = cufet_chan_new();");
        sb.AppendLine($"{inner}pthread_t cf_pth{id}[{n}];");
        for (int i = 0; i < n; i++)
        {
            string inCh  = i == 0     ? "NULL" : $"cf_pch{id}[{i - 1}]";
            string outCh = i == n - 1 ? "NULL" : $"cf_pch{id}[{i}]";
            sb.AppendLine($"{inner}{{ cufet_pipe_arg* cf_pa = (cufet_pipe_arg*)malloc(sizeof(cufet_pipe_arg));");
            sb.AppendLine($"{inner}  cf_pa->in = {inCh}; cf_pa->out = {outCh}; cf_pa->fn = (void (*)(void)){fns[i]};");
            sb.AppendLine($"{inner}  pthread_create(&cf_pth{id}[{i}], NULL, cufet_pipe_stage, cf_pa); }}");
        }
        sb.AppendLine($"{inner}for (int cf_pj = 0; cf_pj < {n}; cf_pj++) pthread_join(cf_pth{id}[cf_pj], NULL);");
        sb.AppendLine($"{inner}for (int cf_pf = 0; cf_pf < {n - 1}; cf_pf++) cufet_chan_free(cf_pch{id}[cf_pf]);");
        sb.AppendLine($"{indent}}}");
    }

    // Builds the raw fallible run result (a `cfl` of the run-result record). A single `run` runs the
    // command; a pipe runs the stages buffered-sequentially, chaining stdout → next stdin (matching
    // the interpreter): aggregated stderr, rightmost-nonzero exit (pipefail), final stdout. A LAUNCH
    // failure of any stage becomes the `or failure`; a ran-but-nonzero command is a success record.
    private string EmitRunRaw(IExpression expr)
    {
        _usesProcess = true;
        string cr   = RegisterRecordStruct(RunResultRecordType);
        string cfl  = RegisterFailableStruct(new FailureType(RunResultRecordType));
        string fErr = MangleName("errors"), fExit = MangleName("exit-code"), fOut = MangleName("output");
        int id = _freshId++;
        string raw = $"cf_run{id}";
        var stages = expr is PipeExpression pipe ? FlattenPipeStages(pipe) : new List<RunExpression> { (RunExpression)expr };

        // Emit each stage's program + argv temps as separate preemit lines first (their operands
        // may add their own preemits), then the one run/chain block referencing those temps.
        var progVars = new List<string>();
        for (int s = 0; s < stages.Count; s++)
        {
            string pg = $"cf_pg{id}_{s}";
            _preEmits.Add($"const char* {pg} = {EmitExpr(stages[s].Program)};");
            var elems = new List<string> { $"(char*){pg}" };
            foreach (var arg in stages[s].Args) elems.Add($"(char*){EmitExpr(arg)}");
            elems.Add("(char*)0");
            _preEmits.Add($"char* cf_av{id}_{s}[] = {{ {string.Join(", ", elems)} }};");
            progVars.Add(pg);
        }

        var b = new StringBuilder();
        b.Append($"{cfl} {raw} = {{0}}; {{ const char* cf_so{id}; const char* cf_se{id}; int cf_ex{id}; CufetFailure cf_e{id}; ");
        if (stages.Count == 1)
        {
            b.Append($"if (cufet_run_capture({progVars[0]}, cf_av{id}_0, NULL, &cf_so{id}, &cf_se{id}, &cf_ex{id}, &cf_e{id})) {{ ");
            b.Append($"{raw}.is_failure = 0; {raw}.val = ({cr}){{ .{fErr} = cf_se{id}, .{fExit} = cufet_dec_from_ll(cf_ex{id}), .{fOut} = cf_so{id} }}; ");
            b.Append($"}} else {{ {raw}.is_failure = 1; {raw}.message = cf_e{id}.message; {raw}.category = cf_e{id}.category; }} ");
        }
        else
        {
            b.Append($"const char* cf_cur{id} = NULL; const char* cf_eagg{id} = \"\"; int cf_code{id} = 0; int cf_ok{id} = 1; ");
            for (int s = 0; s < stages.Count; s++)
            {
                b.Append($"if (cf_ok{id}) {{ if (cufet_run_capture({progVars[s]}, cf_av{id}_{s}, cf_cur{id}, &cf_so{id}, &cf_se{id}, &cf_ex{id}, &cf_e{id})) {{ ");
                b.Append($"cf_eagg{id} = cufet_str_concat(cf_eagg{id}, cf_se{id}); if (cf_ex{id} != 0) cf_code{id} = cf_ex{id}; cf_cur{id} = cf_so{id}; ");
                b.Append($"}} else {{ {raw}.is_failure = 1; {raw}.message = cf_e{id}.message; {raw}.category = cf_e{id}.category; cf_ok{id} = 0; }} }} ");
            }
            b.Append($"if (cf_ok{id}) {{ {raw}.is_failure = 0; {raw}.val = ({cr}){{ .{fErr} = cf_eagg{id}, .{fExit} = cufet_dec_from_ll(cf_code{id}), .{fOut} = cf_cur{id} ? cf_cur{id} : \"\" }}; }} ");
        }
        b.Append("}");
        _preEmits.Add(b.ToString());
        return raw;
    }

    // ── Concurrency (CONC.A+B) ────────────────────────────────────────────────

    private bool ProgramUsesConcurrency(IEnumerable<IStatement> stmts)
    {
        foreach (var s in stmts)
            switch (s)
            {
                case LaunchTaskStatement or SendStatement or CloseStatement: return true;
                // Task pipes + their stage constructs need the pipe/channel substrate emitted.
                case OutputStatement or ForEachFromInputStatement: return true;
                case PipeExpression pe when !FlattenPipeAll(pe).TrueForAll(x => x is RunExpression): return true;
                case PullRabbitStatement p when ProgramUsesConcurrency(p.Body): return true;
                case IfStatement iff when iff.Arms.Any(a => ProgramUsesConcurrency(a.Body)) || (iff.ElseBody != null && ProgramUsesConcurrency(iff.ElseBody)): return true;
                case WhileStatement w when ProgramUsesConcurrency(w.Body): return true;
                case RepeatUntilStatement r when ProgramUsesConcurrency(r.Body): return true;
                case ForEachStatement fe when ProgramUsesConcurrency(fe.Body): return true;
                case TryStatement t when ProgramUsesConcurrency(t.Body) || (t.FailureHandler != null && ProgramUsesConcurrency(t.FailureHandler)): return true;
                case WithOpenStatement wo when ProgramUsesConcurrency(wo.Body): return true;
                case BindStatement b when ProgramUsesConcurrency(b.Body): return true;
                case DefineStatement d when ExprUsesChannel(d.Value): return true;
                case BecomesStatement bc when ExprUsesChannel(bc.Value): return true;
                case StateStatement st when ExprUsesChannel(st.Value): return true;
            }
        return false;
    }

    // Whether the program uses interrupt constructs (CONC.E) — so main installs the SIGINT handler +
    // landing pad and the substrate is emitted. Discovered up front (like concurrency) because main's
    // top is emitted before its body is walked. Concurrency programs also get the substrate (their
    // blocked channel-waits are made interruptible), gated separately at emission.
    private bool ProgramUsesSignals(IEnumerable<IStatement> stmts)
    {
        foreach (var s in stmts)
            switch (s)
            {
                case YieldStatement or AcknowledgeInterruptStatement: return true;
                case DefineStatement d when ExprUsesInterrupt(d.Value): return true;
                case BecomesStatement bc when ExprUsesInterrupt(bc.Value): return true;
                case StateStatement st when ExprUsesInterrupt(st.Value): return true;
                case ReturnStatement r when r.Value != null && ExprUsesInterrupt(r.Value): return true;
                case IfStatement iff when iff.Arms.Any(a => ExprUsesInterrupt(a.Condition) || ProgramUsesSignals(a.Body)) || (iff.ElseBody != null && ProgramUsesSignals(iff.ElseBody)): return true;
                case WhileStatement w when ExprUsesInterrupt(w.Condition) || ProgramUsesSignals(w.Body): return true;
                case RepeatUntilStatement r when ExprUsesInterrupt(r.Condition) || ProgramUsesSignals(r.Body): return true;
                case ForEachStatement fe when ProgramUsesSignals(fe.Body): return true;
                case ForEachFromInputStatement fi when ProgramUsesSignals(fi.Body): return true;
                case PullRabbitStatement p when ProgramUsesSignals(p.Body): return true;
                case TryStatement t when ProgramUsesSignals(t.Body) || (t.FailureHandler != null && ProgramUsesSignals(t.FailureHandler)): return true;
                case WithOpenStatement wo when ProgramUsesSignals(wo.Body): return true;
                case BindStatement b when ProgramUsesSignals(b.Body): return true;
                case LaunchTaskStatement lt when ProgramUsesSignals(lt.Body): return true;
            }
        return false;
    }

    private static bool ExprUsesInterrupt(IExpression? e) => e switch
    {
        InterruptRequestedExpression => true,
        BinaryExpression b => ExprUsesInterrupt(b.Left) || ExprUsesInterrupt(b.Right),
        UnaryExpression u  => ExprUsesInterrupt(u.Operand),
        ButVoidDefault bvd => ExprUsesInterrupt(bvd.Voidable) || ExprUsesInterrupt(bvd.Default),
        _ => false,
    };

    private static bool ExprUsesChannel(IExpression e) => e switch
    {
        ChannelCreation or DeliveryExpression => true,
        ButVoidDefault bvd => ExprUsesChannel(bvd.Voidable) || ExprUsesChannel(bvd.Default),
        BinaryExpression b => ExprUsesChannel(b.Left) || ExprUsesChannel(b.Right),
        UnaryExpression u  => ExprUsesChannel(u.Operand),
        _ => false,
    };

    // `a channel of T` — number-only this slice. Allocated with the enclosing rabbit tracking it so
    // it's freed at Done. (after tasks join). Returns a temp so the tracking side-effect precedes use.
    private string EmitChannelCreation(ChannelCreation cc)
    {
        if (cc.ElementType is not NumberType)
            throw new CompilerException("channels are number-only in this concurrency slice (text/reference elements are deferred).");
        _usesConcurrency = true;
        if (_rabbitCtx.Count == 0)
            throw new CompilerException("a channel must be created inside a rabbit (Pull a rabbit) in this slice.");
        string ctx = _rabbitCtx[^1];
        int id = _freshId++;
        string tmp = $"cf_ch{id}";
        _preEmits.Add($"cufet_chan* {tmp} = cufet_chan_new(); cf_chan{ctx}[cf_nchan{ctx}++] = {tmp};");
        return tmp;
    }

    // `the delivery from ch` → voidable number: blocking receive; void when empty-and-closed.
    private string EmitDelivery(DeliveryExpression de)
    {
        _usesConcurrency = true;
        string cvd = RegisterVoidableStruct(new VoidableType(TNumber));
        string ch = EmitExpr(de.Channel);
        int id = _freshId++;
        // recv → 1 (value), 0 (empty+closed → void), -1 (interrupted while blocked). On -1 run the
        // checkpoint: if this thread has a landing pad it unwinds; otherwise the interrupt reads as void.
        _preEmits.Add($"CufetDec cf_dv{id}; int cf_dh{id} = cufet_chan_recv({ch}, &cf_dv{id}); if (cf_dh{id} < 0) {{ cufet_checkpoint(); cf_dh{id} = 0; }}");
        return $"(cf_dh{id} ? ({cvd}){{ .has = 1, .val = cf_dv{id} }} : ({cvd}){{ .has = 0 }})";
    }

    // `an interrupt is requested` → the current interrupt flag as a fact (0/1).
    private string EmitInterruptRequested()
    {
        _usesSignals = true;
        return "(cufet_interrupted ? 1 : 0)";
    }

    // `Have rabbit start a task [as <name>]: … Done.` → a pthread. Captured enclosing locals are
    // snapshot into a heap arg struct at spawn (deep-copy-at-spawn — value types copied, channels
    // shared) so a parent mutation after spawn can't race the task's read. The thread runs in its
    // own (thread-local) arena. A NAMED task (CONC.C) additionally returns a heap-bridged result via
    // the pthread void* return; `the awaited result of <name>` joins it, copies the result into the
    // awaiter, and frees the bridge. The result type is a self-contained value struct this slice
    // (number/fact + voidable/failable), so the heap copy is a deep copy with no arena pointers.
    private void EmitLaunchTask(StringBuilder sb, LaunchTaskStatement lts, string indent)
    {
        if (_rabbitCtx.Count == 0)
            throw new CompilerException("'Have rabbit start a task' requires an enclosing rabbit.");
        _usesConcurrency = true;
        string ctx = _rabbitCtx[^1];
        int tid = _taskCounter++;

        // Result type (named tasks only) — inferred from the body's returns, mirroring the checker.
        CufetType? resultType = lts.Name != null ? InferTaskResultType(lts.Body) : null;
        string? resultCType = null;
        if (lts.Name != null && resultType != null)
        {
            // The heap-bridged result must be a self-contained value struct (no arena pointers) so a
            // shallow struct copy across the thread boundary IS a deep copy. Text/series/reference
            // results are deferred (recursive heap deep-copy — the channel-of-T follow-on). A fallible
            // result's failure message must be a static string literal (arena-templated I/O-failure
            // messages would dangle past the task's arena_pop) — I/O-inside-tasks is out of scope here.
            var core = resultType;
            if (core is FailureType ft) core = ft.Inner;
            if (core is VoidableType vt) core = vt.Inner;
            if (core is not (NumberType or FactType))
                throw new CompilerException(
                    "task results are number/fact this slice (optionally voidable or fallible); " +
                    "text/series/reference results are deferred to the channel-of-T follow-on (recursive heap deep-copy).");
            resultCType = EmitCType(resultType);
            _taskInfos[lts.Name] = (ctx, resultCType, resultType);
            _varTypes[lts.Name] = new TaskHandleType(resultType);
        }

        // Captured free variables = referenced enclosing locals not defined inside the task body.
        var refs = new HashSet<string>(); var defs = new HashSet<string>();
        foreach (var s in lts.Body) CollectRefsDefs(s, refs, defs);
        var caps = refs.Where(r => !defs.Contains(r) && _varTypes.ContainsKey(r)).OrderBy(x => x).ToList();
        foreach (var c in caps)
            if (_varTypes[c] is not (NumberType or FactType or ChannelType))
                throw new CompilerException($"task captures '{c}' of an unsupported type — this slice captures number/fact (snapshot) and channels (shared).");

        // Arg struct + thread function (accumulated; emitted before the bodies).
        _taskFns.AppendLine($"struct cufet_targ{tid} {{ {string.Join(" ", caps.Select(c => $"{EmitCType(_varTypes[c])} {MangleName(c)};"))} }};");
        _taskFns.AppendLine($"static void* cufet_task{tid}(void* argp) {{");
        _taskFns.AppendLine($"    struct cufet_targ{tid}* cf_a = (struct cufet_targ{tid}*)argp;");
        foreach (var c in caps) _taskFns.AppendLine($"    {EmitCType(_varTypes[c])} {MangleName(c)} = cf_a->{MangleName(c)}; (void){MangleName(c)};");
        _taskFns.AppendLine($"    cufet_arena_push();");
        var savedRet     = _currentReturnType;
        var savedTaskRet = _currentTaskReturn;
        var savedInTask  = _inTaskBody;
        _currentReturnType = resultType;
        _currentTaskReturn = (resultType, resultCType);
        _inTaskBody        = true;
        EmitBlock(_taskFns, lts.Body, "    ");
        _currentReturnType = savedRet;
        _currentTaskReturn = savedTaskRet;
        _inTaskBody        = savedInTask;
        // Fall-through epilogue (reached by fire-and-forget/void tasks; a value-returning task is
        // required to return on every path — CheckLaunchTask — so this is dead code there but safe).
        _taskFns.AppendLine($"    cufet_arena_pop();");
        _taskFns.AppendLine($"    free(cf_a);");
        _taskFns.AppendLine($"    return NULL;");
        _taskFns.AppendLine($"}}");

        // A named task records its array slot + a stored-result var (set once, at its await site) so
        // `the awaited result of <name>` can join THIS task and cache the value for re-awaits.
        if (lts.Name != null && resultCType != null)
        {
            string sfx = TaskSuffix(lts.Name);
            sb.AppendLine($"{indent}int cf_slot_{sfx} = 0; (void)cf_slot_{sfx};");
            sb.AppendLine($"{indent}{resultCType} cf_tres_{sfx} = {{0}}; (void)cf_tres_{sfx};");
            sb.AppendLine($"{indent}cf_slot_{sfx} = cf_nthr{ctx};");
        }

        // Spawn: snapshot captures into the heap arg (value types copied, channels shared) + create.
        sb.AppendLine($"{indent}{{ struct cufet_targ{tid}* cf_a = (struct cufet_targ{tid}*)malloc(sizeof(struct cufet_targ{tid}));");
        foreach (var c in caps) sb.AppendLine($"{indent}  cf_a->{MangleName(c)} = {MangleName(c)};");
        sb.AppendLine($"{indent}  pthread_create(&cf_thr{ctx}[cf_nthr{ctx}++], NULL, cufet_task{tid}, cf_a); }}");
    }

    // A named task's C-identifier suffix (Cufet ids have no '_'; '-'→'_' keeps it valid).
    private static string TaskSuffix(string name) => name.Replace('-', '_');

    // Infers a named task's result type from its returns — mirrors the checker's inference so the
    // heap-bridge C type matches. Scans nested control flow (but not nested tasks). A `return void`
    // makes the result voidable; a `return a failure …` makes it fallible; both compose.
    private CufetType? InferTaskResultType(IReadOnlyList<IStatement> body)
    {
        bool hasFailure = false, hasVoid = false;
        CufetType? valueType = null;
        void Walk(IReadOnlyList<IStatement> stmts)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case ReturnStatement { Value: null }: break;               // bare void early-exit
                    case ReturnStatement { Value: FailureLiteral }: hasFailure = true; break;
                    case ReturnStatement { Value: VoidLiteral }: hasVoid = true; break;
                    case ReturnStatement r: valueType ??= TypeOf(r.Value!); break;
                    case IfStatement iff:
                        foreach (var a in iff.Arms) Walk(a.Body);
                        if (iff.ElseBody != null) Walk(iff.ElseBody);
                        break;
                    case WhileStatement w: Walk(w.Body); break;
                    case RepeatUntilStatement ru: Walk(ru.Body); break;
                    case ForEachStatement fe: Walk(fe.Body); break;
                    case PullRabbitStatement pr: Walk(pr.Body); break;
                    // Nested LaunchTaskStatement bodies own their own returns — do not descend.
                }
        }
        Walk(body);
        if (valueType == null && !hasFailure && !hasVoid) return null;   // fire-and-forget (void)
        CufetType t = valueType ?? TNumber;
        if (hasVoid) t = new VoidableType(t);
        if (hasFailure) t = new FailureType(t);
        return t;
    }

    // `the awaited result of <name>` — join the named task once (guarded so a re-await is a cheap
    // read of the cached result), copy the heap-bridged result into the awaiter + free the bridge,
    // and mark the slot joined so the rabbit's Done. teardown won't re-join it. The stored value is
    // then yielded; if the task is fallible it flows through the standard fallible machinery so
    // Try / but-on-failure / propagate compose exactly as for a fallible call.
    private string EmitAwaitedResult(AwaitedResultExpression are)
    {
        string raw = EmitAwaitedRaw(are, out var info);
        if (info.ResultType is FailureType ft)
            return EmitFallibleCheckGoto(raw, RegisterFailableStruct(ft));
        return raw;
    }

    // Emits the guarded join + result-cache preemit and returns the stored-result C var (a cfl/cvd/
    // scalar). Shared by EmitAwaitedResult (bare / in-Try) and EmitFallibleRaw (but-on-failure /
    // propagate), mirroring how file-read fallibility is factored.
    private string EmitAwaitedRaw(AwaitedResultExpression are, out (string Ctx, string ResultCType, CufetType? ResultType) info)
    {
        if (_inTaskBody)
            throw new CompilerException("awaiting a task's result inside another task is deferred (this slice awaits from the rabbit body).");
        _usesConcurrency = true;
        if (are.Task is not VariableReference vr || !_taskInfos.TryGetValue(vr.Name, out info))
            throw new CompilerException("'the awaited result of' requires a named task declared with 'Have rabbit start a task as <name>:'.");
        string sfx = TaskSuffix(vr.Name);
        string ctx = info.Ctx;
        _preEmits.Add(
            $"if (!cf_jflag{ctx}[cf_slot_{sfx}]) {{ void* cf_ar = NULL; " +
            $"pthread_join(cf_thr{ctx}[cf_slot_{sfx}], &cf_ar); cf_jflag{ctx}[cf_slot_{sfx}] = 1; " +
            $"cf_tres_{sfx} = *({info.ResultCType}*)cf_ar; free(cf_ar); }}");
        return $"cf_tres_{sfx}";
    }

    // Collects referenced variable names (refs) and locally-defined/iterated names (defs) in a
    // statement subtree — for task-capture analysis. Conservative: unknown forms contribute nothing.
    private void CollectRefsDefs(IStatement s, HashSet<string> refs, HashSet<string> defs)
    {
        void E(IExpression? e) { if (e != null) CollectExprRefs(e, refs); }
        switch (s)
        {
            case DefineStatement d: defs.Add(d.Name); E(d.Value); break;
            case BecomesStatement b: refs.Add(b.Name); E(b.Value); break;
            case StateStatement st: E(st.Value); break;
            case ReturnStatement r: E(r.Value); break;
            case SendStatement sd: E(sd.Value); E(sd.Channel); break;
            case CloseStatement c: E(c.Channel); break;
            case SeriesAddStatement sa: E(sa.Value); E(sa.Series); E(sa.AfterIndex); break;
            case IfStatement iff:
                foreach (var a in iff.Arms) { E(a.Condition); foreach (var b2 in a.Body) CollectRefsDefs(b2, refs, defs); }
                if (iff.ElseBody != null) foreach (var b2 in iff.ElseBody) CollectRefsDefs(b2, refs, defs);
                break;
            case WhileStatement w: E(w.Condition); foreach (var b2 in w.Body) CollectRefsDefs(b2, refs, defs); break;
            case RepeatUntilStatement ru: E(ru.Condition); foreach (var b2 in ru.Body) CollectRefsDefs(b2, refs, defs); break;
            case ForEachStatement fe: E(fe.Series); if (fe.IteratorName != null) defs.Add(fe.IteratorName); foreach (var b2 in fe.Body) CollectRefsDefs(b2, refs, defs); break;
            case LaunchTaskStatement lt: foreach (var b2 in lt.Body) CollectRefsDefs(b2, refs, defs); break;
        }
    }

    private void CollectExprRefs(IExpression e, HashSet<string> refs)
    {
        switch (e)
        {
            case VariableReference v: refs.Add(v.Name); break;
            case BinaryExpression b: CollectExprRefs(b.Left, refs); CollectExprRefs(b.Right, refs); break;
            case UnaryExpression u: CollectExprRefs(u.Operand, refs); break;
            case ButVoidDefault bvd: CollectExprRefs(bvd.Voidable, refs); CollectExprRefs(bvd.Default, refs); break;
            case DeliveryExpression de: CollectExprRefs(de.Channel, refs); break;
            case CastExpression c: foreach (var a in c.Args) CollectExprRefs(a, refs); break;
            case SeriesAccess sa: CollectExprRefs(sa.Target, refs); if (sa.Index != null) CollectExprRefs(sa.Index, refs); break;
        }
    }

    // <fallible> or pass the failure off — on failure, return it from the enclosing (fallible)
    // function immediately; on success, the plain value.
    private string EmitFailurePropagate(FailurePropagate fp)
    {
        var (cflName, rawExpr) = EmitFallibleRaw(fp.Fallible, TypeOf(fp));
        string tmp = $"cf_fp{_freshId++}";
        _preEmits.Add($"{cflName} {tmp} = {rawExpr};");
        string enclosing = _currentReturnType is FailureType ft
            ? RegisterFailableStruct(ft)
            : throw new CompilerException("'or pass the failure off' requires the enclosing function to return 'T or failure'.");
        _preEmits.Add($"if ({tmp}.is_failure) {{ {FileCleanupStmts(0)}return (({enclosing}){{ .is_failure = 1, .message = {tmp}.message, .category = {tmp}.category }}); }}");
        return $"{tmp}.val";
    }

    // converted to text — number formats to a fresh arena string; fact → static "true"/"false";
    // text is a no-op. (The type checker restricts the operand to number/fact/text.)
    private string EmitTextConvert(TextConvert tc) => TypeOf(tc.Value) switch
    {
        NumberType => $"cufet_text_from_dec({EmitExpr(tc.Value)})",
        FactType   => $"({EmitExpr(tc.Value)} ? \"true\" : \"false\")",
        TextType   => EmitExpr(tc.Value),
        var t => throw new CompilerException($"'converted to text' of a '{t.GetType().Name}' is not yet supported by the compiler.")
    };

    // converted to number → voidable number (void when unparseable). Parses into a temp.
    private string EmitNumberConvert(NumberConvert nc)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(TNumber));
        string s = EmitExpr(nc.Value);
        int id = _freshId++;
        _preEmits.Add($"CufetDec cf_pn{id}; int cf_pnok{id} = cufet_parse_number({s}, &cf_pn{id});");
        return $"(cf_pnok{id} ? ({cvd}){{ .has = 1, .val = cf_pn{id} }} : ({cvd}){{ .has = 0 }})";
    }

    // the position of <sub> in <text> → voidable number (1-based; void when not found).
    private string EmitTextFind(TextFind tf)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(TNumber));
        int id = _freshId++;
        _preEmits.Add($"int cf_fd{id} = cufet_str_find({EmitExpr(tf.Text)}, {EmitExpr(tf.Substring)});");
        return $"(cf_fd{id} ? ({cvd}){{ .has = 1, .val = cufet_dec_from_ll(cf_fd{id}) }} : ({cvd}){{ .has = 0 }})";
    }

    // `<text> split by <delim>` → a series of text (arena series of arena substrings). Matches
    // the interpreter's C# string.Split(string): N delimiter hits → N+1 parts, empties kept,
    // trailing/leading delimiter → empty parts, delimiter-not-found → single whole-string element.
    private string EmitTextSplit(TextSplit ts)
    {
        string name = RegisterSeriesStruct(new SeriesType(TText));
        string textExpr  = EmitExpr(ts.Text);
        string delimExpr = EmitExpr(ts.Delimiter);
        int id = _freshId++;
        string tmp = $"cs_{id}", parts = $"cf_sp{id}", n = $"cf_spn{id}", j = $"cf_spj{id}";
        _preEmits.Add($"{name}* {tmp} = {name}_new();");
        _preEmits.Add($"{{ const char** {parts}; int {n} = cufet_str_split({textExpr}, {delimExpr}, &{parts}); for (int {j} = 0; {j} < {n}; {j}++) {name}_append({tmp}, {parts}[{j}]); }}");
        return tmp;
    }

    // A file read is a fallible expression: build the raw `cfl` (read into arena, or a failure
    // from errno), then route through the shared fallible check-goto (auto-unwrap in a Try, or
    // fed to but-on-failure / propagate). Mirrors EmitCastExpr for a fallible call.
    private string EmitFileRead(FileReadExpression fr) =>
        EmitFallibleCheckGoto(EmitFileReadRaw(fr), RegisterFailableStruct((FailureType)FallibleReturnType(fr)!));

    // Preemits the read into a `cfl` temp (whole-file text, or a series-of-text of lines) and
    // returns the temp name (the raw fallible value, before the failure check).
    private string EmitFileReadRaw(FileReadExpression fr)
    {
        string cfl = RegisterFailableStruct(new FailureType(FileReadSuccessType(fr)));
        string pathExpr = EmitExpr(fr.Path);
        int id = _freshId++;
        string raw = $"cf_fr{id}", e = $"cf_fre{id}";
        if (fr.Form == FileReadForm.All)
        {
            _preEmits.Add(
                $"{cfl} {raw}; {{ const char* v; CufetFailure {e}; " +
                $"if (cufet_file_read_all({pathExpr}, &v, &{e})) {{ {raw}.is_failure = 0; {raw}.val = v; }} " +
                $"else {{ {raw}.is_failure = 1; {raw}.message = {e}.message; {raw}.category = {e}.category; }} }}");
        }
        else // AllLines → series of text (build the cser inline, like split)
        {
            string ser = RegisterSeriesStruct(new SeriesType(TText));
            string parts = $"cf_lp{id}", n = $"cf_ln{id}", j = $"cf_lj{id}", sv = $"cf_ls{id}";
            _preEmits.Add(
                $"{cfl} {raw}; {{ const char** {parts}; int {n}; CufetFailure {e}; " +
                $"if (cufet_file_read_lines({pathExpr}, &{parts}, &{n}, &{e})) {{ " +
                $"{ser}* {sv} = {ser}_new(); for (int {j} = 0; {j} < {n}; {j}++) {ser}_append({sv}, {parts}[{j}]); " +
                $"{raw}.is_failure = 0; {raw}.val = {sv}; }} " +
                $"else {{ {raw}.is_failure = 1; {raw}.message = {e}.message; {raw}.category = {e}.category; }} }}");
        }
        return raw;
    }

    // `write/append <text> to the file "<path>"` — a fallible statement. On failure it routes to
    // the enclosing Try handler (goto), exactly like a bare fallible call. With no enclosing Try,
    // an I/O failure has nowhere to go: abort with the message (the interpreter's uncaught failure
    // is likewise fatal). The common path — a successful write — emits and continues.
    private void EmitFileWrite(StringBuilder sb, FileWriteStatement fw, string indent)
    {
        string valExpr = EmitExpr(fw.Value);
        FlushPreEmits(sb, indent);
        string pathExpr = EmitExpr(fw.Path);
        FlushPreEmits(sb, indent);
        int id = _freshId++;
        string ok = $"cf_w{id}", err = $"cf_we{id}";
        sb.AppendLine($"{indent}{{ CufetFailure {err}; int {ok} = cufet_file_write({pathExpr}, {valExpr}, {(fw.Append ? 1 : 0)}, &{err});");
        if (_currentTryHandler is { } h)
            sb.AppendLine($"{indent}  if (!{ok}) {{ {h.FailVar}.message = {err}.message; {h.FailVar}.category = {err}.category; {FileCleanupStmts(h.FileDepth)}goto {h.Label}; }} }}");
        else
            sb.AppendLine($"{indent}  if (!{ok}) {{ fprintf(stderr, \"%s\\n\", {err}.message); exit(1); }} }}");
    }

    // Stream reads (infallible). `read a line from s` → voidable text (void at EOF); `read all
    // from s` → text; `read all lines from s` → series of text. Results are arena-allocated.
    private string EmitReadExpr(ReadExpression re)
    {
        string src = EmitExpr(re.Source);
        switch (re.Form)
        {
            case ReadForm.All:
                return $"cufet_stream_read_all({src})";
            case ReadForm.Line:
            {
                string cvd = RegisterVoidableStruct(new VoidableType(TText));
                int id = _freshId++;
                _preEmits.Add($"const char* cf_rl{id} = cufet_stream_read_line({src});");
                return $"(cf_rl{id} ? ({cvd}){{ .has = 1, .val = cf_rl{id} }} : ({cvd}){{ .has = 0 }})";
            }
            case ReadForm.AllLines:
            {
                string ser = RegisterSeriesStruct(new SeriesType(TText));
                int id = _freshId++;
                string sv = $"cs_{id}", ln = $"cf_rln{id}";
                _preEmits.Add($"{ser}* {sv} = {ser}_new(); {{ const char* {ln}; while (({ln} = cufet_stream_read_line({src})) != NULL) {ser}_append({sv}, {ln}); }}");
                return sv;
            }
            default: throw new CompilerException($"unknown read form {re.Form}");
        }
    }

    // `With the file "<path>" open for reading/writing as s: … Done.` — opens the file, registers
    // its close in the cleanup stack, runs the body, and closes on EVERY exit path (normal end,
    // return, failure-goto, break/continue). An open failure becomes a Cufet failure (goto the
    // enclosing Try, or abort). Guaranteed-close is what makes a buffered write always flush.
    private void EmitWithOpen(StringBuilder sb, WithOpenStatement wos, string indent)
    {
        var inner = indent + "    ";
        int id = _freshId++;
        string pathExpr = EmitExpr(wos.Path);
        FlushPreEmits(sb, indent);
        string sVar = MangleName(wos.BindingName);
        string pathTmp = $"cf_op{id}", err = $"cf_oe{id}";
        string mode = wos.Mode == OpenMode.Reading ? "rb" : "wb";

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}const char* {pathTmp} = {pathExpr};");
        sb.AppendLine($"{inner}FILE* {sVar} = fopen({pathTmp}, \"{mode}\");");
        if (_currentTryHandler is { } h)
            sb.AppendLine($"{inner}if (!{sVar}) {{ CufetFailure {err} = cufet_file_failure({pathTmp}, errno); {FileCleanupStmts(h.FileDepth)}{h.FailVar}.message = {err}.message; {h.FailVar}.category = {err}.category; goto {h.Label}; }}");
        else
            sb.AppendLine($"{inner}if (!{sVar}) {{ CufetFailure {err} = cufet_file_failure({pathTmp}, errno); fprintf(stderr, \"%s\\n\", {err}.message); exit(1); }}");

        var savedType = _varTypes.TryGetValue(wos.BindingName, out var pt) ? pt : null;
        _varTypes[wos.BindingName] = wos.Mode == OpenMode.Reading
            ? new ReadableStreamType(TText) : new WritableStreamType(TText);
        _openFiles.Add($"fclose({sVar});");
        EmitBlock(sb, wos.Body, inner);
        _openFiles.RemoveAt(_openFiles.Count - 1);   // pop; emit the normal-exit close
        sb.AppendLine($"{inner}fclose({sVar});");
        sb.AppendLine($"{indent}}}");

        if (savedType != null) _varTypes[wos.BindingName] = savedType; else _varTypes.Remove(wos.BindingName);
    }

    // Emits a loop body, remembering the open-file depth at loop entry so a break/continue inside
    // closes files opened within the loop before jumping out.
    private void EmitLoopBody(StringBuilder sb, IReadOnlyList<IStatement> body, string indent)
    {
        _loopFileDepths.Add(_openFiles.Count);
        EmitBlock(sb, body, indent);
        _loopFileDepths.RemoveAt(_loopFileDepths.Count - 1);
    }

    private int CurrentLoopFileDepth() => _loopFileDepths.Count > 0 ? _loopFileDepths[^1] : 0;

    private string MapName(IExpression mapExpr) => RegisterMapStruct((MapType)TypeOf(mapExpr));

    // A map literal builds an arena map into a temp and populates it (like a series literal);
    // the enclosing statement flushes the pre-emits before using the temp.
    private string EmitMapLiteral(MapLiteral ml)
    {
        var mt = (MapType)MapLiteralType(ml);
        string name = RegisterMapStruct(mt);
        string tmp = $"cs_{_freshId++}";
        _preEmits.Add($"{name}* {tmp} = {name}_new();");
        foreach (var (k, v) in ml.Pairs)
        {
            string keyExpr = EmitExpr(k);
            string valExpr = EmitAsType(v, mt.ValueType);
            _preEmits.Add($"{name}_put({tmp}, {keyExpr}, {valExpr});");
        }
        return tmp;
    }

    // An object literal → a C compound literal (value struct). With embedding, the flat
    // field list is routed to the right level (own vs embedded), recursively — mirroring
    // the interpreter's BuildObjectValue.
    private string EmitObjectLiteral(ObjectLiteral ol) =>
        BuildObjectValue(ol.TypeName, ol.PositionalValues, ol.NamedValues);

    private string BuildObjectValue(string objName, IReadOnlyList<IExpression> positionals,
                                    IReadOnlyList<(string Name, IExpression Value)> named)
    {
        var def = _objectDefs[objName];
        var parts = new List<string>();
        int ownPos = def.PositionalTypes.Count;
        for (int i = 0; i < ownPos; i++) parts.Add($".p{i} = {EmitExpr(positionals[i])}");

        var ownFieldNames = def.NamedFields.Select(f => f.FieldName).ToHashSet();
        var remaining = new List<(string, IExpression)>();
        foreach (var (name, val) in named)
        {
            if (ownFieldNames.Contains(name)) parts.Add($".{MangleName(name)} = {EmitExpr(val)}");
            else remaining.Add((name, val));
        }

        if (def.EmbeddedTypeName != null)
        {
            var restPos = positionals.Skip(ownPos).ToList();
            parts.Add($".{MangleName(def.EmbeddedTypeName)} = {BuildObjectValue(def.EmbeddedTypeName, restPos, remaining)}");
        }
        return $"(({ObjStructName(objName)}){{ {string.Join(", ", parts)} }})";
    }

    // A record literal becomes a C compound literal (value struct) with designated
    // initializers, so field order in the source is irrelevant. Any series-valued field
    // pre-emits its construction; the enclosing statement flushes those first.
    private string EmitRecordLiteral(RecordLiteral rl)
    {
        string structName = RegisterRecordStruct((RecordType)TypeOf(rl));
        var parts = new List<string>();
        for (int i = 0; i < rl.PositionalFields.Count; i++)
            parts.Add($".p{i} = {EmitExpr(rl.PositionalFields[i])}");
        foreach (var (name, valExpr) in rl.NamedFields)
            parts.Add($".{MangleName(name)} = {EmitExpr(valExpr)}");
        return $"(({structName}){{ {string.Join(", ", parts)} }})";
    }

    // Emits a series literal as a named temporary, registering construction
    // statements in _preEmits. The caller must FlushPreEmits before using
    // the returned variable name in a statement.
    private string EmitSeriesLiteral(SeriesLiteral sl)
    {
        var st   = new SeriesType(SeriesElementType(sl));
        string name = RegisterSeriesStruct(st);
        string tmp = $"cs_{_freshId++}";
        _preEmits.Add($"{name}* {tmp} = {name}_new();");
        foreach (var elem in sl.Elements)
        {
            string elemExpr = EmitExpr(elem);
            _preEmits.Add($"{name}_append({tmp}, {elemExpr});");
        }
        return tmp;
    }

    private string EmitSeriesAccess(SeriesAccess sa)
    {
        // Positional access on a record/object (the first/second/... of x) → a struct field.
        var tt = TypeOf(sa.Target);
        if (tt is RecordType)
            return $"({EmitExpr(sa.Target)}).p{LiteralIndex(sa.Index) - 1}";
        if (tt is ObjectType ot)
            return $"({EmitExpr(sa.Target)}).p{ObjectPositionalIndex(ot.Name, sa.Index)}";

        string targetExpr = EmitExpr(sa.Target);
        if (sa.Index == null)
            return $"({targetExpr})->data[({targetExpr})->len - 1]";
        string idxExpr = EmitExpr(sa.Index);
        return $"({targetExpr})->data[cufet_to_int({idxExpr}) - 1]";
    }

    private string EmitCastExpr(CastExpression cast)
    {
        // A BARE fallible call (not wrapped by but-on-failure / propagate) is only valid inside
        // a Try: on failure, record the failure and jump to the handler; otherwise yield the T.
        if (FallibleReturnType(cast) is { } ft)
            return EmitFallibleCheckGoto(EmitCall(cast.Function, cast.Args), RegisterFailableStruct(ft));
        return EmitCall(cast.Function, cast.Args);
    }

    // Binds the raw fallible result to a temp; if it failed, records it into the current Try's
    // caught-failure var and gotos the handler; the expression value is the success `.val`.
    private string EmitFallibleCheckGoto(string rawCall, string cflName)
    {
        string tmp = $"cf_fl{_freshId++}";
        _preEmits.Add($"{cflName} {tmp} = {rawCall};");
        if (_currentTryHandler is not { } h)
            throw new CompilerException("a fallible call must be handled (Try, 'but on failure', or 'or pass the failure off').");
        // Close any files opened since the Try before unwinding to the handler (flush-on-failure).
        _preEmits.Add($"if ({tmp}.is_failure) {{ {h.FailVar}.message = {tmp}.message; {h.FailVar}.category = {tmp}.category; {FileCleanupStmts(h.FileDepth)}goto {h.Label}; }}");
        return $"{tmp}.val";
    }

    // Emits the raw fallible expression (the `cfl` tagged struct) without the call-site check —
    // used by but-on-failure and propagate, which inspect is_failure themselves.
    private (string CflName, string Expr) EmitFallibleRaw(IExpression expr, CufetType resultInner)
    {
        if (expr is FileReadExpression fr)
            return (RegisterFailableStruct(new FailureType(FileReadSuccessType(fr))), EmitFileReadRaw(fr));
        if (expr is RunExpression || expr is PipeExpression)
            return (RegisterFailableStruct(new FailureType(RunResultRecordType)), EmitRunRaw(expr));
        if (expr is AwaitedResultExpression are)
        {
            string raw = EmitAwaitedRaw(are, out var info);
            var aft = info.ResultType as FailureType ?? new FailureType(info.ResultType ?? TNumber);
            return (RegisterFailableStruct(aft), raw);
        }
        if (expr is BinaryExpression mb && IsMatrixOp(mb))   // matrix +/−/× with but-on-failure / propagate
            return (RegisterFailableStruct(new FailureType(MatrixType.Instance)), EmitMatrixOpRaw(mb));
        if (FallibleReturnType(expr) is { } ft)
            return (RegisterFailableStruct(ft), EmitCall(((CastExpression)expr).Function, ((CastExpression)expr).Args));
        // A bare failure literal (or other failable) as the operand — coerce into cfl of the result T.
        var ftype = new FailureType(resultInner);
        return (RegisterFailableStruct(ftype), EmitAsType(expr, ftype));
    }

    // Resolves a Cast to a free-function call or a direct method dispatch, and returns the
    // C call expression. Method receiver is passed by address (&(recv)); `one` and lvalue
    // variables work directly, and C99 compound-literal temporaries are lvalues too.
    private string EmitCall(IExpression funcExpr, IReadOnlyList<IExpression> args)
    {
        if (funcExpr is VariableReference vr)
        {
            if (_funcReturnTypes.ContainsKey(vr.Name))   // free function
                return $"{MangleName(vr.Name)}({string.Join(", ", args.Select(EmitExpr))})";

            // Method dispatch: args[0] is the receiver, the rest are method params.
            if (args.Count > 0 && TypeOf(args[0]) is ObjectType ot)
            {
                var (owner, suffix) = ResolveMethodLevel(ot.Name, vr.Name);
                var call = new[] { $"&(({EmitExpr(args[0])}){suffix})" }.Concat(args.Skip(1).Select(EmitExpr));
                return $"{MethodCName(owner, vr.Name)}({string.Join(", ", call)})";
            }
            throw new CompilerException($"'{vr.Name}': unresolved call — not a known function or method.");
        }

        // Book-member call: `<book>'s <member> of (args)` (a Cast of a book possessive-access).
        if (funcExpr is PossessiveAccess bpa && bpa.Target is VariableReference bookRef
            && _bookAliases.TryGetValue(bookRef.Name, out var bookName))
            return EmitBookFunction(bookName, bpa.Member, args);

        if (funcExpr is PossessiveAccess pa && TypeOf(pa.Target) is ObjectType pot)   // alice's greet
        {
            var (owner, suffix) = ResolveMethodLevel(pot.Name, pa.Member);
            var call = new[] { $"&(({EmitExpr(pa.Target)}){suffix})" }.Concat(args.Select(EmitExpr));
            return $"{MethodCName(owner, pa.Member)}({string.Join(", ", call)})";
        }

        throw new CompilerException("Function-value calls are not yet supported by the compiler.");
    }

    // Routes a book function to its C emission. 1A: the `math` book's EXACT-decimal total functions
    // (floor/ceiling/round/absolute value). Transcendentals (square root/log/power → 1B), collections
    // aggregates (minimum/maximum/average/unique → 1C), transpose (→ 1D) defer cleanly.
    private string EmitBookFunction(string bookName, string member, IReadOnlyList<IExpression> args)
    {
        string m = member.ToLowerInvariant();
        if (bookName == "math" && m is "floor" or "ceiling" or "round" or "absolute value")
        {
            _usesMath = true;
            string fn = m == "absolute value" ? "cufet_math_abs" : $"cufet_math_{m}";
            return $"{fn}({EmitExpr(args[0])})";
        }
        if (bookName == "math" && m is "square root" or "log" or "power")
        {
            // Double-backed transcendental (1B) → voidable number: non-finite / decimal-overflow →
            // void (MathPartial). The raw call returns 1+*out or 0=void; wrap into the cvd inline,
            // the same shape as a channel delivery / read-line.
            _usesMath = true;
            string cvd = RegisterVoidableStruct(new VoidableType(TNumber));
            int id = _freshId++;
            string call = m switch
            {
                "square root" => $"cufet_math_sqrt({EmitExpr(args[0])}, &cf_mv{id})",
                "log"         => $"cufet_math_log({EmitExpr(args[0])}, &cf_mv{id})",
                _             => $"cufet_math_power({EmitExpr(args[0])}, {EmitExpr(args[1])}, &cf_mv{id})",
            };
            _preEmits.Add($"CufetDec cf_mv{id}; int cf_mh{id} = {call};");
            return $"(cf_mh{id} ? ({cvd}){{ .has = 1, .val = cf_mv{id} }} : ({cvd}){{ .has = 0 }})";
        }
        if (bookName == "collections" && m is "minimum" or "maximum" or "average")
        {
            // Series-of-number reduction → voidable number (void on empty — reuses 5C). Replicates
            // the interpreter exactly: min/max keep the FIRST of ties (strict compare, LINQ Min/Max);
            // average is LINQ Sum (sequential cufet_add from 0) then ONE cufet_div by the count —
            // fully exact decimal arithmetic, no double bridge.
            string cvd = RegisterVoidableStruct(new VoidableType(TNumber));
            string ser = SeriesStructOf(args[0]);
            string src = EmitExpr(args[0]);
            int id = _freshId++;
            if (m == "average")
                _preEmits.Add(
                    $"{ser}* cf_as{id} = {src}; CufetDec cf_ag{id} = cufet_dec_from_ll(0); " +
                    $"for (int cf_i{id} = 0; cf_i{id} < cf_as{id}->len; cf_i{id}++) cf_ag{id} = cufet_add(cf_ag{id}, cf_as{id}->data[cf_i{id}]); " +
                    $"int cf_ah{id} = cf_as{id}->len > 0; " +
                    $"if (cf_ah{id}) cf_ag{id} = cufet_div(cf_ag{id}, cufet_dec_from_ll(cf_as{id}->len));");
            else
                _preEmits.Add(
                    $"{ser}* cf_as{id} = {src}; CufetDec cf_ag{id} = cufet_dec_from_ll(0); " +
                    $"int cf_ah{id} = cf_as{id}->len > 0; " +
                    $"if (cf_ah{id}) {{ cf_ag{id} = cf_as{id}->data[0]; " +
                    $"for (int cf_i{id} = 1; cf_i{id} < cf_as{id}->len; cf_i{id}++) " +
                    $"if (cufet_cmp(cf_as{id}->data[cf_i{id}], cf_ag{id}) {(m == "minimum" ? "<" : ">")} 0) cf_ag{id} = cf_as{id}->data[cf_i{id}]; }}");
            return $"(cf_ah{id} ? ({cvd}){{ .has = 1, .val = cf_ag{id} }} : ({cvd}){{ .has = 0 }})";
        }
        if (bookName == "collections" && m == "unique")
        {
            // Element-type-preserving first-occurrence dedup — per-type value equality via EqCall
            // (number/text/record/object/series/voidable all compose — the series-of-T payoff).
            // Builds a NEW arena series (non-mutating), like sorted.
            var st = (SeriesType)TypeOf(args[0]);
            string ser = RegisterSeriesStruct(st);
            string src = EmitExpr(args[0]);
            int id = _freshId++;
            string eq = EqCall($"cf_ud{id}->data[cf_j{id}]", $"cf_us{id}->data[cf_i{id}]", st.ElementType);
            _preEmits.Add(
                $"{ser}* cf_us{id} = {src}; {ser}* cf_ud{id} = {ser}_new(); " +
                $"for (int cf_i{id} = 0; cf_i{id} < cf_us{id}->len; cf_i{id}++) {{ int cf_sn{id} = 0; " +
                $"for (int cf_j{id} = 0; cf_j{id} < cf_ud{id}->len; cf_j{id}++) if ({eq}) {{ cf_sn{id} = 1; break; }} " +
                $"if (!cf_sn{id}) {ser}_append(cf_ud{id}, cf_us{id}->data[cf_i{id}]); }}");
            return $"cf_ud{id}";
        }
        if (bookName == "collections" && m == "transpose")
        {
            _usesMatrix = true;
            return $"cufet_mat_transpose({EmitExpr(args[0])})";   // infallible: cols×rows flip
        }
        if (bookName == "collections")
            throw new CompilerException($"the collections book's member '{member}' is not supported by the compiler.");
        throw new CompilerException($"book '{bookName}' member '{member}' is not yet supported by the compiler.");
    }

    // Handles is void / is not void, voidable-vs-voidable, and voidable-vs-plain-T equality.
    // Returns null when neither operand is void/voidable (the caller falls through).
    private string? EmitVoidableComparison(BinaryExpression b)
    {
        bool eq = b.Op == TokenType.Equal;

        // is void / is not void — one operand is the bare `void` literal.
        if (b.Left is VoidLiteral || b.Right is VoidLiteral)
        {
            var side = b.Left is VoidLiteral ? b.Right : b.Left;
            string v = EmitExpr(side);            // evaluated once; only .has is read
            return eq ? $"(!({v}).has)" : $"(({v}).has)";
        }

        var lt = TypeOf(b.Left);
        var rt = TypeOf(b.Right);

        if (lt is VoidableType && rt is VoidableType)            // voidable vs voidable
        {
            string e = EqCall(EmitExpr(b.Left), EmitExpr(b.Right), lt);
            return eq ? $"({e})" : $"(!({e}))";
        }
        if (lt is VoidableType lv && rt.Equals(lv.Inner))         // voidable vs plain T
            return VoidableVsInner(EmitExpr(b.Left), EmitExpr(b.Right), lv.Inner, eq);
        if (rt is VoidableType rv && lt.Equals(rv.Inner))         // plain T vs voidable
            return VoidableVsInner(EmitExpr(b.Right), EmitExpr(b.Left), rv.Inner, eq);

        return null;
    }

    // A voidable equals a plain T iff it's present and the value matches.
    private string VoidableVsInner(string voidableExpr, string tExpr, CufetType inner, bool eq)
    {
        string present = $"(({voidableExpr}).has && {EqCall($"({voidableExpr}).val", tExpr, inner)})";
        return eq ? present : $"(!{present})";
    }

    private string EmitUnary(UnaryExpression u) => u.Op switch
    {
        TokenType.Minus => $"cufet_neg({EmitExpr(u.Operand)})",
        TokenType.Not   => $"(!{EmitExpr(u.Operand)})",
        _ => throw new CompilerException($"Unary operator '{u.Op}' is not yet supported by the compiler.")
    };

    private string EmitBinary(BinaryExpression b)
    {
        // Void / voidable comparisons first — a bare `void` operand has no standalone C form,
        // so it must be handled before EmitExpr touches the operands.
        if (b.Op is TokenType.Equal or TokenType.NotEqual && EmitVoidableComparison(b) is { } vc)
            return vc;

        // Matrix arithmetic is FALLIBLE (dimension mismatch → failure): a bare matrix op routes
        // through the standard fallible machinery — check-goto in a Try, exactly like a fallible call.
        if (IsMatrixOp(b))
            return EmitFallibleCheckGoto(EmitMatrixOpRaw(b), RegisterFailableStruct(new FailureType(MatrixType.Instance)));

        string L = EmitExpr(b.Left);
        string R = EmitExpr(b.Right);

        switch (b.Op)
        {
            case TokenType.Plus:    return $"cufet_add({L}, {R})";
            case TokenType.Minus:   return $"cufet_sub({L}, {R})";
            case TokenType.Star:    return $"cufet_mul({L}, {R})";
            case TokenType.Slash:   return $"cufet_div({L}, {R})";
            case TokenType.Percent: return $"cufet_mod({L}, {R})";
            case TokenType.And:     return $"({L} && {R})";
            case TokenType.Or:      return $"({L} || {R})";
        }

        // Comparison / equality. Numbers via cufet_cmp; text via strcmp; facts are ints.
        var lt = TypeOf(b.Left);
        if (lt is NumberType)
        {
            string cmp = $"cufet_cmp({L}, {R})";
            return b.Op switch
            {
                TokenType.Equal    => $"({cmp} == 0)",
                TokenType.NotEqual => $"({cmp} != 0)",
                TokenType.Lt       => $"({cmp} < 0)",
                TokenType.Gt       => $"({cmp} > 0)",
                TokenType.Lte      => $"({cmp} <= 0)",
                TokenType.Gte      => $"({cmp} >= 0)",
                _ => throw new CompilerException($"Binary operator '{b.Op}' is not yet supported by the compiler.")
            };
        }

        if (lt is TextType)
            return b.Op switch
            {
                TokenType.Equal    => $"(strcmp({L}, {R}) == 0)",
                TokenType.NotEqual => $"(strcmp({L}, {R}) != 0)",
                _ => throw new CompilerException($"Text comparison '{b.Op}' is not yet supported by the compiler.")
            };

        // Records (structural) / objects (nominal) / series (element-wise, in-order): value
        // equality via _eq. Series are ordered sequences — two series are equal iff same elements
        // in the same order, matching the interpreter's List value-equality (NOT pointer equality;
        // maps below stay pointer/reference equality, which is what the interpreter does for them).
        if (lt is RecordType or ObjectType or SeriesType)
        {
            string eq = EqCall(L, R, lt);
            return b.Op switch
            {
                TokenType.Equal    => $"({eq})",
                TokenType.NotEqual => $"(!({eq}))",
                _ => throw new CompilerException($"'{b.Op}' on a '{lt.GetType().Name}' is not supported (only is / is not).")
            };
        }

        // Facts (ints) and maps (reference/pointer equality — matches the interpreter's Dictionary).
        return b.Op switch
        {
            TokenType.Equal    => $"({L} == {R})",
            TokenType.NotEqual => $"({L} != {R})",
            _ => throw new CompilerException($"Binary operator '{b.Op}' on '{lt.GetType().Name}' is not yet supported by the compiler.")
        };
    }

    // cv_ prefix avoids C keyword collisions (e.g. Cufet "double" → cv_double).
    // Hyphens replaced by underscores; Cufet identifiers never contain underscores, so no collision.
    private static string MangleName(string name) => "cv_" + name.Replace('-', '_');

    // Maps a Cufet type to its C type. Records are value structs (synthesized per shape);
    // text is an immutable const char*; series/maps are arena pointers. Objects/maps are
    // not yet lowered (later slices) — the default arm defers cleanly.
    private string EmitCType(CufetType? type) => type switch
    {
        null       => "void",
        NumberType => "CufetDec",
        FactType   => "int",
        TextType   => "const char*",
        SeriesType st => RegisterSeriesStruct(st) + "*",   // series are arena pointers (reference type)
        RecordType rt => RegisterRecordStruct(rt),
        ObjectType ot => ObjStructName(ot.Name),
        VoidableType vt => RegisterVoidableStruct(vt),
        MapType mt => RegisterMapStruct(mt) + "*",   // maps are arena pointers (reference type)
        FailureType ft => RegisterFailableStruct(ft),
        FailureMarkerType => "CufetFailure",         // a caught / bare failure (message + category)
        ReadableStreamType or WritableStreamType => "FILE*",   // a stream is an open FILE* (or stdin)
        ChannelType => "cufet_chan*",                          // a channel is a shared mutex/condvar queue
        MatrixType => MatrixCType(),                           // a matrix is an arena pointer (reference type)
        _ => throw new CompilerException(
                 $"'{type!.GetType().Name}' is not yet supported by the compiler (slice 5B: records + text; objects/maps later).")
    };

    // Emits a number literal as a CufetDec constructor, decomposing the C# decimal
    // into its 96-bit coefficient (hi/lo halves), scale, and sign via decimal.GetBits.
    // This is bit-identical to the interpreter because both start from the same decimal.
    private static string EmitNumberLiteral(decimal d)
    {
        int[] bits = decimal.GetBits(d);
        ulong lo   = (uint)bits[0];
        ulong mid  = (uint)bits[1];
        ulong hi   = (uint)bits[2];
        int flags  = bits[3];
        int scale  = (flags >> 16) & 0xFF;
        int sign   = flags < 0 ? 1 : 0;
        ulong lo64 = (mid << 32) | lo;   // low 64 bits of the coefficient
        ulong hi64 = hi;                 // high 32 bits of the coefficient
        return $"cufet_dec_lit({hi64}ULL, {lo64}ULL, {scale}, {sign})";
    }

    // The lexer resolves escape sequences, so StringLiteral.Value is the cooked string.
    // Re-escape it for C: backslash and double-quote must be escaped; control chars normalized.
    private static string EscapeStringLiteral(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"'  => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                _    => c.ToString()
            });
        sb.Append('"');
        return sb.ToString();
    }
}
