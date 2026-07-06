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
    // Tracks the C type of declared variables so StateStatement / DefineStatement
    // can dispatch to the right C print helper and declaration keyword.
    private readonly Dictionary<string, CodeType> _varTypes = new();

    private enum CodeType { Scalar, Series }

    public string Generate(Program program)
    {
        var sb = new StringBuilder();

        // ── Standard includes ─────────────────────────────────────────────
        sb.AppendLine("#include <stdio.h>");
        sb.AppendLine("#include <stdlib.h>");
        sb.AppendLine("#include <string.h>");
        sb.AppendLine("#include <math.h>");
        sb.AppendLine();

        // ── Number formatting helper ──────────────────────────────────────
        // Formats a double into buf using the same rules as the interpreter:
        // integers print without decimal point; floats use %.15g.
        sb.AppendLine("static void cufet_format_number(char* buf, size_t bufsz, double v) {");
        sb.AppendLine("    long long i = (long long)v;");
        sb.AppendLine("    if ((double)i == v && v >= -9007199254740992.0 && v <= 9007199254740992.0) {");
        sb.AppendLine("        snprintf(buf, bufsz, \"%lld\", i);");
        sb.AppendLine("    } else {");
        sb.AppendLine("        snprintf(buf, bufsz, \"%.15g\", v);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_print_number(double v) {");
        sb.AppendLine("    char buf[64];");
        sb.AppendLine("    cufet_format_number(buf, sizeof(buf), v);");
        sb.AppendLine("    printf(\"%s\\n\", buf);");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_print_text(const char* s) {");
        sb.AppendLine("    printf(\"%s\\n\", s);");
        sb.AppendLine("}");
        sb.AppendLine();

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
        // growth — old buffer stays in ptrs, freed at arena_pop).
        sb.AppendLine("typedef struct { double* data; int len; int cap; } CufetSeries;");
        sb.AppendLine();
        sb.AppendLine("static CufetSeries* cufet_series_new(void) {");
        sb.AppendLine("    CufetSeries* s = (CufetSeries*)cufet_arena_alloc(sizeof(CufetSeries));");
        sb.AppendLine("    s->data = NULL; s->len = 0; s->cap = 0;");
        sb.AppendLine("    return s;");
        sb.AppendLine("}");
        sb.AppendLine();
        // Ensures s->len < s->cap, allocating/reallocating data in the arena.
        sb.AppendLine("static void cufet_series_ensure(CufetSeries* s) {");
        sb.AppendLine("    if (s->len >= s->cap) {");
        sb.AppendLine("        int nc = s->cap == 0 ? 4 : s->cap * 2;");
        sb.AppendLine("        double* nd = (double*)cufet_arena_alloc((size_t)nc * sizeof(double));");
        sb.AppendLine("        if (s->len > 0) memcpy(nd, s->data, (size_t)s->len * sizeof(double));");
        sb.AppendLine("        s->data = nd; s->cap = nc;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_series_append(CufetSeries* s, double v) {");
        sb.AppendLine("    cufet_series_ensure(s);");
        sb.AppendLine("    s->data[s->len++] = v;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_series_prepend(CufetSeries* s, double v) {");
        sb.AppendLine("    cufet_series_ensure(s);");
        sb.AppendLine("    if (s->len > 0) memmove(s->data + 1, s->data, (size_t)s->len * sizeof(double));");
        sb.AppendLine("    s->data[0] = v; s->len++;");
        sb.AppendLine("}");
        sb.AppendLine();
        // Inserts v after 1-based position after1.
        // after1 is already the correct 0-based insertion index (1-based "after N" == 0-based index N).
        sb.AppendLine("static void cufet_series_insert(CufetSeries* s, int after1, double v) {");
        sb.AppendLine("    cufet_series_ensure(s);");
        sb.AppendLine("    int pos = after1;");
        sb.AppendLine("    if (s->len > pos) memmove(s->data + pos + 1, s->data + pos, (size_t)(s->len - pos) * sizeof(double));");
        sb.AppendLine("    s->data[pos] = v; s->len++;");
        sb.AppendLine("}");
        sb.AppendLine();
        // idx1: 1-based; pass -1 for "last".
        sb.AppendLine("static void cufet_series_remove_at(CufetSeries* s, int idx1) {");
        sb.AppendLine("    int idx = (idx1 < 0) ? s->len - 1 : idx1 - 1;");
        sb.AppendLine("    if (s->len - idx - 1 > 0) memmove(s->data + idx, s->data + idx + 1, (size_t)(s->len - idx - 1) * sizeof(double));");
        sb.AppendLine("    s->len--;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_series_remove_value(CufetSeries* s, double v) {");
        sb.AppendLine("    for (int i = 0; i < s->len; i++) {");
        sb.AppendLine("        if (s->data[i] == v) {");
        sb.AppendLine("            if (s->len - i - 1 > 0) memmove(s->data + i, s->data + i + 1, (size_t)(s->len - i - 1) * sizeof(double));");
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
                    if (InferCodeType(s.Value) == CodeType.Series)
                        sb.AppendLine($"{indent}cufet_print_series({valExpr});");
                    else
                        sb.AppendLine($"{indent}cufet_print_number({valExpr});");
                }
                break;
            }

            case DefineStatement d:
            {
                string valExpr = EmitExpr(d.Value);
                FlushPreEmits(sb, indent);
                var ct = InferCodeType(d.Value);
                _varTypes[d.Name] = ct;
                if (ct == CodeType.Series)
                    sb.AppendLine($"{indent}CufetSeries* {MangleName(d.Name)} = {valExpr};");
                else
                    sb.AppendLine($"{indent}{(d.Permanent ? "const double" : "double")} {MangleName(d.Name)} = {valExpr};");
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
                    sb.AppendLine($"{indent}cufet_series_insert({serExpr}, (int)({idxExpr}), {valExpr});");
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
                    sb.AppendLine($"{indent}cufet_series_remove_at({serExpr}, (int)({idxExpr}));");
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
                    sb.AppendLine($"{indent}{serExpr}->data[(int)({idxExpr}) - 1] = {valExpr};");
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
    //   - ascending when start ≤ end, descending otherwise
    //   - step is a positive magnitude; direction determined by start/end
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
        string stepExpr = range.Step != null ? EmitExpr(range.Step) : "1.0";

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}double {s}  = {EmitExpr(range.Start)};");
        sb.AppendLine($"{inner}double {e}  = {EmitExpr(range.End)};");
        sb.AppendLine($"{inner}double {st} = {stepExpr};");
        sb.AppendLine($"{inner}double {d}  = ({s} <= {e}) ? {st} : -{st};");
        sb.AppendLine($"{inner}for (double {iterName} = {s}; {d} > 0.0 ? {iterName} <= {e} : {iterName} >= {e}; {iterName} += {d}) {{");
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
        sb.AppendLine($"{loopIndent}double {iterName} = {ser}->data[{idx}];");
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

    // Returns the CodeType (Scalar or Series) of an expression.
    // Used to pick the right C declaration keyword and print helper.
    private CodeType InferCodeType(IExpression expr) => expr switch
    {
        SeriesLiteral      => CodeType.Series,
        VariableReference vr => _varTypes.TryGetValue(vr.Name, out var t) ? t : CodeType.Scalar,
        _                  => CodeType.Scalar,
    };

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
            _varTypes[pName] = CodeType.Scalar; // all compiled params are scalar

        sb.AppendLine($"{EmitFunctionSignature(bind)} {{");
        EmitBlock(sb, bind.Body, "    ");
        sb.AppendLine("}");
        sb.AppendLine();

        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
    }

    private string EmitExpr(IExpression expr) => expr switch
    {
        NumberLiteral n      => FormatDecimal(n.Value),
        BooleanLiteral bl    => bl.Value ? "1" : "0",
        UnaryExpression u    => EmitUnary(u),
        BinaryExpression b   => EmitBinary(b),
        VariableReference v  => MangleName(v.Name),
        CastExpression cast  => EmitCastExpr(cast),
        SeriesLiteral sl     => EmitSeriesLiteral(sl),
        SeriesLength sl2     => $"(double)(({EmitExpr(sl2.Series)})->len)",
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
        return $"({targetExpr})->data[(int)({idxExpr}) - 1]";
    }

    private string EmitCastExpr(CastExpression cast)
    {
        if (cast.Function is not VariableReference vr)
            throw new CompilerException("Method calls and function-value calls are not yet supported by the compiler.");
        return $"{MangleName(vr.Name)}({string.Join(", ", cast.Args.Select(EmitExpr))})";
    }

    private string EmitUnary(UnaryExpression u) => u.Op switch
    {
        TokenType.Minus => $"(-{EmitExpr(u.Operand)})",
        TokenType.Not   => $"(!{EmitExpr(u.Operand)})",
        _ => throw new CompilerException($"Unary operator '{u.Op}' is not yet supported by the compiler.")
    };

    private string EmitBinary(BinaryExpression b)
    {
        // % on doubles requires fmod(); sign-of-dividend semantics match C# decimal %.
        if (b.Op == TokenType.Percent)
            return $"fmod({EmitExpr(b.Left)}, {EmitExpr(b.Right)})";

        string op = b.Op switch
        {
            TokenType.Plus     => "+",
            TokenType.Minus    => "-",
            TokenType.Star     => "*",
            TokenType.Slash    => "/",
            TokenType.Equal    => "==",
            TokenType.NotEqual => "!=",
            TokenType.Lt       => "<",
            TokenType.Gt       => ">",
            TokenType.Lte      => "<=",
            TokenType.Gte      => ">=",
            TokenType.And      => "&&",
            TokenType.Or       => "||",
            _ => throw new CompilerException($"Binary operator '{b.Op}' is not yet supported by the compiler.")
        };
        return $"({EmitExpr(b.Left)} {op} {EmitExpr(b.Right)})";
    }

    // cv_ prefix avoids C keyword collisions (e.g. Cufet "double" → cv_double).
    // Hyphens replaced by underscores; Cufet identifiers never contain underscores, so no collision.
    private static string MangleName(string name) => "cv_" + name.Replace('-', '_');

    // Scalar types (number → double, fact → int) map cleanly to C pass-by-value parameters.
    // Text, series, maps, objects, etc. are reference types that need the arena (slice 5+).
    private static bool IsScalarType(CufetType? type) =>
        type == null || type is NumberType || type is FactType;

    private static string EmitCType(CufetType? type) => type switch
    {
        null       => "void",
        NumberType => "double",
        FactType   => "int",
        _ => throw new CompilerException(
                 $"'{type!.GetType().Name}' is not a scalar type — reference-type function parameters and returns are not yet implemented by the compiler.")
    };

    // Normalize trailing zeros (mirrors interpreter's Format(decimal)) then render as a C
    // double literal. Appending ".0" ensures C parses it as floating-point, not integer.
    private static string FormatDecimal(decimal d)
    {
        decimal n = d / 1.0000000000000000000000000000m;
        string s = n.ToString();
        return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0";
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
