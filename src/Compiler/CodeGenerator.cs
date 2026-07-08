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

    // ── Emitted C runtime ─────────────────────────────────────────────────
    // Self-contained: compiles with plain `gcc file.c`, no external libraries.
    // The software decimal (CufetDec) is bit-identical to .NET System.Decimal:
    //   value = (sign ? -1 : 1) * coef * 10^-scale,  coef <= 2^96-1,  scale in [0,28]
    // Precision-overflow (multiply, division) rounds half-to-even, exactly as
    // measured against the interpreter's decimal. u256 (four 64-bit limbs) carries
    // the up-to-192-bit intermediate products and scaled division numerators.
    private const string RuntimePreamble =
"""
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

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

""";

    public string Generate(Program program)
    {
        var sb = new StringBuilder();

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
        sb.AppendLine("static CufetArena cufet_arenas[CUFET_ARENA_MAX_DEPTH];");
        sb.AppendLine("static int cufet_arena_top = -1;");
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

        // ── Series runtime (series of number only for this slice) ─────────
        // CufetSeries: struct in arena; data buffer in arena (re-allocated on
        // growth — old buffer stays in ptrs, freed at arena_pop). Elements are
        // CufetDec so series-of-number is exact decimal, like scalars.
        sb.AppendLine("typedef struct { CufetDec* data; int len; int cap; } CufetSeries;");
        sb.AppendLine();
        sb.AppendLine("static CufetSeries* cufet_series_new(void) {");
        sb.AppendLine("    CufetSeries* s = (CufetSeries*)cufet_arena_alloc(sizeof(CufetSeries));");
        sb.AppendLine("    s->data = NULL; s->len = 0; s->cap = 0;");
        sb.AppendLine("    return s;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_series_ensure(CufetSeries* s) {");
        sb.AppendLine("    if (s->len >= s->cap) {");
        sb.AppendLine("        int nc = s->cap == 0 ? 4 : s->cap * 2;");
        sb.AppendLine("        CufetDec* nd = (CufetDec*)cufet_arena_alloc((size_t)nc * sizeof(CufetDec));");
        sb.AppendLine("        if (s->len > 0) memcpy(nd, s->data, (size_t)s->len * sizeof(CufetDec));");
        sb.AppendLine("        s->data = nd; s->cap = nc;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_series_append(CufetSeries* s, CufetDec v) {");
        sb.AppendLine("    cufet_series_ensure(s);");
        sb.AppendLine("    s->data[s->len++] = v;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_series_prepend(CufetSeries* s, CufetDec v) {");
        sb.AppendLine("    cufet_series_ensure(s);");
        sb.AppendLine("    if (s->len > 0) memmove(s->data + 1, s->data, (size_t)s->len * sizeof(CufetDec));");
        sb.AppendLine("    s->data[0] = v; s->len++;");
        sb.AppendLine("}");
        sb.AppendLine();
        // Inserts v after 1-based position after1 (already the correct 0-based insertion index).
        sb.AppendLine("static void cufet_series_insert(CufetSeries* s, int after1, CufetDec v) {");
        sb.AppendLine("    cufet_series_ensure(s);");
        sb.AppendLine("    int pos = after1;");
        sb.AppendLine("    if (s->len > pos) memmove(s->data + pos + 1, s->data + pos, (size_t)(s->len - pos) * sizeof(CufetDec));");
        sb.AppendLine("    s->data[pos] = v; s->len++;");
        sb.AppendLine("}");
        sb.AppendLine();
        // idx1: 1-based; pass -1 for "last".
        sb.AppendLine("static void cufet_series_remove_at(CufetSeries* s, int idx1) {");
        sb.AppendLine("    int idx = (idx1 < 0) ? s->len - 1 : idx1 - 1;");
        sb.AppendLine("    if (s->len - idx - 1 > 0) memmove(s->data + idx, s->data + idx + 1, (size_t)(s->len - idx - 1) * sizeof(CufetDec));");
        sb.AppendLine("    s->len--;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_series_remove_value(CufetSeries* s, CufetDec v) {");
        sb.AppendLine("    for (int i = 0; i < s->len; i++) {");
        sb.AppendLine("        if (cufet_cmp(s->data[i], v) == 0) {");
        sb.AppendLine("            if (s->len - i - 1 > 0) memmove(s->data + i, s->data + i + 1, (size_t)(s->len - i - 1) * sizeof(CufetDec));");
        sb.AppendLine("            s->len--; return;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        // Writes a series in the interpreter's format: (e1, e2, ...) — no trailing newline,
        // so it can be nested inside a record/object field. print_series adds the newline.
        sb.AppendLine("static void cufet_write_series(CufetSeries* s) {");
        sb.AppendLine("    printf(\"(\");");
        sb.AppendLine("    for (int i = 0; i < s->len; i++) {");
        sb.AppendLine("        if (i > 0) printf(\", \");");
        sb.AppendLine("        cufet_write_number(s->data[i]);");
        sb.AppendLine("    }");
        sb.AppendLine("    printf(\")\");");
        sb.AppendLine("}");
        sb.AppendLine("static void cufet_print_series(CufetSeries* s) { cufet_write_series(s); printf(\"\\n\"); }");
        // Element-wise value equality (used by record/object equality on series fields).
        sb.AppendLine("static int cufet_series_eq(CufetSeries* a, CufetSeries* b) {");
        sb.AppendLine("    if (a->len != b->len) return 0;");
        sb.AppendLine("    for (int i = 0; i < a->len; i++) if (cufet_cmp(a->data[i], b->data[i]) != 0) return 0;");
        sb.AppendLine("    return 1;");
        sb.AppendLine("}");
        sb.AppendLine();

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

        foreach (var bind in topFuncs)
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

        // ── main() ────────────────────────────────────────────────────────
        // A global arena is pushed so series created at top level (outside an
        // explicit Pull) are safely tracked and freed at program exit.
        body.AppendLine("int main(void) {");
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

        // ── Struct declarations (records + objects) + write helpers (dependency-ordered) ──
        EmitStructs(sb);

        // ── Forward declarations: object methods/getters/setters, then free functions ──
        foreach (var def in _objectDefs.Values)
        {
            foreach (var method in def.Methods) sb.AppendLine($"{MethodSignature(def, method)};");
            foreach (var g in def.Getters)      sb.AppendLine($"{GetterSignature(def, g)};");
            foreach (var s in def.Setters)      sb.AppendLine($"{SetterSignature(def, s)};");
        }
        foreach (var bind in topFuncs)
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
            case RecordType rt: RegisterRecordStruct(rt); break;
            case SeriesType st: RegisterNestedRecords(st.ElementType); break;
        }
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

    // Emits all record and object structs (both are C value structs) in dependency order —
    // a struct's nested value-struct fields must be declared before it (nesting is a DAG) —
    // each followed by its `_write` helper for nested/inline printing.
    private void EmitStructs(StringBuilder sb)
    {
        var specs = new Dictionary<string, (List<FieldSpec> Fields, string WritePrefix)>();
        foreach (var (name, rt) in _recordStructs) specs[name] = (RecordFields(rt), "record");
        foreach (var def in _objectDefs.Values)    specs[ObjStructName(def.Name)] = (ObjectFields(def), def.Name);
        if (specs.Count == 0) return;

        var emitted = new HashSet<string>();
        var order   = new List<string>();
        void Visit(string cname)
        {
            if (!emitted.Add(cname)) return;
            foreach (var fs in specs[cname].Fields)
            {
                string? dep = fs.Type switch
                {
                    RecordType rt => RegisterRecordStruct(rt),
                    ObjectType ot => ObjStructName(ot.Name),
                    _ => null
                };
                if (dep != null && specs.ContainsKey(dep)) Visit(dep);
            }
            order.Add(cname);
        }
        foreach (var cname in specs.Keys.ToList()) Visit(cname);

        sb.AppendLine("// ── Record & object shapes (value structs; nested by value, series by pointer) ──");
        foreach (var cname in order)
        {
            sb.AppendLine("typedef struct {");
            foreach (var fs in specs[cname].Fields)
                sb.AppendLine($"    {EmitCType(fs.Type)} {fs.CField};");
            sb.AppendLine($"}} {cname};");
        }
        sb.AppendLine();
        foreach (var cname in order)
        {
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

        // Value-equality helper per struct (records: structural; objects: nominal same-type).
        // Field-by-field, recursing into nested record/object fields and comparing series
        // element-wise — matching the interpreter's ValuesEqual.
        foreach (var cname in order)
        {
            var fields = specs[cname].Fields;
            string cond = fields.Count == 0 ? "1"
                : string.Join(" && ", fields.Select(fs => EqCall($"a.{fs.CField}", $"b.{fs.CField}", fs.Type)));
            sb.AppendLine($"static int {cname}_eq({cname} a, {cname} b) {{ return {cond}; }}");
        }
        sb.AppendLine();
    }

    // The C boolean expression comparing two values of type `t` by value.
    private string EqCall(string a, string b, CufetType t) => t switch
    {
        NumberType => $"cufet_cmp({a}, {b}) == 0",
        FactType   => $"({a} == {b})",
        TextType   => $"strcmp({a}, {b}) == 0",
        SeriesType => $"cufet_series_eq({a}, {b})",
        RecordType rt => $"{RegisterRecordStruct(rt)}_eq({a}, {b})",
        ObjectType ot => $"{ObjStructName(ot.Name)}_eq({a}, {b})",
        _ => throw new CompilerException($"equality on a '{t.GetType().Name}' is not yet supported by the compiler.")
    };

    // The C expression that writes `valExpr` inline (no trailing newline), dispatching
    // on its static type — used by record/object write helpers and by State.
    private string WriteCall(string valExpr, CufetType t) => t switch
    {
        NumberType => $"cufet_write_number({valExpr})",
        FactType   => $"cufet_write_fact({valExpr})",
        TextType   => $"cufet_write_text({valExpr})",
        SeriesType => $"cufet_write_series({valExpr})",
        RecordType rt => $"{RegisterRecordStruct(rt)}_write({valExpr})",
        ObjectType ot => $"{ObjStructName(ot.Name)}_write({valExpr})",
        _ => throw new CompilerException(
                 $"printing a '{t.GetType().Name}' is not yet supported by the compiler.")
    };

    private void EmitBlock(StringBuilder sb, IReadOnlyList<IStatement> body, string indent)
    {
        foreach (var stmt in body)
            EmitStatement(sb, stmt, indent);
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
                    SeriesType    => $"cufet_print_series({valExpr})",
                    RecordType rt => $"{RegisterRecordStruct(rt)}_write({valExpr}); printf(\"\\n\")",
                    ObjectType ot => $"{ObjStructName(ot.Name)}_write({valExpr}); printf(\"\\n\")",
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
                string valExpr = EmitExpr(b.Value);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{MangleName(b.Name)} = {valExpr};");
                break;
            }

            case ReturnStatement ret:
                if (ret.Value == null)
                    sb.AppendLine($"{indent}return;");
                else
                {
                    string retExpr = EmitExpr(ret.Value);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}return {retExpr};");
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

            case BindStatement:
                throw new CompilerException("Nested function declarations and closures are not yet supported by the compiler.");

            case PullRabbitStatement prs:
                sb.AppendLine($"{indent}cufet_arena_push();");
                sb.AppendLine($"{indent}{{");
                EmitBlock(sb, prs.Body, indent + "    ");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}cufet_arena_pop();");
                break;

            case SeriesAddStatement sa:
            {
                string valExpr = EmitExpr(sa.Value);
                FlushPreEmits(sb, indent);
                string serExpr = EmitExpr(sa.Series);
                FlushPreEmits(sb, indent);
                if (sa.ToStart)
                    sb.AppendLine($"{indent}cufet_series_prepend({serExpr}, {valExpr});");
                else if (sa.AfterIndex == null)
                    sb.AppendLine($"{indent}cufet_series_append({serExpr}, {valExpr});");
                else
                {
                    string idxExpr = EmitExpr(sa.AfterIndex);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}cufet_series_insert({serExpr}, cufet_to_int({idxExpr}), {valExpr});");
                }
                break;
            }

            case SeriesRemoveAtStatement sra:
            {
                string serExpr = EmitExpr(sra.Series);
                FlushPreEmits(sb, indent);
                if (sra.Index == null)
                    sb.AppendLine($"{indent}cufet_series_remove_at({serExpr}, -1);");
                else
                {
                    string idxExpr = EmitExpr(sra.Index);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}cufet_series_remove_at({serExpr}, cufet_to_int({idxExpr}));");
                }
                break;
            }

            case SeriesRemoveValueStatement srv:
            {
                string valExpr = EmitExpr(srv.Value);
                FlushPreEmits(sb, indent);
                string serExpr = EmitExpr(srv.Series);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_series_remove_value({serExpr}, {valExpr});");
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

            case ObjectDefinition:
            case GetterDeclaration:
            case SetterDeclaration:
                break;   // declarations — structs, methods, getters, setters emitted in the prelude

            case IfStatement ifStmt:
                EmitIf(sb, ifStmt, indent);
                break;

            case WhileStatement ws:
                sb.AppendLine($"{indent}while ({EmitExpr(ws.Condition)}) {{");
                EmitBlock(sb, ws.Body, indent + "    ");
                sb.AppendLine($"{indent}}}");
                break;

            case RepeatUntilStatement ru:
                sb.AppendLine($"{indent}do {{");
                EmitBlock(sb, ru.Body, indent + "    ");
                sb.AppendLine($"{indent}}} while (!({EmitExpr(ru.Condition)}));");
                break;

            case StopStatement:
                sb.AppendLine($"{indent}break;");
                break;

            case SkipStatement:
                sb.AppendLine($"{indent}continue;");
                break;

            case ForEachStatement fe when fe.Series is RangeExpression range:
                EmitForEachRange(sb, fe, range, indent);
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
        sb.AppendLine($"{indent}if ({EmitExpr(first.Condition)}) {{");
        EmitBlock(sb, first.Body, inner);

        for (int i = 1; i < ifStmt.Arms.Count; i++)
        {
            var arm = ifStmt.Arms[i];
            sb.AppendLine($"{indent}}} else if ({EmitExpr(arm.Condition)}) {{");
            EmitBlock(sb, arm.Body, inner);
        }

        if (ifStmt.ElseBody != null)
        {
            sb.AppendLine($"{indent}}} else {{");
            EmitBlock(sb, ifStmt.ElseBody, inner);
        }

        sb.AppendLine($"{indent}}}");
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
        EmitBlock(sb, fe.Body, loopIndent);
        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");
    }

    // For each loop over a materialized series (non-range).
    // cf_ temporaries avoid collisions with user variables.
    private void EmitForEachSeries(StringBuilder sb, ForEachStatement fe, string indent)
    {
        var inner      = indent + "    ";
        var loopIndent = inner  + "    ";
        int id = _forCounter++;
        string ser = $"cf_ser{id}";
        string idx = $"cf_i{id}";
        string iterName = MangleName(fe.IteratorName ?? "it");

        string serExpr = EmitExpr(fe.Series);
        FlushPreEmits(sb, indent);

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}CufetSeries* {ser} = {serExpr};");
        sb.AppendLine($"{inner}int {ser}_n = {ser}->len;");
        sb.AppendLine($"{inner}for (int {idx} = 0; {idx} < {ser}_n; {idx}++) {{");
        sb.AppendLine($"{loopIndent}CufetDec {iterName} = {ser}->data[{idx}];");
        EmitBlock(sb, fe.Body, loopIndent);
        sb.AppendLine($"{inner}}}");
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
        BinaryExpression b    => IsArithmeticOp(b.Op) ? TNumber : TFact,
        VariableReference vr  => _varTypes.TryGetValue(vr.Name, out var t) ? t : TNumber,
        CastExpression c      => CastReturnType(c),
        RecordLiteral rl      => new RecordType(
                                     rl.PositionalFields.Select(TypeOf).ToList(),
                                     rl.NamedFields.Select(f => (f.Name, TypeOf(f.Value))).ToList()),
        RecordNamedAccess rna => FieldType(TypeOf(rna.Record), rna.FieldName),
        ObjectLiteral ol      => ObjType(ol.TypeName),
        PossessiveAccess pa   => FieldType(TypeOf(pa.Target), pa.Member),
        _ => throw new CompilerException(
                 $"'{expr.GetType().Name}' expressions are not yet supported by the compiler.")
    };

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
        if (c.Function is VariableReference vr)
        {
            if (_funcReturnTypes.TryGetValue(vr.Name, out var rt)) return rt ?? TNumber;   // free function
            if (c.Args.Count > 0 && TypeOf(c.Args[0]) is ObjectType ot)                    // method dispatch
                return MethodReturnType(ot.Name, vr.Name);
        }
        if (c.Function is PossessiveAccess pa && TypeOf(pa.Target) is ObjectType pot)
            return MethodReturnType(pot.Name, pa.Member);
        return TNumber;
    }

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
        _varTypes.Clear();
        foreach (var (pType, pName) in bind.Parameters)
            _varTypes[pName] = pType;

        sb.AppendLine($"{EmitFunctionSignature(bind)} {{");
        EmitBlock(sb, bind.Body, "    ");
        sb.AppendLine("}");
        sb.AppendLine();

        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
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
        _varTypes.Clear();
        _methodReceiverType = def.Name;
        _varTypes["one"] = ObjType(def.Name);
        sb.AppendLine($"{GetterSignature(def, g)} {{");
        EmitBlock(sb, g.Body, "    ");
        sb.AppendLine("}");
        sb.AppendLine();
        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _methodReceiverType = savedRecv;
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
    private string EmitMemberAccess(IExpression target, string member) =>
        TypeOf(target) is ObjectType ot
            ? EmitObjectMemberRead(EmitExpr(target), ot.Name, member)
            : $"({EmitExpr(target)}).{MangleName(member)}";   // record field

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
        _varTypes.Clear();
        _methodReceiverType = def.Name;               // `one` → (*cv_one), resolves fields
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
    }

    private string EmitExpr(IExpression expr) => expr switch
    {
        NumberLiteral n       => EmitNumberLiteral(n.Value),
        BooleanLiteral bl     => bl.Value ? "1" : "0",
        StringLiteral s       => EscapeStringLiteral(s.Value),   // text-as-stored-data: static C string
        UnaryExpression u     => EmitUnary(u),
        BinaryExpression b    => EmitBinary(b),
        // `one` inside a method is a pointer param; deref so it reads as a value-struct
        // lvalue everywhere (field access via `.`, address taken as `&(*cv_one)` = cv_one).
        VariableReference v   => v.Name == "one" && _methodReceiverType != null ? "(*cv_one)" : MangleName(v.Name),
        CastExpression cast   => EmitCastExpr(cast),
        SeriesLiteral sl      => EmitSeriesLiteral(sl),
        SeriesLength sl2      => $"cufet_dec_from_ll(({EmitExpr(sl2.Series)})->len)",
        SeriesAccess sa       => EmitSeriesAccess(sa),
        RecordLiteral rl      => EmitRecordLiteral(rl),
        RecordNamedAccess rna => EmitMemberAccess(rna.Record, rna.FieldName),
        ObjectLiteral ol      => EmitObjectLiteral(ol),
        PossessiveAccess pa   => EmitMemberAccess(pa.Target, pa.Member),
        _ => throw new CompilerException(
                 $"'{expr.GetType().Name}' expressions are not yet supported by the compiler.")
    };

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
        if (SeriesElementType(sl) is not NumberType)
            throw new CompilerException("series of a non-number element type is not yet supported by the compiler (series are number-only this slice).");
        string tmp = $"cs_{_freshId++}";
        _preEmits.Add($"CufetSeries* {tmp} = cufet_series_new();");
        foreach (var elem in sl.Elements)
        {
            string elemExpr = EmitExpr(elem);
            _preEmits.Add($"cufet_series_append({tmp}, {elemExpr});");
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

    private string EmitCastExpr(CastExpression cast) => EmitCall(cast.Function, cast.Args);

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

        if (funcExpr is PossessiveAccess pa && TypeOf(pa.Target) is ObjectType pot)   // alice's greet
        {
            var (owner, suffix) = ResolveMethodLevel(pot.Name, pa.Member);
            var call = new[] { $"&(({EmitExpr(pa.Target)}){suffix})" }.Concat(args.Select(EmitExpr));
            return $"{MethodCName(owner, pa.Member)}({string.Join(", ", call)})";
        }

        throw new CompilerException("Function-value calls are not yet supported by the compiler.");
    }

    private string EmitUnary(UnaryExpression u) => u.Op switch
    {
        TokenType.Minus => $"cufet_neg({EmitExpr(u.Operand)})",
        TokenType.Not   => $"(!{EmitExpr(u.Operand)})",
        _ => throw new CompilerException($"Unary operator '{u.Op}' is not yet supported by the compiler.")
    };

    private string EmitBinary(BinaryExpression b)
    {
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

        // Records (structural) / objects (nominal same-type): field-by-field via _eq.
        if (lt is RecordType or ObjectType)
        {
            string eq = EqCall(L, R, lt);
            return b.Op switch
            {
                TokenType.Equal    => $"({eq})",
                TokenType.NotEqual => $"(!({eq}))",
                _ => throw new CompilerException($"'{b.Op}' on a '{lt.GetType().Name}' is not supported (only is / is not).")
            };
        }

        // Facts (ints).
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
        SeriesType st => st.ElementType is NumberType ? "CufetSeries*"
            : throw new CompilerException("series of a non-number element type is not yet supported by the compiler (series are number-only this slice)."),
        RecordType rt => RegisterRecordStruct(rt),
        ObjectType ot => ObjStructName(ot.Name),
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
