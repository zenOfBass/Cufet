using System.Text;
using Cufet.Interpreter;
using Cufet.Lexer;

namespace Cufet.Compiler;

public sealed class CodeGenerator
{
    private int _forCounter;

    public string Generate(Program program)
    {
        var sb = new StringBuilder();

        sb.AppendLine("#include <stdio.h>");
        sb.AppendLine("#include <math.h>");
        sb.AppendLine();
        // cufet_print_number: integers within double's exact range print without decimal;
        //   non-integers via %.15g (approximation; exact decimal deferred to later slice).
        sb.AppendLine("static void cufet_print_number(double v) {");
        sb.AppendLine("    long long i = (long long)v;");
        sb.AppendLine("    if ((double)i == v && v >= -9007199254740992.0 && v <= 9007199254740992.0) {");
        sb.AppendLine("        printf(\"%lld\\n\", i);");
        sb.AppendLine("        return;");
        sb.AppendLine("    }");
        sb.AppendLine("    char buf[64];");
        sb.AppendLine("    snprintf(buf, sizeof(buf), \"%.15g\", v);");
        sb.AppendLine("    printf(\"%s\\n\", buf);");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("static void cufet_print_text(const char* s) {");
        sb.AppendLine("    printf(\"%s\\n\", s);");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("int main(void) {");

        foreach (var stmt in program.Statements)
            EmitStatement(sb, stmt, "    ");

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
                // String literals go through cufet_print_text; everything else is numeric.
                if (s.Value is StringLiteral sl)
                    sb.AppendLine($"{indent}cufet_print_text({EscapeStringLiteral(sl.Value)});");
                else
                    sb.AppendLine($"{indent}cufet_print_number({EmitExpr(s.Value)});");
                break;

            case DefineStatement d:
                string qualifier = d.Permanent ? "const double" : "double";
                sb.AppendLine($"{indent}{qualifier} {MangleName(d.Name)} = {EmitExpr(d.Value)};");
                break;

            case BecomesStatement b:
                sb.AppendLine($"{indent}{MangleName(b.Name)} = {EmitExpr(b.Value)};");
                break;

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
        // dir is +step when ascending, -step when descending
        sb.AppendLine($"{inner}double {d}  = ({s} <= {e}) ? {st} : -{st};");
        sb.AppendLine($"{inner}for (double {iterName} = {s}; {d} > 0.0 ? {iterName} <= {e} : {iterName} >= {e}; {iterName} += {d}) {{");
        EmitBlock(sb, fe.Body, loopIndent);
        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");
    }

    private string EmitExpr(IExpression expr) => expr switch
    {
        NumberLiteral n      => FormatDecimal(n.Value),
        BooleanLiteral bl    => bl.Value ? "1" : "0",
        UnaryExpression u    => EmitUnary(u),
        BinaryExpression b   => EmitBinary(b),
        VariableReference v  => MangleName(v.Name),
        _ => throw new CompilerException(
                 $"'{expr.GetType().Name}' expressions are not yet supported by the compiler.")
    };

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

        // && and || short-circuit in C, matching the interpreter's short-circuit evaluation.
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

    // Normalize trailing zeros (mirrors interpreter's Format(decimal)) then render as a C
    // double literal.  Appending ".0" ensures C parses it as floating-point, not integer.
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
