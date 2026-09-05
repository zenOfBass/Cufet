using System.Text;
using Cufet.Interpreter;
using Cufet.Lexer;

namespace Cufet.Compiler;

/// <summary>
/// The C the generator pastes into every program — the fixed runtime, as source.
/// </summary>
/// <remarks>
/// <para>
/// ★★ This is DATA, not logic: C source held in string constants, emitted verbatim when a program
/// uses the thing it supports. It sat interleaved with the emitters that reference it and made up
/// a sixth of the largest file in the repo, so a reader scrolling for a C# method walked through
/// hundreds of lines of C to reach one.
/// </para>
/// <para>
/// ★ Each block is gated by a `_usesX` flag set while emitting, so a program that never launches a
/// process carries no process runtime. Which blocks a program gets is decided in
/// <c>CodeGenerator.cs</c>; what is in them is decided here.
/// </para>
/// <para>
/// ⚠ The comment above each block is the only explanation of C that nothing else documents — why
/// a buffer is UTF-32, why a wait retries on EINTR, why setjmp is spelled the way it is on mingw.
/// They moved with their blocks deliberately; a constant here with no comment is a gap, not a
/// tidy-up.
/// </para>
/// </remarks>
public sealed partial class CodeGenerator
{
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
#include <setjmp.h>
#include <stdarg.h>
/* ⚠ For int32_t. A chase stores one code point per element and the width is part of what the
   type PROMISES — four bytes, UTF-32 — so it is spelled exactly rather than left to `int`, which
   C does not guarantee to be 32 bits even where every target this compiles for happens to make
   it so. The interpreter stores .NET Int32, and the two have to be the same thing. */
#include <stdint.h>
#if defined(_WIN32)
#include <io.h>
#include <fcntl.h>
#endif

/* ───────── setjmp WITHOUT a Windows SEH unwind ─────────
   ★★ On x86-64 mingw-w64, plain `setjmp(b)` expands to `_setjmp((b), __builtin_frame_address(0))`
   (setjmp.h), and that saved frame pointer makes `longjmp` perform a full SEH unwind through
   ntdll's RtlUnwindEx. At -O2 that unwinder reads stack memory it cannot validate and
   ACCESS-VIOLATES depending on what happens to be on the stack — measured at 121 crashes in 3000
   serial runs of one binary that raises and catches once. Passing NULL as the context makes
   longjmp restore registers directly and skip the unwind: 3000 runs, 0 crashes.

   ★ Skipping it is not a workaround, it is what this runtime already assumes. The unwinder's job
   is to run __finally blocks and C++ destructors between the jump and its target; generated Cufet
   C has neither, and cufet_raise runs the unmakers, closes the files and pops the arenas ITSELF
   before it jumps. There was never anything for RtlUnwindEx to do.

   ⚠ Two arguments is the mingw-w64 spelling. glibc's `_setjmp` takes one, so this is a _WIN32
   branch and nothing else changes on POSIX.

   ⚠ It presented as a test that "flaked occasionally" for weeks. It was a 4%-of-runs crash in
   every compiled program that catches an exception on Windows, and the reason it looked rare is
   that one suite run launches such a binary a handful of times. */
#if defined(_WIN32)
#define CUFET_PLAIN_SETJMP(b) _setjmp((b), NULL)
#else
#define CUFET_PLAIN_SETJMP(b) setjmp(b)
#endif

/* ───────── Line endings on stdout ─────────
   Windows opens stdout in TEXT mode, where the C runtime rewrites every '\n' on its way out as
   "\r\n". That is fine for a line terminator and wrong for everything else: a '\n' the program
   put INSIDE a text value is data, and rewriting it makes the compiled backend print something
   the interpreter does not. `State "a\nb".` gave 61 0a 62 interpreted and 61 0d 0a 62 compiled.

   So stdout is switched to BINARY at startup — nothing is rewritten, and what a program prints is
   what it wrote — and the line terminator becomes explicit. CUFET_NL is what `State` appends, and
   it is "\r\n" on Windows because that is what the interpreter's WriteLine emits there; the two
   backends have to agree on the terminator as well as on the data.

   ★ Every newline written to stdout is one or the other, and the distinction is the whole point:
   a TERMINATOR calls cufet_nl(), and DATA is passed through untouched. Emitting a bare newline
   through printf is neither, which is why GeneratedC_UsesTheNewlineMacro refuses one — this is
   the per-site pattern that has been reintroduced by hand three times in this codebase, so it is
   held by a test rather than by remembering. stderr is deliberately left in text mode:
   diagnostics are terminator-only, and both backends already agree there. */
#if defined(_WIN32)
#define CUFET_NL "\r\n"
#define CUFET_STDOUT_BINARY() (void)_setmode(_fileno(stdout), _O_BINARY)
#else
#define CUFET_NL "\n"
#define CUFET_STDOUT_BINARY() ((void)0)
#endif
#define cufet_nl() fputs(CUFET_NL, stdout)

/* ───────── One `State` is ONE line, even from two threads ─────────
   A State writes in several calls — the value, then the terminator, and a series or record writes
   every element and separator separately. Two threads printing at once interleaved BETWEEN those
   calls, so output came out spliced: `side effectdone` followed by both newlines. Measured at
   roughly 4-8% of runs on a two-thread program whose output is otherwise deterministic.

   Locking the stream for the whole statement makes a State atomic against other threads. stdio
   calls take this same lock individually, so holding it across them is exactly what it is for, and
   an unthreaded program pays one uncontended lock per line.

   No-op off POSIX: tasks are pthreads-only, so a Windows build has no second thread to race. */
#if defined(__unix__) || defined(__APPLE__)
#define cufet_out_lock()   flockfile(stdout)
#define cufet_out_unlock() funlockfile(stdout)
#else
#define cufet_out_lock()   ((void)0)
#define cufet_out_unlock() ((void)0)
#endif

/* ───────── Exceptions (E-prime): setjmp/longjmp over SOFTWARE faults ─────────
   Cufet numbers are software decimals, so divide/modulo-by-zero, series/matrix OOB, etc. are
   software-DETECTED conditions, not hardware signals. Every fault site calls cufet_raise: if a
   `Try to: … In case of exception:` handler is installed (a per-thread jmp_buf stack — nested
   Trys nest; innermost wins), the fault longjmps to it; otherwise the pre-exception behavior
   (print + exit 1) is unchanged. Messages match the interpreter's RuntimeException text
   (arena-allocated so a bound `the message of the exception` outlives later faults). */
#define CUFET_EXC_MAX 64
static _Thread_local jmp_buf cufet_exc_bufs[CUFET_EXC_MAX];
static _Thread_local int cufet_exc_top = -1;
static _Thread_local const char* cufet_exc_msg = 0;
/* Unmaker (destructor) registry (UNMK) — user `unmaking` bodies run at block scope-exit. A longjmp
   (exception) abandons the C-stack objects those bodies touch, so cufet_raise runs the pending
   unmakers BEFORE it jumps, down to the target handler's snapshot (cufet_exc_um[]). Normal / return /
   Stop / failure-goto exits run them at emit-time via cufet_run_unmakers_to. Zero cost when unused
   (cufet_num stays 0). */
#define CUFET_UNMAKERS_MAX 8192
static _Thread_local void* cufet_um_obj[CUFET_UNMAKERS_MAX];
static _Thread_local void (*cufet_um_fn[CUFET_UNMAKERS_MAX])(void*);
static _Thread_local int cufet_num = 0;
static _Thread_local int cufet_exc_um[CUFET_EXC_MAX];
static void cufet_reg_unmaker(void* o, void (*f)(void*)) { if (cufet_num < CUFET_UNMAKERS_MAX) { cufet_um_obj[cufet_num] = o; cufet_um_fn[cufet_num] = f; cufet_num++; } }
static void cufet_run_unmakers_to(int n) { while (cufet_num > n) { cufet_num--; cufet_um_fn[cufet_num](cufet_um_obj[cufet_num]); } }
static void* cufet_arena_alloc(size_t size);            /* defined with the arena, below */
static void* cufet_arena_alloc_at(int depth, size_t size);
/* Arena depth at each Try's setjmp — the exception MESSAGE is copied into that arena at raise time
   (see cufet_raise) so it survives the arena pops the catch performs on the way in. */
static _Thread_local int cufet_exc_arena[CUFET_EXC_MAX];
static const char* cufet_msgf(const char* fmt, ...) {
    va_list ap; va_start(ap, fmt);
    va_list ap2; va_copy(ap2, ap);
    int need = vsnprintf(NULL, 0, fmt, ap) + 1;
    va_end(ap);
    char* b = (char*)cufet_arena_alloc((size_t)need);
    vsnprintf(b, (size_t)need, fmt, ap2);
    va_end(ap2);
    return b;
}
static void cufet_raise(const char* msg) {
    if (cufet_exc_top >= 0) {
        /* ★ MESSAGE LIFETIME: cufet_msgf allocates in the arena live at the FAULT site, but the catch
           pops every arena deeper than the Try before running the handler — so the message would
           dangle (and its block get reused by the next arena_alloc, reading back as another string).
           Copy it into the TARGET handler's OWN arena, which the catch never pops: that arena outlives
           the handler, and a re-raise outward re-copies into the next handler's arena. Arena-managed,
           so there is no malloc/free discipline and no leak. A raise with no handler needs no copy —
           nothing has been popped yet and we print and exit immediately. */
        if (msg) {
            size_t n = strlen(msg) + 1;
            char* b = (char*)cufet_arena_alloc_at(cufet_exc_arena[cufet_exc_top], n);
            memcpy(b, msg, n);
            msg = b;
        }
        cufet_exc_msg = msg;
        cufet_run_unmakers_to(cufet_exc_um[cufet_exc_top]);
        longjmp(cufet_exc_bufs[cufet_exc_top], 1);
    }
    /* ★ NO HANDLER: the program is ending, but this thread's pending unmakers still run first —
       the same unwind the interpreter performs as the exception propagates out through each open
       block. Skipping them was a real divergence: an object made inside a block that then faulted
       was never unmade, which is invisible until the destructor DOES something (prints, unlinks a
       temp file, releases a lock). The registry is _Thread_local, so a dying worker runs only its
       own; `to(0)` is this thread's whole pending set, which is what unwinding to the top means.
       Free when unused — cufet_num stays 0. Files need no equivalent: exit() flushes them. */
    cufet_run_unmakers_to(0);
    fprintf(stderr, "%s\n", msg);
    exit(1);
}
/* Runtime FILE registry — a longjmp (exception OR interrupt) jumps past the emit-time fclose
   sites, so open files must be runtime-tracked to flush+close on the unwind (the 9B no-data-loss
   discipline, extended to nonlocal jumps). Normal closes unregister → no double-close. */
#define CUFET_FILES_MAX 256
static _Thread_local FILE* cufet_live_files[CUFET_FILES_MAX];
static _Thread_local int cufet_nfiles = 0;
static void cufet_reg_file(FILE* f) { if (cufet_nfiles < CUFET_FILES_MAX) cufet_live_files[cufet_nfiles++] = f; }
static void cufet_close_file(FILE* f) {
    for (int i = cufet_nfiles - 1; i >= 0; i--)
        if (cufet_live_files[i] == f) { for (int j = i; j < cufet_nfiles - 1; j++) cufet_live_files[j] = cufet_live_files[j + 1]; cufet_nfiles--; break; }
    fclose(f);
}
static void cufet_close_files_from(int n) { while (cufet_nfiles > n) fclose(cufet_live_files[--cufet_nfiles]); }

/* 1-based series bounds checks — the messages replicate the interpreter's warm OOB errors.
   (Compiled series access was previously UNCHECKED — E-prime adds the checks so OOB is a real,
   catchable exception instead of undefined behavior.) */
static long long cufet_idx_check(long long idx, int len, const char* name, int line) {
    if (idx >= 1 && idx <= len) return idx;
    if (len == 0)
        cufet_raise(cufet_msgf("There's no item %lld — '%s' is empty. This happened on line %d.", idx, name, line));
    cufet_raise(cufet_msgf("There's no item %lld — '%s' has %d %s (you can reach items 1 through %d). This happened on line %d.",
                           idx, name, len, len == 1 ? "item" : "items", len, line));
    return 0;
}
static long long cufet_last_check(int len, const char* name, int line) {
    if (len == 0) cufet_raise(cufet_msgf("Can't access the last item — '%s' is empty on line %d.", name, line));
    return len;
}

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
/* A `number` on its way INTO foreign source. Range-checked, never truncated: C is being handed a
   64-bit integer, and a decimal that is fractional or too large is a mistake in the program rather
   than something to round off quietly. Raised as an ordinary catchable exception, the same class as
   a divide by zero — see cufet_raise. The interpreter checks identically before it marshals. */
static void cufet_raise(const char* msg);
static const char* cufet_msgf(const char* fmt, ...);
static const char* cufet_text_from_dec(CufetDec d);
static long long cufet_foreign_ll(CufetDec d, int line) {
    /* ⚠ The ORIGINAL, kept for the message. Scaling `d` down in place and reporting that prints
       3.50 as "3.5" — a different sentence from the one the interpreter produces for the same
       program, which the oracle compares as strictly as it compares answers. */
    CufetDec as_written = d;
    for (int s = d.scale; s > 0; s--) {
        if (d.coef % 10 != 0)
            cufet_raise(cufet_msgf("Foreign source takes whole numbers, but got %s. This happened on line %d.",
                                   cufet_text_from_dec(as_written), line));   /* ForeignC.WholeArgumentMessage */
        d.coef /= 10; d.scale--;
    }
    if (d.coef > (unsigned __int128)9223372036854775807ULL + (d.sign ? 1u : 0u))
        cufet_raise(cufet_msgf("%s is too large to hand to foreign source. This happened on line %d.",
                               cufet_text_from_dec(as_written), line));       /* ForeignC.LargeArgumentMessage */
    unsigned long long m = (unsigned long long)d.coef;
    return d.sign ? -(long long)m : (long long)m;
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
/* Minimal form, the way .NET leaves a decimal DIVISION: 11/10 is 1.1 at scale 1, not
   1.1000...0 at scale 28. Trailing zeros are invisible when printed (cufet_format_number
   strips them too), so a difference here hides until some LATER operation on the value
   overflows at one scale and not the other — which is exactly how it was found. */
static CufetDec cufet_dec_strip(CufetDec d) {
    while (d.scale > 0 && d.coef != 0 && d.coef % 10 == 0) { d.coef /= 10; d.scale--; }
    return d;
}
static CufetDec cufet_div(CufetDec a, CufetDec b, int line) {
    if (b.coef == 0) cufet_raise(cufet_msgf("Division by zero on line %d.", line));
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
        return cufet_dec_strip(cufet_dec_reduce(Q, 28, rsign, 0));
    }
    return cufet_dec_strip(cufet_dec_reduce(Q, 28, rsign, !u256_is_zero(R)));
}
static CufetDec cufet_mod(CufetDec a, CufetDec b, int line) {           /* remainder, sign of dividend */
    if (b.coef == 0) cufet_raise(cufet_msgf("Modulo by zero on line %d.", line));
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
/* A bit pattern: unsigned, at most 64 bits. `base` is the display base ('x', 'o' or 'b') —
   a pattern shows itself in the base it was written in — and `width` is the bit width the
   literal's digits spelled out, which is what `not` flips within and what pads the display.
   Both ride on the value rather than the type, so every bits value is assignable to any other. */
typedef struct { unsigned long long value; char base; int width; } CufetBits;

/* write_ = format inline (no newline), for nested printing inside records/objects/series.
   print_ = write_ + newline, for a top-level State. */
static void cufet_write_number(CufetDec d) { char b[64]; cufet_format_number(b, sizeof(b), d); printf("%s", b); }
static void cufet_write_fact(int b) { printf("%s", b ? "true" : "false"); }
static void cufet_write_text(const char* s) { printf("%s", s); }
/* Digits are padded out to the declared width, so 0x0F prints as 0x0F and not 0xF. A value that
   outgrew its width prints in the smallest width that holds it — nothing is ever truncated.
   Hex digits are canonically uppercase: a computed value has no literal to take its case from. */
static void cufet_format_bits(char* buf, size_t bufsz, CufetBits x) {
    int per = x.base == 'x' ? 4 : (x.base == 'o' ? 3 : 1);
    int declared = (x.width + per - 1) / per;
    char ds[68];
    int n = 0;
    unsigned long long v = x.value;
    if (v == 0) ds[n++] = '0';
    while (v) {
        int d = (int)(v & (unsigned long long)((1 << per) - 1));
        ds[n++] = (char)(d < 10 ? '0' + d : 'A' + d - 10);
        v >>= per;
    }
    char out[80];
    int p = 0;
    out[p++] = '0';
    out[p++] = x.base;
    for (int i = n; i < declared; i++) out[p++] = '0';
    for (int i = n - 1; i >= 0; i--) out[p++] = ds[i];
    out[p] = '\0';
    snprintf(buf, bufsz, "%s", out);
}
static void cufet_write_bits(CufetBits x) { char b[80]; cufet_format_bits(b, sizeof(b), x); printf("%s", b); }
static void cufet_print_number(CufetDec d) { cufet_write_number(d); cufet_nl(); }
static void cufet_print_fact(int b) { cufet_write_fact(b); cufet_nl(); }
static void cufet_print_text(const char* s) { cufet_write_text(s); cufet_nl(); }
static void cufet_print_bits(CufetBits x) { cufet_write_bits(x); cufet_nl(); }

/* The gates. A result carries the LEFT operand's base and width, widened when the value needs
   more room — left because in real bit code the left operand is the accumulator
   (`flags or MASK`, `flags and not MASK`), so it is the thing you will print. Widening rather
   than truncating means nothing ever silently falls off the end. */
static int cufet_bits_minwidth(unsigned long long v) {
    int n = 0;
    while (v) { n++; v >>= 1; }
    return n;
}
static CufetBits cufet_bits_combine(CufetBits left, unsigned long long result) {
    int min = cufet_bits_minwidth(result);
    CufetBits out;
    out.value = result;
    out.base  = left.base;
    out.width = left.width > min ? left.width : min;
    return out;
}
static CufetBits cufet_bits_and(CufetBits a, CufetBits b) { return cufet_bits_combine(a, a.value & b.value); }
static CufetBits cufet_bits_or (CufetBits a, CufetBits b) { return cufet_bits_combine(a, a.value | b.value); }
static CufetBits cufet_bits_xor(CufetBits a, CufetBits b) { return cufet_bits_combine(a, a.value ^ b.value); }
/* Flips every bit WITHIN the value's own width, so not 0xFF is 0x00 and not 0b1010 is 0b0101.
   Unsigned with a known width is precisely why those are the answers rather than a negative. */
static CufetBits cufet_bits_not(CufetBits a) {
    unsigned long long mask = a.width >= 64 ? ~0ULL : ((1ULL << a.width) - 1ULL);
    CufetBits out;
    out.value = (~a.value) & mask;
    out.base  = a.base;
    out.width = a.width;
    return out;
}

/* Arithmetic. The type is unsigned with a 64-bit ceiling, so a result that would go negative or
   need a 65th bit has no representation and RAISES — the same treatment division by zero already
   gets. A value-level failure would ride in the type as `bits or failure` and force an unwrap
   after every masking expression, which is exactly why divide-by-zero is not one. */
static void cufet_bits_overflow(CufetBits a, CufetBits b, const char* op, const char* why, int line) {
    char x[80], y[80];
    cufet_format_bits(x, sizeof(x), a);
    cufet_format_bits(y, sizeof(y), b);
    cufet_raise(cufet_msgf("%s %s %s %s (line %d).", x, op, y, why, line));
}
static CufetBits cufet_bits_add(CufetBits a, CufetBits b, int line) {
    if (a.value > ~0ULL - b.value) cufet_bits_overflow(a, b, "+", "does not fit in 64 bits", line);
    return cufet_bits_combine(a, a.value + b.value);
}
static CufetBits cufet_bits_sub(CufetBits a, CufetBits b, int line) {
    if (b.value > a.value) cufet_bits_overflow(a, b, "-", "would be negative, and bits are unsigned", line);
    return cufet_bits_combine(a, a.value - b.value);
}
static CufetBits cufet_bits_mul(CufetBits a, CufetBits b, int line) {
    if (a.value != 0 && b.value > ~0ULL / a.value) cufet_bits_overflow(a, b, "*", "does not fit in 64 bits", line);
    return cufet_bits_combine(a, a.value * b.value);
}
static CufetBits cufet_bits_div(CufetBits a, CufetBits b, int line) {
    if (b.value == 0) cufet_raise(cufet_msgf("Division by zero on line %d.", line));
    return cufet_bits_combine(a, a.value / b.value);
}
static CufetBits cufet_bits_mod(CufetBits a, CufetBits b, int line) {
    if (b.value == 0) cufet_raise(cufet_msgf("Modulo by zero on line %d.", line));
    return cufet_bits_combine(a, a.value % b.value);
}

/* Shifting. The amount arrives as a CufetDec because it counts POSITIONS — a quantity, like the
   3 in "item 3 of s" — so it has to be whole and non-negative.

   Note the >= 64 guards: shifting by at least the operand's width is UNDEFINED BEHAVIOUR in C,
   so the answer has to be written out rather than left to the hardware. */
/* <number> converted to hex|binary|octal. Width is the smallest that holds the value, rounded up
   to whole digits of the target base, so 255 becomes 0xFF and 16 becomes 0x10. Raises rather than
   yielding a voidable, matching arithmetic overflow. */
static CufetBits cufet_bits_from_number(CufetDec d, char base_, int line);
static CufetDec cufet_bits_to_number(CufetBits b) {
    CufetDec d;
    d.coef  = (unsigned __int128)b.value;
    d.scale = 0;
    d.sign  = 0;
    return d;
}

/* Whole iff dividing the coefficient down by its scale leaves no remainder. Done on the struct
   so it stays exact and cannot overflow, unlike round-tripping through an int. */
static int cufet_bits_whole(CufetDec d) {
    unsigned __int128 c = d.coef;
    for (int s = d.scale; s > 0; s--) { if (c % 10 != 0) return 0; c /= 10; }
    return 1;
}
static CufetBits cufet_bits_shift(CufetBits a, CufetDec amount, int left, int line) {
    char buf[64];
    if (!cufet_bits_whole(amount)) {
        cufet_format_number(buf, sizeof(buf), amount);
        cufet_raise(cufet_msgf("the shift amount must be a whole number of positions, not %s (line %d).", buf, line));
    }
    if (cufet_cmp(amount, cufet_dec_from_ll(0)) < 0)
        cufet_raise(cufet_msgf("the shift amount cannot be negative — shift the other way instead (line %d).", line));

    /* Clamp before converting: anything past the ceiling behaves identically, and cufet_to_int
       would overflow on a genuinely huge amount. */
    int by = cufet_cmp(amount, cufet_dec_from_ll(64)) > 0 ? 65 : cufet_to_int(amount);

    if (!left) {
        CufetBits out;
        out.value = by >= 64 ? 0ULL : (a.value >> by);
        out.base  = a.base;
        out.width = a.width;
        return out;
    }

    if ((by >= 64 && a.value != 0) || (by < 64 && a.value > (~0ULL >> by))) {
        char x[80];
        cufet_format_bits(x, sizeof(x), a);
        cufet_format_number(buf, sizeof(buf), amount);
        cufet_raise(cufet_msgf("%s shifted left by %s does not fit in 64 bits (line %d).", x, buf, line));
    }
    return cufet_bits_combine(a, by >= 64 ? 0ULL : (a.value << by));
}

/* `<bits> at <n> bits` - the same value carried at a STATED width.

   A width is otherwise only ever raised to fit the value, so leading zeros no operand ever held
   could not be produced: `0b0 shifted left by 2` is `0b0`, not `0b000`. This is what lets a
   program choose one - and `0b0 at 3 bits` is how "three zero bits" is spelled.

   Widening is free. Narrowing is refused when it would drop a set bit, because a packer that
   silently loses its high bits writes a file that decodes to garbage. `cufet_bits_minwidth` is
   the same count Interpreter.EvaluateBitsAtWidth computes, so both backends refuse identically. */
static CufetBits cufet_bits_at_width(CufetBits x, CufetDec w, int line) {
    char buf[64];
    if (!cufet_bits_whole(w) || cufet_cmp(w, cufet_dec_from_ll(0)) < 0) {
        cufet_format_number(buf, sizeof(buf), w);
        cufet_raise(cufet_msgf("a stated width must be a whole, non-negative number of bits, not %s (line %d).", buf, line));
    }
    int stated = cufet_cmp(w, cufet_dec_from_ll(64)) > 0 ? 65 : cufet_to_int(w);
    int needed = cufet_bits_minwidth(x.value);
    if (stated < needed)
        cufet_raise(cufet_msgf("%d bits cannot hold this value - it needs %d (line %d). "
                               "Widening is always fine; narrowing is refused when it would drop a set bit. "
                               "Mask with 'and' if dropping them is what you meant.", stated, needed, line));
    x.width = stated;
    return x;
}

static CufetBits cufet_bits_from_number(CufetDec d, char base_, int line) {
    char buf[64];
    if (!cufet_bits_whole(d)) {
        cufet_format_number(buf, sizeof(buf), d);
        cufet_raise(cufet_msgf("only a whole number can become a bit pattern, and %s is not one (line %d).", buf, line));
    }
    if (d.sign) {
        cufet_format_number(buf, sizeof(buf), d);
        cufet_raise(cufet_msgf("%s is negative, and bit patterns are unsigned (line %d).", buf, line));
    }
    /* Compare against 2^64-1 before narrowing, since the coefficient is 128 bits wide. */
    unsigned __int128 whole = d.coef;
    for (int s = d.scale; s > 0; s--) whole /= 10;
    if (whole > (unsigned __int128)~0ULL) {
        cufet_format_number(buf, sizeof(buf), d);
        cufet_raise(cufet_msgf("%s does not fit in 64 bits (line %d).", buf, line));
    }

    unsigned long long value = (unsigned long long)whole;
    int per = base_ == 'x' ? 4 : (base_ == 'o' ? 3 : 1);
    int min = cufet_bits_minwidth(value);
    if (min < 1) min = 1;
    CufetBits out;
    out.value = value;
    out.base  = base_;
    out.width = (min + per - 1) / per * per;   /* whole digits, no partial leading one */
    return out;
}

/* A caught failure (in an In-case-of-failure handler) — T-agnostic, so one handler works
   regardless of which fallible call's T produced the failure. category NULL = absent. */
typedef struct { const char* message; const char* category; } CufetFailure;

""";

    // Running out of stack. Always emitted: any program can recurse, and the thing this replaces is
    // a program that vanishes without a word. See the block comment inside for why it is CAUGHT
    // rather than predicted — the short version is that every way of predicting it costs more than
    // it is worth, and that was measured rather than reasoned.
    private const string StackGuardRuntime =
"""
/* ── Running out of stack ─────────────────────────────────────────────────────────────────────
   ⚠⚠ A program that recursed past the end of its stack used to VANISH. On Windows: exit code
   0xC00000FD and not one character on either stream. On Linux: a segfault whose only word,
   "Segmentation fault", comes from the SHELL rather than from us — and is gone the moment the
   program is run from anything but an interactive one. The interpreter says what happened; the
   compiled program said nothing at all.

   ★★ It is CAUGHT rather than PREDICTED, and that was measured rather than reasoned. Any per-call
   check has to take the address of a local, and gcc will not reuse the frame of a function whose
   local's address was taken — so any check silently disables tail-call flattening. MEASURED
   2026-09-05, one self-recursive function, gcc -O2, three ways:

       no check at all    flattened into a loop, ran forever on no stack whatsoever
       depth counter      segfault — the counter CAUSED the crash it was meant to report
       headroom check     grew the stack until it tripped — flattening lost

   Both checks turn a program that runs in constant space into one that dies. Asking the operating
   system AFTERWARDS costs nothing per call, so the generated code is byte-for-byte what it was and
   gcc keeps flattening whatever it can.

   ⚠ What that costs instead, and it is not nothing: the message cannot name a function or a line,
   because a signal handler may only call a short list of async-signal-safe things and has no idea
   which Cufet function it was in — and it cannot be caught by `In case of exception`, which the
   interpreted form CAN be. That divergence is deliberate; see DESIGN.md.

   ⚠⚠ Only a fault near THIS THREAD'S OWN STACK is reported this way, and the bounds are read per
   thread because a rabbit runs on its own. Anything else is a genuine bad pointer, and laying a
   calm Cufet sentence over a real crash would be worse than the silence this replaces. When the
   bounds cannot be read, the guard is simply not installed: claiming nothing beats guessing. */
#define CUFET_DEEP_MSG "This program ran out of stack. How deep a program can go depends on where it runs, and this is as far as it could go here.\n"
#if defined(__unix__) || defined(__APPLE__)
#include <signal.h>
#include <unistd.h>
#include <pthread.h>
/* The handler needs a stack of its own — the program's has just run out. Per thread, so a rabbit
   that overflows has somewhere to land too; small, because all it does is write and exit. */
#define CUFET_ALTSTACK 32768
/* The guard region sits just BELOW the mapped stack, so the faulting address is a little past the
   low end rather than inside it. */
#define CUFET_STACK_SLACK 65536
/* ⚠⚠ MALLOC'd, not a `_Thread_local` array, and that is not a style preference. MEASURED
   2026-09-05: as thread-local storage this produced seven AddressSanitizer failures under WSL —
   `failed to deallocate 0x8000 (32768) bytes`, which is this buffer exactly — because a 32 KB
   addition to every thread's static TLS block upsets ASan's teardown. On the heap it is an
   ordinary allocation that ASan understands, and a pthread key hands it back when the thread ends,
   which covers a task that leaves by returning AND one that leaves by longjmp to its pad. */
static pthread_key_t cufet_altstack_key;
static pthread_once_t cufet_altstack_once = PTHREAD_ONCE_INIT;
static _Thread_local char* cufet_stack_lo = 0;
static _Thread_local char* cufet_stack_hi = 0;
static void cufet_drop_altstack(void* block) {
    /* Unregister BEFORE freeing: a signal arriving during teardown must not land on memory that
       has just gone back to the allocator. */
    stack_t off; off.ss_sp = 0; off.ss_size = 0; off.ss_flags = SS_DISABLE;
    sigaltstack(&off, (stack_t*)0);
    free(block);
}
static void cufet_make_altstack_key(void) { pthread_key_create(&cufet_altstack_key, cufet_drop_altstack); }
static void cufet_on_segv(int sig, siginfo_t* info, void* ctx) {
    (void)ctx;
    char* at = info ? (char*)info->si_addr : (char*)0;
    if (cufet_stack_lo && at >= cufet_stack_lo - CUFET_STACK_SLACK && at <= cufet_stack_hi) {
        ssize_t wrote = write(2, CUFET_DEEP_MSG, sizeof(CUFET_DEEP_MSG) - 1);
        (void)wrote;
        _exit(1);
    }
    /* Not ours. Put the default action back and return onto the faulting instruction, so the
       process dies exactly as it would have and a core file still says where. */
    signal(sig, SIG_DFL);
}
static void cufet_watch_stack(void) {
    char* block;
    stack_t ss;
    pthread_once(&cufet_altstack_once, cufet_make_altstack_key);
    block = (char*)malloc(CUFET_ALTSTACK);
    if (!block) return;                       /* no room for a lifeboat — carry on unguarded */
    pthread_setspecific(cufet_altstack_key, block);
    ss.ss_sp = block; ss.ss_size = CUFET_ALTSTACK; ss.ss_flags = 0;
    if (sigaltstack(&ss, (stack_t*)0) != 0) return;
#if defined(__APPLE__)
    {
        char* top = (char*)pthread_get_stackaddr_np(pthread_self());
        size_t size = pthread_get_stacksize_np(pthread_self());
        if (top && size) { cufet_stack_hi = top; cufet_stack_lo = top - size; }
    }
#elif defined(__GLIBC__)
    {
        pthread_attr_t attr; void* addr = 0; size_t size = 0;
        if (pthread_getattr_np(pthread_self(), &attr) == 0) {
            if (pthread_attr_getstack(&attr, &addr, &size) == 0 && addr && size) {
                cufet_stack_lo = (char*)addr;
                cufet_stack_hi = (char*)addr + size;
            }
            pthread_attr_destroy(&attr);
        }
    }
#endif
    if (!cufet_stack_lo) return;   /* bounds unknown — install nothing rather than guess */
    {
        struct sigaction sa; memset(&sa, 0, sizeof(sa));
        sa.sa_sigaction = cufet_on_segv;
        sa.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigaction(SIGSEGV, &sa, (struct sigaction*)0);
        sigaction(SIGBUS, &sa, (struct sigaction*)0);
    }
}
#elif defined(_WIN32)
/* ⚠ LEAN_AND_MEAN and NOMINMAX before windows.h, because RuntimeSplit copies preprocessor lines
   into the HEADER as well — so whatever this drags in is dragged into the generated program too,
   where a stray `min`/`max` macro would collide with generated code. */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
static LONG WINAPI cufet_on_overflow(EXCEPTION_POINTERS* info) {
    if (info && info->ExceptionRecord
        && info->ExceptionRecord->ExceptionCode == (DWORD)EXCEPTION_STACK_OVERFLOW) {
        DWORD wrote = 0;
        WriteFile(GetStdHandle(STD_ERROR_HANDLE), CUFET_DEEP_MSG,
                  (DWORD)(sizeof(CUFET_DEEP_MSG) - 1), &wrote, (LPOVERLAPPED)0);
        /* ⚠ TerminateProcess, not ExitProcess. MEASURED: ExitProcess runs the C runtime's shutdown
           — atexit handlers, stream flushes, DLL detach — on the very stack that just ran out, and
           faults partway through. The message came out and the program then reported 0xC0000005,
           an access violation, so a fixed stack overflow looked like a fresh crash. Terminating
           runs none of that. Nothing is lost: stdout is line-buffered to a console and this path
           is a program ending badly, not a program ending. */
        TerminateProcess(GetCurrentProcess(), 1);
    }
    return EXCEPTION_CONTINUE_SEARCH;   /* anything else dies as it always did */
}
/* One filter for the whole process, so unlike the POSIX side there is nothing per thread. */
static void cufet_watch_stack(void) { SetUnhandledExceptionFilter(cufet_on_overflow); }
#else
static void cufet_watch_stack(void) {}
#endif

""";

    // Text runtime. Text is `const char*` and immutable — every operation allocates a fresh
    // result in the current arena (freed at Done.); literals stay static. Trim/parse are
    // ASCII/invariant (matching the interpreter for ASCII input); CASING IS NOT HERE — it needs a
    // Unicode table, so it lives in the gated CaseRuntime below and is emitted only when used.
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
/* ── Character positions ──────────────────────────────────────────────────────────────────
   A Cufet character position is a UNICODE CODE POINT, on both backends. Text is stored here as
   UTF-8, so a position is NOT a byte: "héllo" is five characters in six bytes. Counting bytes
   made the compiled program disagree with the interpreted one on every non-ASCII string, which
   the no-divergence rule forbids outright.

   UTF-8 makes the arithmetic cheap. Exactly one byte of each character has a top bit pattern
   other than 10xxxxxx, so counting characters is counting the bytes that are not continuations,
   and no decoding table is needed. See TextPositions in the interpreter for the UTF-16 half of
   the same agreement, and for why the unit is code points and not grapheme clusters. */
static int cufet_u8_len(const char* s) {
    int n = 0;
    for (const unsigned char* p = (const unsigned char*)s; *p; p++)
        if ((*p & 0xC0) != 0x80) n++;
    return n;
}
/* Byte offset at which character number `index` begins, counting from zero. An index at or past
   the end returns the byte length, so a caller can use it as an exclusive bound unguarded. */
static int cufet_u8_offset(const char* s, int index) {
    const unsigned char* p = (const unsigned char*)s;
    int i = 0, n = 0;
    while (p[i] && n < index) {
        i++;
        while ((p[i] & 0xC0) == 0x80) i++;   /* skip this character's continuation bytes */
        n++;
    }
    return i;
}
/* The character index containing byte offset `off` — the inverse, for turning a byte-wise
   search result back into a position someone can hand to `the characters from`. */
static int cufet_u8_index(const char* s, int off) {
    const unsigned char* p = (const unsigned char*)s;
    int n = 0;
    for (int i = 0; i < off && p[i]; i++)
        if ((p[i] & 0xC0) != 0x80) n++;
    return n;
}
static const char* cufet_str_range(const char* s, int from1, int to1, int line) {
    if (from1 <= 0) cufet_raise(cufet_msgf("a character position must be 1 or greater — positions start at 1 (line %d).", line));
    int len = cufet_u8_len(s);
    if (to1 < 0 || to1 > len) to1 = len;      /* to1 < 0 sentinel = to end; clamp high */
    int length = to1 - from1 + 1;              /* 1-based inclusive */
    if (length <= 0) return "";
    int from_b = cufet_u8_offset(s, from1 - 1);
    int to_b   = cufet_u8_offset(s, to1);
    return cufet_str_substr(s, from_b, to_b - from_b);
}
static const char* cufet_str_edge(const char* s, int count, int from_start) {
    int len = cufet_u8_len(s);
    int c = count < 0 ? 0 : (count > len ? len : count);
    if (from_start) return cufet_str_substr(s, 0, cufet_u8_offset(s, c));
    int from_b = cufet_u8_offset(s, len - c);
    return cufet_str_substr(s, from_b, (int)strlen(s) - from_b);
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
    /* The SEARCH is byte-wise and that is correct — UTF-8 is self-synchronising, so one
       character's bytes can never occur inside another and a byte match is a character match.
       Only the POSITION it reports has to be converted, from bytes to characters. */
    const char* p = strstr(text, sub);
    return p ? cufet_u8_index(text, (int)(p - text)) + 1 : 0;   /* 1-based; 0 = not found */
}
/* Splits s on each non-overlapping occurrence of delim, keeping empty parts (C# string.Split
   with StringSplitOptions.None): N hits -> N+1 arena-allocated substrings, written to *out.
   Delimiter-not-found -> one part (the whole string); "" -> one empty part. */
static int cufet_str_split(const char* s, const char* delim, const char*** out, int line) {
    size_t dl = strlen(delim);
    if (dl == 0) cufet_raise(cufet_msgf("'split by' needs a non-empty delimiter (line %d).", line));
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
static const char* cufet_str_replace(const char* s, const char* olds, const char* news, int line) {
    size_t lo = strlen(olds);
    if (lo == 0) cufet_raise(cufet_msgf("'replace' needs a non-empty target (line %d).", line));
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

/* ── Current directory ──────────────────────────────────────────────────────
   `the current directory` → voidable text; void only when the process has no working
   directory to report, which in practice means it was removed underneath it.
   `The current directory becomes <p>.` → fallible statement.

   ★ The stat() checks run BEFORE chdir(), and that ordering is load-bearing for matching the
   interpreter. .NET collapses "no such directory" and "that is a file" into a single
   IOException, so the interpreter must test existence itself; relying on errno here instead
   would diverge on Windows, where _chdir onto a file reports ENOENT rather than ENOTDIR.
   Checking the same way on both sides is what makes the failure CATEGORY agree everywhere. */
#include <unistd.h>
static const char* cufet_getcwd(void) {
    /* Grown rather than fixed at PATH_MAX: a truncated answer would be a silent divergence from
       the interpreter, which has no length ceiling. Superseded buffers stay in the arena and die
       with it, and the loop runs a handful of times at most. */
    size_t cap = 512;
    for (;;) {
        char* buf = (char*)cufet_arena_alloc(cap);
        if (getcwd(buf, cap)) return buf;
        if (errno != ERANGE || cap > (1u << 20)) return NULL;
        cap *= 2;
    }
}
static int cufet_chdir(const char* path, CufetFailure* err) {
    struct stat st;
    if (stat(path, &st) != 0) {
        err->category = "not-found";
        err->message  = cufet_arena_msg("the directory '%s' was not found", path);
        return 0;
    }
    if (!S_ISDIR(st.st_mode)) {
        err->category = "not-a-directory";
        err->message  = cufet_arena_msg("'%s' is not a directory", path);
        return 0;
    }
    if (chdir(path) != 0) {
        if (errno == EACCES || errno == EPERM) {
            err->category = "permission-denied";
            err->message  = cufet_arena_msg("permission denied entering directory '%s'", path);
        } else {
            err->category = "disk-error";
            err->message  = cufet_arena_msg("changing to the directory '%s' failed", path);
        }
        return 0;
    }
    return 1;
}

/* ── Directory contents (cleanup slice) ─────────────────────────────────────
   `the contents of the directory <p>` → SORTED (ordinal, strcmp) full paths "<p><sep><name>",
   skipping "." / "..". Both backends sort: the raw OS order is filesystem-dependent, so sorting
   defines the undefined (the FormatRecord normalization move). The separator is the PLATFORM's
   (matching .NET on the same platform); a trailing separator on the input is not doubled. */
#include <dirent.h>
static CufetFailure cufet_dir_failure(const char* path, int e) {
    CufetFailure f;
    if (e == ENOENT) {
        f.category = "not-found";
        f.message  = cufet_arena_msg("the directory '%s' was not found", path);
    } else if (e == EACCES || e == EPERM) {
        f.category = "permission-denied";
        f.message  = cufet_arena_msg("permission denied reading directory '%s'", path);
    } else {
        f.category = "disk-error";
        f.message  = cufet_arena_msg("reading the directory '%s' failed", path);
    }
    return f;
}
static int cufet_dir_cmp(const void* a, const void* b) { return strcmp(*(const char* const*)a, *(const char* const*)b); }
static int cufet_dir_contents(const char* path, const char*** out_items, int* out_n, CufetFailure* err) {
    DIR* d = opendir(path);
    if (!d) { *err = cufet_dir_failure(path, errno); return 0; }
#ifdef _WIN32
    const char sep = '\\';
#else
    const char sep = '/';
#endif
    size_t plen = strlen(path);
    int hasSep = plen > 0 && (path[plen - 1] == '/' || path[plen - 1] == '\\');
    int n = 0, cap = 16;
    const char** items = (const char**)cufet_arena_alloc((size_t)cap * sizeof(char*));
    struct dirent* de;
    while ((de = readdir(d)) != NULL) {
        if (strcmp(de->d_name, ".") == 0 || strcmp(de->d_name, "..") == 0) continue;
        if (n == cap) {
            cap *= 2;
            const char** ni = (const char**)cufet_arena_alloc((size_t)cap * sizeof(char*));
            memcpy(ni, items, (size_t)n * sizeof(char*));
            items = ni;
        }
        size_t nl = strlen(de->d_name);
        char* full = (char*)cufet_arena_alloc(plen + (hasSep ? 0 : 1) + nl + 1);
        memcpy(full, path, plen);
        if (!hasSep) full[plen] = sep;
        memcpy(full + plen + (hasSep ? 0 : 1), de->d_name, nl + 1);
        items[n] = full; n++;
    }
    closedir(d);
    qsort(items, (size_t)n, sizeof(char*), cufet_dir_cmp);
    *out_items = items; *out_n = n;
    return 1;
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
/* ⚠⚠ Reap a child across an INTERRUPT. The SIGINT handler is installed with no SA_RESTART, so a
   Ctrl-C makes waitpid return -1/EINTR — and the old code ignored the return value entirely,
   reporting the launch as finished while the child was very possibly still alive. Retrying is
   also what makes a shell work: the terminal signalled the child too, so the next wait reaps it
   and the parent carries on to its own interrupt checkpoint instead of being torn down there. */
static void cufet_wait_for(pid_t pid, int* st) {
    while (waitpid(pid, st, 0) < 0 && errno == EINTR) { }
}
/* `run <program>.` as a STATEMENT — the child INHERITS this process’s stdio.

   ★★ The whole difference from cufet_run_capture is the pipes that are not here. No dup2, so the
   child keeps fds 0/1/2: its output streams live rather than arriving all at once when it exits,
   and a program that asks stdout "what terminal are you?" gets a real answer. Capturing and then
   discarding could never have produced either — by then the child has already been handed a pipe.

   ⚠ The exec-status pipe stays. It is the only way to tell "the program does not exist" from "the
   program ran and failed", and a launch failure is a Cufet failure while a nonzero exit is not. */
/* ★★ ONE derivation of `exit-code`, read by both launch paths, so the capturing form and the
   terminal form cannot drift apart on what a child's exit means. 128 + the signal number for a
   child that was KILLED is the universal shell convention — Ctrl-C gives 130 — and it keeps
   exit-code a plain number with nothing voidable: killed codes are >= 129, ordinary exits are
   0-128, so a caller who cares can tell which happened.

   ⚠ -1 is the third case: neither exited nor signalled. Reachable in principle (a stopped child
   reported through a wait this code does not ask for), not in practice here. It is a number a
   program can see, so it is documented rather than left to be discovered. */
static int cufet_exit_status(int st) {
    return WIFEXITED(st) ? WEXITSTATUS(st) : (WIFSIGNALED(st) ? 128 + WTERMSIG(st) : -1);
}
/* ⚠ out_exit may be NULL: the launching STATEMENT has nowhere to put an exit code, and says so by
   not asking for one. The expression form always asks. */
static int cufet_run_inherit(const char* program, char* const argv[], CufetFailure* err, int* out_exit) {
    /* ⚠⚠ FLUSHED BEFORE THE FORK, and this is not tidiness. The child writes straight to fd 1 while
       anything this program has printed may still be sitting in stdio’s buffer — so without it the
       child’s output OVERTAKES text that was printed first. Measured: `State "before".` then a
       launch printed the child’s line above "before". Only visible when stdout is not a terminal,
       which is exactly how the test harness runs it. */
    fflush(NULL);
    int xp[2];
    if (pipe(xp) < 0) { *err = cufet_launch_failure(program, EIO); return 0; }
    fcntl(xp[1], F_SETFD, FD_CLOEXEC);   /* exec closes it → parent reads EOF = exec ok */
    pid_t pid = fork();
    if (pid < 0) { close(xp[0]); close(xp[1]); *err = cufet_launch_failure(program, EIO); return 0; }
    if (pid == 0) {
        close(xp[0]);
        execvp(program, argv);
        int e = errno; ssize_t w = write(xp[1], &e, sizeof(e)); (void)w; _exit(127);
    }
    close(xp[1]);
    int child_errno = 0;
    ssize_t xn = read(xp[0], &child_errno, sizeof(child_errno));
    close(xp[0]);
    if (xn > 0) {   /* exec failed in the child → launch failure */
        int st; cufet_wait_for(pid, &st);
        *err = cufet_launch_failure(program, child_errno);
        return 0;
    }
    int st; cufet_wait_for(pid, &st);
    if (out_exit) *out_exit = cufet_exit_status(st);
    return 1;
}

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
        int st; cufet_wait_for(pid, &st);
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
    int st; cufet_wait_for(pid, &st);
    *out_exit = cufet_exit_status(st);
    char* os = (char*)cufet_arena_alloc(ol + 1); memcpy(os, ob, ol); os[ol] = '\0';
    char* es = (char*)cufet_arena_alloc(el + 1); memcpy(es, eb, el); es[el] = '\0';
    free(ob); free(eb);
    *out_stdout = os; *out_stderr = es;
    return 1;
}
#endif

""";

    // ── chase: a mutable character buffer (the `collections` book) ─────────────
    //
    // ★★ UTF-32 INSIDE, UTF-8 at the edges. Cufet text is UTF-8, where a character is one to four
    // bytes, so `item n of` over it would have to walk from the start. Storing code points
    // fixed-width makes that a subscript, which is the whole reason the type exists — and it is
    // also what makes the two backends agree, because the interpreter stores code points too. The
    // cost is 4× memory on a thing you build and discard, which is where that trade is cheapest.
    //
    // ⚠ Arena-allocated and never freed by hand: a chase is a reference type that dies with its
    // rabbit, exactly as a series or a map does.
    private const string ChaseRuntime =
"""
typedef struct { int32_t* data; int len; int cap; } CufetChase;

static CufetChase* cufet_chase_new(void) {
    CufetChase* c = (CufetChase*)cufet_arena_alloc(sizeof(CufetChase));
    c->len = 0; c->cap = 16;
    c->data = (int32_t*)cufet_arena_alloc(sizeof(int32_t) * (size_t)c->cap);
    return c;
}

static void cufet_chase_push(CufetChase* c, int32_t point) {
    if (c->len == c->cap) {
        int grown = c->cap * 2;
        int32_t* moved = (int32_t*)cufet_arena_alloc(sizeof(int32_t) * (size_t)grown);
        memcpy(moved, c->data, sizeof(int32_t) * (size_t)c->len);
        c->data = moved; c->cap = grown;
    }
    c->data[c->len++] = point;
}

/* Appends every character of a UTF-8 text. A malformed byte is taken as one character rather
   than rejected: the lexer only ever hands over well-formed text, and a buffer is the wrong
   place to discover otherwise. */
static void cufet_chase_append(CufetChase* c, const char* text) {
    const unsigned char* p = (const unsigned char*)text;
    while (*p) {
        int32_t point; int extra;
        if      (*p < 0x80) { point = *p;        extra = 0; }
        else if ((*p & 0xE0) == 0xC0) { point = *p & 0x1F; extra = 1; }
        else if ((*p & 0xF0) == 0xE0) { point = *p & 0x0F; extra = 2; }
        else if ((*p & 0xF8) == 0xF0) { point = *p & 0x07; extra = 3; }
        else { point = *p; extra = 0; }
        p++;
        for (int i = 0; i < extra && (*p & 0xC0) == 0x80; i++) { point = (point << 6) | (*p & 0x3F); p++; }
        cufet_chase_push(c, point);
    }
}

/* How many bytes one code point needs in UTF-8. */
static int cufet_utf8_width(int32_t point) {
    if (point < 0x80) return 1;
    if (point < 0x800) return 2;
    if (point < 0x10000) return 3;
    return 4;
}

static char* cufet_utf8_put(char* out, int32_t point) {
    if (point < 0x80) { *out++ = (char)point; return out; }
    if (point < 0x800) {
        *out++ = (char)(0xC0 | (point >> 6));
        *out++ = (char)(0x80 | (point & 0x3F));
        return out;
    }
    if (point < 0x10000) {
        *out++ = (char)(0xE0 | (point >> 12));
        *out++ = (char)(0x80 | ((point >> 6) & 0x3F));
        *out++ = (char)(0x80 | (point & 0x3F));
        return out;
    }
    *out++ = (char)(0xF0 | (point >> 18));
    *out++ = (char)(0x80 | ((point >> 12) & 0x3F));
    *out++ = (char)(0x80 | ((point >> 6) & 0x3F));
    *out++ = (char)(0x80 | (point & 0x3F));
    return out;
}

/* The explicit COPY `converted to text` makes. The buffer lives on, independent. */
static const char* cufet_chase_text(CufetChase* c) {
    size_t need = 1;
    for (int i = 0; i < c->len; i++) need += (size_t)cufet_utf8_width(c->data[i]);
    char* out = (char*)cufet_arena_alloc(need);
    char* at = out;
    for (int i = 0; i < c->len; i++) at = cufet_utf8_put(at, c->data[i]);
    *at = 0;
    return out;
}

/* Printed as the COLLECTION it is — `(h, e, l, l, o)` — never as the text it will become. */
/* Sets one position, and REFUSES a text that is not exactly one character.

   ⚠ A length check at run time rather than a type error, because a text's length is not known
   until it exists. The alternative — taking the first character and dropping the rest — is the
   silent resolution this language refuses everywhere else; setting one position to "abc" is a
   mistake, not an abbreviation. Insert is the operation that takes however many. */
static void cufet_chase_set(CufetChase* c, int index1, const char* one, int line) {
    CufetChase* scratch = cufet_chase_new();
    cufet_chase_append(scratch, one);
    if (scratch->len != 1)
        cufet_raise(cufet_msgf(
            "Setting one position needs exactly one character, and \"%s\" is %d. This happened on line %d.",
            one, scratch->len, line));
    c->data[index1 - 1] = scratch->data[0];
}

/* Takes one character out and closes the gap. -1 means the last, matching the series form. */
static void cufet_chase_remove_at(CufetChase* c, long long index1, const char* name, int line) {
    int at = (index1 < 0)
        ? (int)cufet_last_check(c->len, name, line)
        : (int)cufet_idx_check(index1, c->len, name, line);
    memmove(&c->data[at - 1], &c->data[at], sizeof(int32_t) * (size_t)(c->len - at));
    c->len--;
}

/* One character out, as the one-character TEXT the language calls a character. Four bytes plus a
   terminator is the most any code point needs. */
static const char* cufet_chase_at(CufetChase* c, int index1) {
    char* out = (char*)cufet_arena_alloc(5);
    char* at = cufet_utf8_put(out, c->data[index1 - 1]);
    *at = 0;
    return out;
}

/* Structural, matching the interpreter: a buffer of the same characters is the same buffer. */
static int cufet_chase_eq(CufetChase* a, CufetChase* b) {
    if (a == b) return 1;
    if (!a || !b || a->len != b->len) return 0;
    for (int i = 0; i < a->len; i++) if (a->data[i] != b->data[i]) return 0;
    return 1;
}

static const char* cufet_chase_show(CufetChase* c) {
    size_t need = 3;
    for (int i = 0; i < c->len; i++) need += (size_t)cufet_utf8_width(c->data[i]) + 2;
    char* out = (char*)cufet_arena_alloc(need);
    char* at = out;
    *at++ = 40;
    for (int i = 0; i < c->len; i++) {
        if (i > 0) { *at++ = 44; *at++ = 32; }
        at = cufet_utf8_put(at, c->data[i]);
    }
    *at++ = 41;
    *at = 0;
    return out;
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
    if (r < 1 || r > m->rows) cufet_raise(cufet_msgf("Row index %lld is out of range — this matrix has %d row(s) (line %d).", r, m->rows, line));
    if (c < 1 || c > m->cols) cufet_raise(cufet_msgf("Column index %lld is out of range — this matrix has %d column(s) (line %d).", c, m->cols, line));
    return m->data[(r - 1) * m->cols + (c - 1)];
}
/* The write half. Same bounds, same messages — a matrix is a pointer, so this mutates every
   binding that names it, exactly as the interpreter's shared decimal[] does. */
static void cufet_mat_set(CufetMatrix* m, long long r, long long c, CufetDec v, int line) {
    if (r < 1 || r > m->rows) cufet_raise(cufet_msgf("Row index %lld is out of range — this matrix has %d row(s) (line %d).", r, m->rows, line));
    if (c < 1 || c > m->cols) cufet_raise(cufet_msgf("Column index %lld is out of range — this matrix has %d column(s) (line %d).", c, m->cols, line));
    m->data[(r - 1) * m->cols + (c - 1)] = v;
}
/* `a matrix of R by C [filled with F]` — runtime validation for non-literal dimensions
   (literals are rejected statically by the typechecker), matching the interpreter's messages. */
static CufetMatrix* cufet_mat_sized(CufetDec rd, CufetDec cd, CufetDec fill, int line) {
    long long r = cufet_to_int(rd), c = cufet_to_int(cd);
    if (cufet_cmp(rd, cufet_dec_from_ll(r)) != 0 || r < 1) cufet_raise(cufet_msgf("Matrix row count must be a positive whole number, but got %s (line %d).", cufet_text_from_dec(rd), line));
    if (cufet_cmp(cd, cufet_dec_from_ll(c)) != 0 || c < 1) cufet_raise(cufet_msgf("Matrix column count must be a positive whole number, but got %s (line %d).", cufet_text_from_dec(cd), line));
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
/* Scaling: every element times a scalar. Returns non-NULL always — there are no dimensions to
   disagree, which is why the emit site does not wrap it in a failable the way the others are. */
static CufetMatrix* cufet_mat_scale(CufetMatrix* a, CufetDec f) {
    CufetMatrix* m = cufet_mat_new(a->rows, a->cols);
    for (int i = 0; i < a->rows * a->cols; i++) m->data[i] = cufet_mul(a->data[i], f);
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
    if (cufet_cmp(low, high) > 0)
        cufet_raise(cufet_msgf("Random number range is invalid: low (%s) is greater than high (%s) (line %d).",
                    cufet_text_from_dec(low), cufet_text_from_dec(high), line));
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
/* The landing pad is established the same way everywhere; only the FLAVOUR of setjmp differs.
   sigsetjmp/siglongjmp save and restore the signal mask, which is what makes the unwind safe from
   a signal-interrupted checkpoint — and which mingw has no notion of. */
#define CUFET_SETJMP(b) sigsetjmp((b), 1)
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
/* mingw: no sigaction and no signal mask, so Ctrl-C keeps its default (terminate) and the
   checkpoint never unwinds — `cufet_interrupted` is never set. The landing pad still EXISTS,
   because the task and pipe machinery establishes one unconditionally; here setjmp always returns
   0 and the body simply runs. Declaring it is what lets threads compile on a platform that has
   pthreads (mingw-w64 ships winpthreads) but not POSIX signals. */
#include <setjmp.h>
/* ⚠ The no-unwind form, like the exception pad. Nothing longjmps to this pad on mingw today —
   cufet_interrupted is never set — but a pad established with a bare `setjmp` is a crash waiting
   for the day something does jump to it, and the two pads should not differ in a way nobody
   intended. See CUFET_PLAIN_SETJMP. */
#define CUFET_SETJMP(b) CUFET_PLAIN_SETJMP(b)
static volatile int cufet_interrupted = 0;
static _Thread_local jmp_buf cufet_thread_top;
static _Thread_local int cufet_pad_set = 0;
static void cufet_install_sigint(void) {}
static void cufet_checkpoint(void) {}
#endif

""";

    // Concurrency runtime (CONC.A+B): pthreads + a thread-safe channel (mutex + condvar). Emitted
    // only when tasks/channels are used; POSIX-guarded (Linux-targeted, WSL-verified). The channel is
    // ONE type-erased container (like the interpreter's single ChannelValue holding `object`s): each
    // node carries a `void*` to a malloc'd, fully-heap-owned envelope of the element value (built by
    // the per-element-type `cchan_<T>_heapenv` deep-copy on send). Recv hands the envelope back; the
    // caller arena-copies it in + frees it. Teardown of un-received nodes frees each envelope via the
    // channel's `freeval` (installed at creation from the element type) — so channel-of-T is race-free
    // and leak-free by the same construction as the number-only A+B channel, for arbitrary T.
    private const string ConcurrencyRuntime =
"""
#if defined(__unix__) || defined(__APPLE__) || defined(__MINGW32__)
#include <pthread.h>
#include <time.h>
#define CUFET_TASK_MAX 4096
typedef struct cufet_chan_node { void* val; struct cufet_chan_node* next; } cufet_chan_node;
typedef struct { pthread_mutex_t m; pthread_cond_t c; cufet_chan_node* head; cufet_chan_node* tail; int closed; void (*freeval)(void*); } cufet_chan;
/* Live-channel registry — so an interrupt unwind (CONC.E) can free channels the longjmp jumped past.
   A normal cufet_chan_free unregisters; the interrupt teardown frees whatever is still registered. */
static cufet_chan* cufet_live_chans[CUFET_TASK_MAX];
static int cufet_nlive = 0;
static pthread_mutex_t cufet_live_m = PTHREAD_MUTEX_INITIALIZER;
static cufet_chan* cufet_chan_new(void (*freeval)(void*)) {
    cufet_chan* ch = (cufet_chan*)malloc(sizeof(cufet_chan));
    pthread_mutex_init(&ch->m, NULL); pthread_cond_init(&ch->c, NULL);
    ch->head = ch->tail = NULL; ch->closed = 0; ch->freeval = freeval;
    pthread_mutex_lock(&cufet_live_m); if (cufet_nlive < CUFET_TASK_MAX) cufet_live_chans[cufet_nlive++] = ch; pthread_mutex_unlock(&cufet_live_m);
    return ch;
}
/* Enqueues a heap envelope (already a self-contained deep copy of the element — no arena pointers). */
static void cufet_chan_send(cufet_chan* ch, void* env) {
    cufet_chan_node* n = (cufet_chan_node*)malloc(sizeof(cufet_chan_node));
    n->val = env; n->next = NULL;
    pthread_mutex_lock(&ch->m);
    if (ch->tail) ch->tail->next = n; else ch->head = n; ch->tail = n;
    pthread_cond_signal(&ch->c); pthread_mutex_unlock(&ch->m);
}
/* Blocking receive → 1 with *out set to the heap envelope if a value is available, 0 if the channel
   is empty-and-closed (→ Cufet void), -1 if a SIGINT arrived while blocked (CONC.E — the caller runs
   a checkpoint). The wait is a 50ms timed-wait loop so a blocked worker re-checks the interrupt flag
   (true-preemptive: a real pthread_cond_wait can be woken by a signal). Frees the node (not the
   envelope — the caller arena-copies from it, then frees it via cchan_<T>_freeenv). */
static int cufet_chan_recv(cufet_chan* ch, void** out) {
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
/* ── A named task's result box ───────────────────────────────────────────────
   The task publishes its result envelope here exactly once; any number of awaiters — the rabbit
   body, other tasks, or the same awaiter twice — wait for it and deep-copy into their own arena.

   ★ Nobody joins at an await site. pthread_join happens once, in the rabbit's Done. teardown,
   which the structured guarantee requires anyway. That is what makes N awaiters safe BY
   CONSTRUCTION: a check-then-join guard is only sound while exactly one thread may run it, and
   `the awaited result of x` can now appear in several tasks at once.

   The box owns the envelope until teardown frees it through `freeenv`, so awaiters only ever
   read it. Awaiters copy rather than share because arenas are thread-local — each one needs the
   value in its own. */
typedef struct {
    pthread_mutex_t m;
    pthread_cond_t  c;
    int    done;                  /* published (a NULL env means the task was abandoned) */
    void*  env;                   /* malloc'd result envelope, owned by the box */
    void (*freeenv)(void*);       /* per-element-type deep free, recorded at spawn */
} cufet_rbox;

static cufet_rbox* cufet_rbox_new(void (*freeenv)(void*)) {
    cufet_rbox* b = (cufet_rbox*)malloc(sizeof(cufet_rbox));
    pthread_mutex_init(&b->m, NULL);
    pthread_cond_init(&b->c, NULL);
    b->done = 0; b->env = NULL; b->freeenv = freeenv;
    return b;
}
static void cufet_rbox_publish(cufet_rbox* b, void* env) {
    if (!b) { if (env) free(env); return; }
    pthread_mutex_lock(&b->m);
    b->env = env; b->done = 1;
    pthread_cond_broadcast(&b->c);
    pthread_mutex_unlock(&b->m);
}
/* Returns the envelope, still owned by the box. Waits on a 50ms poll for the same reason
   cufet_chan_recv does: a thread blocked in an untimed wait cannot notice SIGINT, and INT.1
   made every blocking point interruptible. NULL means abandoned — the caller checkpoints. */
static void* cufet_rbox_await(cufet_rbox* b) {
    pthread_mutex_lock(&b->m);
    while (!b->done) {
        if (cufet_interrupted) { pthread_mutex_unlock(&b->m); return NULL; }
        struct timespec ts; clock_gettime(CLOCK_REALTIME, &ts);
        ts.tv_nsec += 50000000L; if (ts.tv_nsec >= 1000000000L) { ts.tv_sec++; ts.tv_nsec -= 1000000000L; }
        pthread_cond_timedwait(&b->c, &b->m, &ts);
    }
    void* e = b->env;
    pthread_mutex_unlock(&b->m);
    return e;
}
static void cufet_rbox_free(cufet_rbox* b) {
    if (!b) return;
    if (b->env) { if (b->freeenv) b->freeenv(b->env); else free(b->env); }
    pthread_mutex_destroy(&b->m);
    pthread_cond_destroy(&b->c);
    free(b);
}
static void cufet_chan_close(cufet_chan* ch) {
    pthread_mutex_lock(&ch->m); ch->closed = 1; pthread_cond_broadcast(&ch->c); pthread_mutex_unlock(&ch->m);
}
static void cufet_chan_free(cufet_chan* ch) {   /* frees un-received envelopes (teardown/close-with-pending) */
    pthread_mutex_lock(&cufet_live_m);
    for (int i = 0; i < cufet_nlive; i++) if (cufet_live_chans[i] == ch) { cufet_live_chans[i] = cufet_live_chans[--cufet_nlive]; break; }
    pthread_mutex_unlock(&cufet_live_m);
    cufet_chan_node* n = ch->head; while (n) { cufet_chan_node* x = n->next; if (ch->freeval) ch->freeval(n->val); free(n); n = x; }
    pthread_mutex_destroy(&ch->m); pthread_cond_destroy(&ch->c); free(ch);
}
/* Interrupt-teardown helper: free every still-live channel (the unwind longjmp'd past their frees). */
static void cufet_free_all_chans(void) {
    pthread_mutex_lock(&cufet_live_m);
    while (cufet_nlive > 0) {
        cufet_chan* ch = cufet_live_chans[--cufet_nlive];
        cufet_chan_node* n = ch->head; while (n) { cufet_chan_node* x = n->next; if (ch->freeval) ch->freeval(n->val); free(n); n = x; }
        pthread_mutex_destroy(&ch->m); pthread_cond_destroy(&ch->c); free(ch);
    }
    pthread_mutex_unlock(&cufet_live_m);
}
/* Exception-unwind helper (E-prime): free channels registered AFTER a Try-entry snapshot — the
   longjmp jumped past their rabbit teardown. Freeing from the top preserves snapshot indexing
   (cufet_chan_free swap-removes; removing the last entry is a plain pop). */
static void cufet_free_chans_from(int n) {
    while (cufet_nlive > n) cufet_chan_free(cufet_live_chans[cufet_nlive - 1]);
}
/* Rabbit teardown after a caught exception may see channels ALREADY freed at the catch — free
   only if still registered (idempotent teardown; no double-free). */
static void cufet_chan_free_if_live(cufet_chan* ch) {
    pthread_mutex_lock(&cufet_live_m);
    int live = 0;
    for (int i = 0; i < cufet_nlive; i++) if (cufet_live_chans[i] == ch) { live = 1; break; }
    pthread_mutex_unlock(&cufet_live_m);
    if (live) cufet_chan_free(ch);
}
/* Task pipes (CONC.D): each stage runs as its own thread connected by channels. `output <v>` and
   `for each … from the input` inside a stage read these THREAD-LOCAL implicit channels — mirroring
   the interpreter's per-stage _pipeOutputChan / _pipeInputChan, but thread-local so concurrent
   stages don't clash. A stage closes its output channel when its function returns → downstream sees
   the stream complete (recv returns void on empty-and-closed). All values cross the SAME heap-bridged
   channel boundary as A+B, so inter-stage streaming is race-free by the same construction. */
static _Thread_local cufet_chan* cufet_pipe_in;
static _Thread_local cufet_chan* cufet_pipe_out;
/* A stage is a closure value: fn takes the captured env (NULL for a plain named-function stage, whose
   fn is an env-ignoring thunk). The env is allocated in the pipe's creating scope, which blocks on the
   join, so sharing it read-only with the stage thread is race-free (value captures are immutable). */
typedef struct { cufet_chan* in; cufet_chan* out; void (*fn)(void* env); void* env; } cufet_pipe_arg;
static void* cufet_pipe_stage(void* argp) {
    cufet_pipe_arg* a = (cufet_pipe_arg*)argp;
    cufet_pipe_in = a->in; cufet_pipe_out = a->out;
    cufet_arena_push();
    /* Interrupt landing pad (CONC.E): if a blocked recv inside the stage is interrupted, it unwinds
       to here and the stage tears down normally (arena pop + close output + reaped by the pipe join). */
    if (CUFET_SETJMP(cufet_thread_top) == 0) { cufet_pad_set = 1; a->fn(a->env); }
    /* INT.1: run this thread's pending unmakers + close its open files before tearing down. Both
       registries are _Thread_local, so this touches only the stage's own. On the interrupt path
       these would otherwise be skipped entirely (destructors never fire, buffered writes lost). */
    cufet_run_unmakers_to(0);
    cufet_close_files_from(0);
    cufet_arena_pop();
    if (a->out) cufet_chan_close(a->out);      /* signal downstream: no more values */
    free(a);
    return NULL;
}
#endif

""";
}
