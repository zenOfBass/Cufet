using System.Runtime.InteropServices.JavaScript;
using Cufet.Interpreter;
using Cufet.Lexer;
using CufetLexer = Cufet.Lexer.Lexer;
using CufetInterpreter = Cufet.Interpreter.Interpreter;

namespace Cufet.Playground;

// The whole browser-facing surface of the playground: two functions taking source text.
//
// The interpreter already accepts a TextWriter and TextReader — that is how the test suite has
// always driven it — so capturing output needs no change to it at all. Nothing here reaches for
// Console.
public static partial class Runtime
{
    // Runs a program and returns everything it printed. A Cufet-level error (bad syntax, a type
    // error, an uncaught exception) is part of the ANSWER here, not an exception to propagate
    // into JavaScript: the playground wants to show the message the same way a terminal would.
    [JSExport]
    internal static string Run(string source)
    {
        var output = new StringWriter();
        try
        {
            var tokens  = new CufetLexer(source).Tokenize();
            var program = new Parser(tokens).Parse();
            // ★★ SourceDirectory is what lets `Pull a ‹name›.` reach ‹name›.cufe — without it the
            // loader never runs and a book in another file is refused as "there is nothing named
            // ... to pull". Measured: the playground could not demonstrate the module system at
            // all, and `examples/language/ledger.cufe` failed on the page that exists to show it.
            //
            // ⚠ "." is the runtime's working directory, which is where the worker places seeded
            // files. A PASTED program has no directory of its own, so "." plays the part of the
            // one it would live in — its books sit beside it there, exactly as siblings do in a
            // checkout.
            var checker = new TypeChecker { SourceDirectory = "." };
            program = checker.Check(program);
            // All three streams are passed explicitly. Leaving any of them null falls back to
            // Console, and Console.In throws PlatformNotSupported in a browser — there is no
            // stdin to read. Error goes to the same buffer as output so nothing a program
            // writes can silently vanish.
            //
            // ⚠⚠ NO depth limit of its own any more, and that is the change: the interpreter now
            // asks the REAL stack whether it can descend (see OutOfStack), so a browser needs no
            // guess about how deep is survivable.
            //
            // ★ What the guess cost: a count cannot know what a call costs, and the cost varies
            // hugely with the program. Measured here, a minimal recursive function survived depth
            // 275 while one nesting a few calls inside arithmetic died between 140 and 150 — so
            // the number picked to be safe for the second (100) refused perfectly good programs
            // of the first kind, and STILL did not save `examples/algorithms/sudoku.cufe`, which
            // never reached a call depth of 100 at all. Its stack went on nested statement frames
            // BETWEEN calls, which a call count cannot see.
            //
            // ★★ Measured after: sudoku now runs further than it ever did here, stops with an
            // ordinary Cufet refusal, and leaves the runtime answering — where it used to take
            // the whole page down with nothing shown at all.
            //
            // All three streams are passed explicitly. Leaving any of them null falls back to
            // Console, and Console.In throws PlatformNotSupported in a browser — there is no
            // stdin to read. Error goes to the same buffer as output so nothing a program writes
            // can silently vanish.
            new CufetInterpreter(output, new StringReader(""), output).Execute(program);
        }
        catch (Exception e) when (e is LexerException or ParseException or TypeException or RuntimeException)
        {
            output.Write(e.Message);
            if (!e.Message.EndsWith('\n')) output.Write('\n');
        }
        return output.ToString();
    }

    // The same front end the editor extension calls, so the playground's squiggles and the
    // editor's are the same diagnostics from the same code — never a re-implementation that
    // could drift.
    //
    // ★★ One JSON object PER LINE, which is the shape `cufet check --json` already emits and the
    // VS Code extension already parses (editors/vscode/extension.js, parseDiagnostics). Matching it
    // means the two editors cannot disagree about what a program's problems are.
    //
    // ⚠ No column, deliberately. The CLI emits one and the extension IGNORES it: a squiggle is
    // drawn from the line's first non-blank character to its end, because a caret under one
    // character is easy to miss and a zero-width range is invisible. Emitting a field nothing
    // reads would be inventing surface.
    //
    // "" when the program is clean.
    [JSExport]
    internal static string Check(string source)
    {
        var reported = new System.Text.StringBuilder();
        IReadOnlyList<Diagnostic> style;
        var checker = new TypeChecker { SourceDirectory = "." };

        try
        {
            var tokens  = new CufetLexer(source).Tokenize();
            var parser  = new Parser(tokens);
            var program = parser.Parse();
            checker.Check(program);
            // ⚠ Style is judged only on a program that PARSES AND TYPE-CHECKS, exactly as the CLI
            // does it. Advising someone on how a line reads while it is still wrong would bury the
            // thing they actually need to fix.
            style = Linter.Lint(tokens, parser.StatementStarts, program);
        }
        catch (Exception e) when (e is LexerException or ParseException or TypeException)
        {
            return Line(LineOf(e), "error", e.Message);
        }

        foreach (var d in checker.Diagnostics.Items)
            reported.Append(Line(d.Line, d.SeverityName, d.Message));
        foreach (var d in style)
            reported.Append(Line(d.Line, d.SeverityName, d.Message));
        return reported.ToString();
    }

