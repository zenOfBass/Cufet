using Cufet.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>What the C# checker says of a program, printed the way `self-hosting/front-end/checker/checker.cufe` prints it.</summary>
/// <remarks>
/// ★ Its own file, depending on nothing but the interpreter, so a scratch tool can compile the very
/// same answer key the test uses — the reason <see cref="ParserTreePrinter"/> is one too.
/// </remarks>
public static class CheckerAnswer
{
    /// <summary>`ok`, `error` and the message on one line, or `parse` when there was nothing to check.</summary>
    /// <remarks>
    /// ⚠ Checked as a lone file — no source directory, so no neighbours and no book loaded from
    /// disk. The Cufet checker is given each file on its own the same way, and a pull of a book
    /// that is not bundled is then a refusal on both sides.
    /// </remarks>
    public static string Expected(string source)
    {
        Cufet.Interpreter.Program program;
        try
        {
            program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        }
        catch (Exception e) when (e is ParseException or Cufet.Lexer.LexerException)
        {
            return "parse";
        }
        try
        {
            new TypeChecker().Check(program);
            return "ok";
        }
        catch (TypeException e)
        {
            return $"error\t{OneLine(e.Message)}";
        }
    }

    /// <summary>What C# says of the file at <paramref name="path"/>, checked where it sits — as `cufet check` checks it.</summary>
    /// <remarks>
    /// ★ Its directory and its own path are given, so inside a project its folder's files are part
    /// of its program and a book is loaded from beside it. A file under no project gets nothing
    /// more, so its answer is <see cref="Expected"/>'s.
    /// ⚠ A refusal's lines are turned back into each file's own through THIS check's map — never
    /// <see cref="SourceMap.Current"/>, which every check running at once would share.
    /// </remarks>
    public static string ExpectedAt(string path)
    {
        var full = Path.GetFullPath(path);
        Cufet.Interpreter.Program program;
        try
        {
            program = new Parser(new CufetLexer(File.ReadAllText(full)).Tokenize()).Parse();
        }
        catch (Exception e) when (e is ParseException or Cufet.Lexer.LexerException)
        {
            return "parse";
        }
        var checker = new TypeChecker { SourceDirectory = Path.GetDirectoryName(full), SourceFile = full };
        try
        {
            checker.Check(program);
            return "ok";
        }
        catch (TypeException e)
        {
            return $"error\t{OneLine(Rewritten(e.Message, checker.Sources))}";
        }
        catch (Exception e) when (e is ParseException or Cufet.Lexer.LexerException)
        {
            return "parse";
        }
    }

    /// <summary><see cref="CheckedTree"/> for the file at <paramref name="path"/>, checked where it sits.</summary>
    public static string? CheckedTreeAt(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var checker = new TypeChecker { SourceDirectory = Path.GetDirectoryName(full), SourceFile = full };
            var program = checker.Check(new Parser(new CufetLexer(File.ReadAllText(full)).Tokenize()).Parse());
            return string.Join("\n", program.Statements
                .Where(s => !TypeChecker.IsFromPrelude(s))
                .Select(ParserTreePrinter.Show));
        }
        catch (Exception e) when (e is ParseException or Cufet.Lexer.LexerException or TypeException)
        {
            return null;
        }
    }

    /// <summary>`SourceMap.Rewrite` for one map: every line from a loaded file, that file's own.</summary>
    private static string Rewritten(string message, SourceMap map) =>
        System.Text.RegularExpressions.Regex.Replace(message, "[0-9]{6,}",
            m => int.TryParse(m.Value, out int n) && map.Resolve(n) is { } at
                 ? at.Line.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 : m.Value);

    /// <summary>The program `Check` hands back, one printed statement per line, the prelude left out.</summary>
    /// <remarks>
    /// ★ The prelude is told by C#'s own rule, <see cref="TypeChecker.IsFromPrelude"/> — never by a
    /// list of book names kept here. Null when the program does not parse or is refused; <see
    /// cref="Expected"/> says which.
    /// </remarks>
    public static string? CheckedTree(string source)
    {
        try
        {
            var program = new TypeChecker().Check(new Parser(new CufetLexer(source).Tokenize()).Parse());
            return string.Join("\n", program.Statements
                .Where(s => !TypeChecker.IsFromPrelude(s))
                .Select(ParserTreePrinter.Show));
        }
        catch (Exception e) when (e is ParseException or Cufet.Lexer.LexerException or TypeException)
        {
            return null;
        }
    }

    /// <summary>A message's line breaks written `\n`, and its backslashes doubled so they stay apart.</summary>
    public static string OneLine(string message) =>
        message.Replace("\r", "").Replace("\\", "\\\\").Replace("\n", "\\n");
}
