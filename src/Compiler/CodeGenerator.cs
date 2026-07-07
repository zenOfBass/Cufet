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
    // Tracks the code-type of declared variables so StateStatement / DefineStatement
    // can dispatch to the right C print helper and declaration keyword.
    private readonly Dictionary<string, CodeType> _varTypes = new();
    // Return code-type of each top-level function, so CastExpression can be typed.
    private readonly Dictionary<string, CodeType> _funcReturnTypes = new();

    // A compiled scalar is now either a number (software decimal) or a fact (int).
    // Numbers were doubles before slice 5.5; they are CufetDec now so decimal
    // arithmetic is bit-identical to the interpreter's System.Decimal.
    private enum CodeType { Number, Fact, Series }

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
static void cufet_print_number(CufetDec d) { char b[64]; cufet_format_number(b, sizeof(b), d); printf("%s\n", b); }
static void cufet_print_fact(int b) { printf("%s\n", b ? "true" : "false"); }
static void cufet_print_text(const char* s) { printf("%s\n", s); }

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
        // Prints a series in the interpreter's format: (e1, e2, ...) with newline.
        sb.AppendLine("static void cufet_print_series(CufetSeries* s) {");
        sb.AppendLine("    char buf[64];");
        sb.AppendLine("    printf(\"(\");");
        sb.AppendLine("    for (int i = 0; i < s->len; i++) {");
        sb.AppendLine("        if (i > 0) printf(\", \");");
        sb.AppendLine("        cufet_format_number(buf, sizeof(buf), s->data[i]);");
        sb.AppendLine("        printf(\"%s\", buf);");
        sb.AppendLine("    }");
        sb.AppendLine("    printf(\")\\n\");");
        sb.AppendLine("}");
        sb.AppendLine();

        // ── Slice 4: top-level scalar function declarations ───────────────
        // Forward declarations first so mutual recursion and forward calls work.
        var topFuncs = program.Statements
            .OfType<BindStatement>()
            .Where(b => b.UntoType == null && b.ConstructsTypeName == null)
            .ToList();

        foreach (var bind in topFuncs)
            _funcReturnTypes[bind.Name] = bind.ReturnType is FactType ? CodeType.Fact : CodeType.Number;

        foreach (var bind in topFuncs)
            sb.AppendLine($"{EmitFunctionSignature(bind)};");
        if (topFuncs.Count > 0)
            sb.AppendLine();

        foreach (var bind in topFuncs)
            EmitBind(sb, bind);

        // ── main() ────────────────────────────────────────────────────────
        // A global arena is pushed so series created at top level (outside an
        // explicit Pull) are safely tracked and freed at program exit.
        // Nested Pull blocks push additional arenas on top of this one.
        sb.AppendLine("int main(void) {");
        sb.AppendLine("    cufet_arena_push();");
        foreach (var stmt in program.Statements)
        {
            if (stmt is BindStatement) continue; // already emitted above
            EmitStatement(sb, stmt, "    ");
        }
        sb.AppendLine("    cufet_arena_pop();");
        sb.AppendLine("    return 0;");
        sb.AppendLine("}");

        return sb.ToString();
    }

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
                if (s.Value is StringLiteral sl)
                {
                    sb.AppendLine($"{indent}cufet_print_text({EscapeStringLiteral(sl.Value)});");
                }
                else
                {
                    string valExpr = EmitExpr(s.Value);
                    FlushPreEmits(sb, indent);
                    switch (InferCodeType(s.Value))
                    {
                        case CodeType.Series: sb.AppendLine($"{indent}cufet_print_series({valExpr});"); break;
                        case CodeType.Fact:   sb.AppendLine($"{indent}cufet_print_fact({valExpr});");   break;
                        default:              sb.AppendLine($"{indent}cufet_print_number({valExpr});");  break;
                    }
                }
                break;
            }

            case DefineStatement d:
            {
                string valExpr = EmitExpr(d.Value);
                FlushPreEmits(sb, indent);
                var ct = InferCodeType(d.Value);
                _varTypes[d.Name] = ct;
                string baseType = ct switch
                {
                    CodeType.Series => "CufetSeries*",
                    CodeType.Fact   => "int",
                    _               => "CufetDec",
                };
                // 'permanently' fixes the binding — const on the scalar C type
                // (series are pointers into the arena; leave them non-const).
                string decl = (d.Permanent && ct != CodeType.Series) ? "const " + baseType : baseType;
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
                if (cs.Function is not VariableReference csVr)
                    throw new CompilerException("Method calls and function-value calls are not yet supported by the compiler.");
                sb.AppendLine($"{indent}{MangleName(csVr.Name)}({string.Join(", ", cs.Args.Select(EmitExpr))});");
                break;

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

    // Returns the CodeType (Number, Fact, or Series) of an expression.
    // Used to pick the right C declaration keyword and print helper.
    private CodeType InferCodeType(IExpression expr) => expr switch
    {
        SeriesLiteral        => CodeType.Series,
        RangeExpression      => CodeType.Series,
        NumberLiteral        => CodeType.Number,
        BooleanLiteral       => CodeType.Fact,
        SeriesLength         => CodeType.Number,
        SeriesAccess         => CodeType.Number,   // series-of-number element
        VariableReference vr => _varTypes.TryGetValue(vr.Name, out var t) ? t : CodeType.Number,
        UnaryExpression u    => u.Op == TokenType.Not ? CodeType.Fact : CodeType.Number,
        BinaryExpression b   => IsArithmeticOp(b.Op) ? CodeType.Number : CodeType.Fact,
        CastExpression c     => c.Function is VariableReference cvr && _funcReturnTypes.TryGetValue(cvr.Name, out var rt) ? rt : CodeType.Number,
        _                    => CodeType.Number,
    };

    private static bool IsArithmeticOp(TokenType op) =>
        op is TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent;

    // Validates and emits the signature of a top-level scalar function.
    // Used for both forward declarations (appending ";") and full definitions.
    // Throws CompilerException for methods, constructors, or reference-type params/returns.
    private string EmitFunctionSignature(BindStatement bind)
    {
        if (bind.UntoType != null)
            throw new CompilerException($"Object methods declared with 'unto' are not yet supported by the compiler.");
        if (bind.ConstructsTypeName != null)
            throw new CompilerException($"Named constructors are not yet supported by the compiler.");

        foreach (var (type, pName) in bind.Parameters)
        {
            if (!IsScalarType(type))
                throw new CompilerException(
                    $"'{bind.Name}': parameter '{pName}' has a reference type — reference-type function parameters are not yet implemented by the compiler.");
        }

        if (!IsScalarType(bind.ReturnType))
            throw new CompilerException(
                $"'{bind.Name}': reference-type return types are not yet implemented by the compiler.");

        var paramsStr = string.Join(", ", bind.Parameters.Select(p => $"{EmitCType(p.Type)} {MangleName(p.Name)}"));
        return $"{EmitCType(bind.ReturnType)} {MangleName(bind.Name)}({paramsStr})";
    }

    private void EmitBind(StringBuilder sb, BindStatement bind)
    {
        // Save and restore _varTypes so function-local names don't pollute
        // the outer scope's type map (and vice versa).
        var saved = new Dictionary<string, CodeType>(_varTypes);
        _varTypes.Clear();
        foreach (var (pType, pName) in bind.Parameters)
            _varTypes[pName] = pType is FactType ? CodeType.Fact : CodeType.Number;

        sb.AppendLine($"{EmitFunctionSignature(bind)} {{");
        EmitBlock(sb, bind.Body, "    ");
        sb.AppendLine("}");
        sb.AppendLine();

        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
    }

    private string EmitExpr(IExpression expr) => expr switch
    {
        NumberLiteral n      => EmitNumberLiteral(n.Value),
        BooleanLiteral bl    => bl.Value ? "1" : "0",
        UnaryExpression u    => EmitUnary(u),
        BinaryExpression b   => EmitBinary(b),
        VariableReference v  => MangleName(v.Name),
        CastExpression cast  => EmitCastExpr(cast),
        SeriesLiteral sl     => EmitSeriesLiteral(sl),
        SeriesLength sl2     => $"cufet_dec_from_ll(({EmitExpr(sl2.Series)})->len)",
        SeriesAccess sa      => EmitSeriesAccess(sa),
        _ => throw new CompilerException(
                 $"'{expr.GetType().Name}' expressions are not yet supported by the compiler.")
    };

    // Emits a series literal as a named temporary, registering construction
    // statements in _preEmits. The caller must FlushPreEmits before using
    // the returned variable name in a statement.
    private string EmitSeriesLiteral(SeriesLiteral sl)
    {
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
        string targetExpr = EmitExpr(sa.Target);
        if (sa.Index == null)
            return $"({targetExpr})->data[({targetExpr})->len - 1]";
        string idxExpr = EmitExpr(sa.Index);
        return $"({targetExpr})->data[cufet_to_int({idxExpr}) - 1]";
    }

    private string EmitCastExpr(CastExpression cast)
    {
        if (cast.Function is not VariableReference vr)
            throw new CompilerException("Method calls and function-value calls are not yet supported by the compiler.");
        return $"{MangleName(vr.Name)}({string.Join(", ", cast.Args.Select(EmitExpr))})";
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

        // Comparison / equality. Numbers compare via cufet_cmp; facts are ints.
        if (InferCodeType(b.Left) == CodeType.Number)
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

        return b.Op switch
        {
            TokenType.Equal    => $"({L} == {R})",
            TokenType.NotEqual => $"({L} != {R})",
            _ => throw new CompilerException($"Binary operator '{b.Op}' on facts is not yet supported by the compiler.")
        };
    }

    // cv_ prefix avoids C keyword collisions (e.g. Cufet "double" → cv_double).
    // Hyphens replaced by underscores; Cufet identifiers never contain underscores, so no collision.
    private static string MangleName(string name) => "cv_" + name.Replace('-', '_');

    // Scalar types (number → CufetDec, fact → int) map cleanly to C pass-by-value parameters.
    // Text, series, maps, objects, etc. are reference types that need the arena (slice 5+).
    private static bool IsScalarType(CufetType? type) =>
        type == null || type is NumberType || type is FactType;

    private static string EmitCType(CufetType? type) => type switch
    {
        null       => "void",
        NumberType => "CufetDec",
        FactType   => "int",
        _ => throw new CompilerException(
                 $"'{type!.GetType().Name}' is not a scalar type — reference-type function parameters and returns are not yet implemented by the compiler.")
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