    // Written by hand rather than with JsonSerializer. Reflection-based serialization needs
    // metadata that trimming removes, so it throws PlatformNotSupported in a trimmed WASM build —
    // and three fields do not justify a source-generated context, let alone shipping
    // System.Text.Json to every visitor.
    // ★ A raw string literal, so not one quote in here is escaped. The previous form was a
    // chain of "\"line\":" fragments — correct, unreadable, and the exact shape that gets
    // mangled by any tool that touches this file through a shell.
    //
    // ⚠ JsonString already returns its own surrounding quotes, so `message` takes none here.
    private static string Line(int line, string severity, string message) =>
        $$"""{"line":{{line}},"severity":"{{severity}}","message":{{JsonString(message)}}}"""
        + "\n";
    // The name-kind layer the TextMate grammar cannot produce — the same walk `cufet tokens` runs,
    // so the page and the editor colour a name from one source of truth. One JSON object per line,
    // matching the CLI's shape. A program that does not type-check has no reliable kinds to report,
    // so it yields nothing rather than guesses: the squiggles from Check are the answer then.
    [JSExport]
    internal static string Tokens(string source)
    {
        try
        {
            var tokens  = new CufetLexer(source).Tokenize();
            var program = new Parser(tokens).Parse();
            // See Run. Colouring a name that a loaded book declared needs the same loading.
            var checker = new TypeChecker { SourceDirectory = "." };
            checker.Check(program);

            var sb = new System.Text.StringBuilder();
            foreach (var t in SemanticTokenizer.Collect(program, tokens, checker))
                sb.Append("{\"line\":").Append(t.Line)
                  .Append(",\"column\":").Append(t.Column)
                  .Append(",\"length\":").Append(t.Length)
                  .Append(",\"kind\":\"").Append(SemanticTokenLegend.NameOf(t.Kind))
                  .Append("\"}\n");
            return sb.ToString();
        }
        catch (Exception e) when (e is LexerException or ParseException or TypeException)
        {
            return "";
        }
    }

    /// <summary>
    /// Puts a file where a program can read it. Returns "" on success, or a message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★★ There is ALREADY a filesystem here, and this was measured before anything was built: a
    /// Cufet program can write a file and read it back under wasm today, and listing, appending and
    /// existence checks all work. Emscripten gives the runtime an in-memory filesystem and .NET's
    /// File APIs sit on it. Nothing had to be implemented.
    /// </para>
    /// <para>
    /// What was missing is that it starts EMPTY. `examples/parsing/config.cufe` reads
    /// `examples/assets/config.txt`, which exists in the repository and not in a browser, so the
    /// example met a truthful `not found` and could not demonstrate itself. This is how the host
    /// puts those files in before a program looks for them.
    /// </para>
    /// <para>
    /// ⚠ The parent directories are created here, and that is NOT the language being lenient — it
    /// is the HOST placing a file, the same as a checkout having made the directory already.
    /// Cufet has no directory-creating operation at all, and `Write` to a path whose directory is
    /// missing fails on a real OS exactly as it does here. That agreement is worth keeping.
    /// </para>
    /// <para>
    /// ★ Failure is REPORTED, never thrown. Seeding runs while the worker boots; a throw there
    /// would take the runtime down over a missing text file and leave the visitor with a dead page
    /// instead of a playground that merely cannot run two of the examples.
    /// </para>
    /// </remarks>
    [JSExport]
    internal static string PlaceFile(string path, string content)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, content);
            return "";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
    private static string JsonString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 16).Append('"');
        foreach (char c in s)
            sb.Append(c switch
            {
                '"'  => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => "\\u" + ((int)c).ToString("x4"),
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }

    // Lexer and parse errors carry a line. Type errors do not — TypeException holds only a
    // message — but nearly all of them come from FormatTypeError, which always writes
    // "Here on line N, you're trying to ...". That anchor is tried first because a message may
    // name several lines and the violation is the one to underline.
    private static int LineOf(Exception e) => e switch
    {
        LexerException le => le.Line,
        ParseException pe => pe.Line,
        _ => LineFromMessage(e.Message),
    };

    private static int LineFromMessage(string message)
    {
        var anchored = System.Text.RegularExpressions.Regex.Match(message, @"Here on line (\d+)");
        if (anchored.Success) return int.Parse(anchored.Groups[1].Value);
        var mentioned = System.Text.RegularExpressions.Regex.Match(
            message, @"\bline (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return mentioned.Success ? int.Parse(mentioned.Groups[1].Value) : 1;
    }
}

public static class Program
{
    // browser-wasm still wants an entry point; the real surface is the [JSExport]s above.
    public static void Main() { }
}
