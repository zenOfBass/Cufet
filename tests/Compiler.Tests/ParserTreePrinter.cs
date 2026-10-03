using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cufet.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>The C# parser's tree, printed the way `self-hosting/front-end/parser/parser.cufe` prints its own.</summary>
/// <remarks>
/// ★ Its own file, depending on nothing but the interpreter, so a scratch tool can compile the very
/// same printer the test uses — two copies of an answer key is how an answer key drifts.
/// </remarks>
public static class ParserTreePrinter
{
    public static List<string> Expected(string source)
    {
        try
        {
            var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
            return program.Statements.Select(Show).ToList();
        }
        catch (Exception e) when (e is ParseException or Cufet.Lexer.LexerException)
        {
            return [$"error\t{InCharacters(e.Message, source)}"];
        }
    }

    /// <summary>A refusal's column, counted in characters rather than UTF-16 units.</summary>
    /// <remarks>
    /// ⚠ The same conversion the lexer's test makes, for the same reason: past a character outside
    /// the Basic Multilingual Plane, C# counts one more than Cufet does. Both message shapes carry
    /// the position — `Line 3, column 7: …` and `… on line 3, column 7.`
    /// </remarks>
    private static string InCharacters(string message, string source)
    {
        var lines = source.Split('\n');
        return System.Text.RegularExpressions.Regex.Replace(message,
            @"([Ll]ine )(\d+)(, column )(\d+)",
            m =>
            {
                int line = int.Parse(m.Groups[2].Value), column = int.Parse(m.Groups[4].Value);
                if (line - 1 >= lines.Length) return m.Value;
                string text = lines[line - 1];
                int pairs = 0;
                for (int i = 0; i + 1 < text.Length && i < column - 1; i++)
                    if (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1])) pairs++;
                return $"{m.Groups[1].Value}{line}{m.Groups[3].Value}{column - pairs}";
            },
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    /// <summary>One value of the tree, as the Cufet parser prints it.</summary>
    public static string Show(object? value)
    {
        switch (value)
        {
            case null:     return "_";
            case string s: return Quoted(s);
            case char c:   return Quoted(c.ToString());
            case bool b:   return b ? "true" : "false";
            // ⚠ A number prints in its shortest form — `1.50` and `1.5` are one number, and a
            // decimal remembers the scale it was written with.
            // ⚠ Fixed-point, never scientific: `G29` printed 1e-14 as `1E-14`, which is not how a
            // number is written in Cufet and not how Cufet prints one.
            case decimal d: return d.ToString("0.############################", CultureInfo.InvariantCulture);
            case int or long or ulong: return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case Enum e:   return e.ToString();
            // A type named in an annotation is only a name here; the checker fills in the rest.
            case ObjectType ot:    return $"(ObjectType {Quoted(ot.Name)} {Show(ot.TypeArguments)})";
            case ITuple tuple:
            {
                var parts = new List<string>();
                for (int i = 0; i < tuple.Length; i++) parts.Add(Show(tuple[i]));
                return "[" + string.Join(" ", parts) + "]";
            }
            case IEnumerable list:
                return "[" + string.Join(" ", list.Cast<object?>().Select(Show)) + "]";
        }

        var type = value.GetType();
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        var fields = ctor?.GetParameters() ?? [];
        var shown = fields.Select(p =>
            Show(type.GetProperty(p.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                     ?.GetValue(value))).ToList();

        // ★ What the parser set OUTSIDE the constructor — `NamedArgs`, `PassesFailureOff`, a Bind's
        // `When`. Printed only when it holds something, as `:Name value`. The checker's side
        // channels are settable too, but nothing has checked this tree yet, so they are all still
        // empty and none of them prints.
        var fieldNames = fields.Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (fieldNames.Contains(property.Name) || property.SetMethod is not { IsPublic: true }) continue;
            var held = property.GetValue(value);
            bool empty = held is null or false or 0
                      || (held is ICollection { Count: 0 })
                      || (held is IEnumerable e and not string && !e.Cast<object?>().Any());
            if (!empty) shown.Add($":{property.Name} {Show(held)}");
        }
        return shown.Count == 0 ? $"({type.Name})" : $"({type.Name} {string.Join(" ", shown)})";
    }

    public static string Quoted(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
                .Replace("\t", "\\t").Replace("\r", "\\r") + "\"";

}
