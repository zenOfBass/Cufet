using System.Text;
using Cufet.Interpreter;
using Cufet.Lexer;

namespace Cufet.Compiler;

public sealed class CodeGenerator
{
    public string Generate(Program program)
    {
        var sb = new StringBuilder();

        // Runtime support — emitted into every translation unit.
        // cufet_print_number: matches the interpreter's Format() for numbers.
        //   Integers within double's exact range print without a decimal point.
        //   Non-integers use %.15g (approximate; exact decimal deferred to later slice).
        sb.AppendLine("#include <stdio.h>");
        sb.AppendLine();
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
        sb.AppendLine("int main(void) {");

        foreach (var stmt in program.Statements)
            EmitStatement(sb, stmt);

        sb.AppendLine("    return 0;");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private void EmitStatement(StringBuilder sb, IStatement stmt)
    {
        switch (stmt)
        {
            case StateStatement s:
                sb.AppendLine($"    cufet_print_number({EmitExpr(s.Value)});");
                break;
            case DefineStatement d:
                string qualifier = d.Permanent ? "const double" : "double";
                sb.AppendLine($"    {qualifier} {MangleName(d.Name)} = {EmitExpr(d.Value)};");
                break;
            case BecomesStatement b:
                sb.AppendLine($"    {MangleName(b.Name)} = {EmitExpr(b.Value)};");
                break;
            default:
                throw new CompilerException(
                    $"'{stmt.GetType().Name}' is not yet supported by the compiler.");
        }
    }

    private string EmitExpr(IExpression expr) => expr switch
    {
        NumberLiteral n      => FormatDecimal(n.Value),
        UnaryExpression u    => EmitUnary(u),
        BinaryExpression b   => EmitBinary(b),
        VariableReference v  => MangleName(v.Name),
        _ => throw new CompilerException(
                 $"'{expr.GetType().Name}' expressions are not yet supported by the compiler.")
    };

    private string EmitUnary(UnaryExpression u)
    {
        if (u.Op != TokenType.Minus)
            throw new CompilerException($"Unary operator '{u.Op}' is not yet supported by the compiler.");
        return $"(-{EmitExpr(u.Operand)})";
    }

    private string EmitBinary(BinaryExpression b)
    {
        string op = b.Op switch
        {
            TokenType.Plus  => "+",
            TokenType.Minus => "-",
            TokenType.Star  => "*",
            TokenType.Slash => "/",
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
}
