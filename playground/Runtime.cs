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
            program = new TypeChecker().Check(program);
            // All three streams are passed explicitly. Leaving any of them null falls back to
            // Console, and Console.In throws PlatformNotSupported in a browser — there is no
            // stdin to read. Error goes to the same buffer as output so nothing a program
            // writes can silently vanish.
            //
            // ⚠⚠ A depth limit this host can SURVIVE, and it is the difference between a message
            // and a dead page. The CLI runs the interpreter on a 16 MB thread (RunOnLargeStack) and
            // never approaches the 1000 default; a browser gives whatever stack wasm has, and the
            // real stack dies first. A .NET StackOverflowException cannot be caught, so the catch
            // below never runs, the Mono runtime is gone, and the visitor sees NOTHING — not an
            // error, not the output printed before it, not the program they typed.
            //
            // ★ MEASURED, not chosen: a minimal recursive function returns at depth 275 and kills
            // the runtime at 300. But the ceiling is not one number — it is however many C# frames
            // one Cufet call costs, and that varies with the program: a body that nests a few calls
            // inside arithmetic dies between 140 and 150. 100 sits under both.
            //
            // ⚠⚠ So this catches RUNAWAY RECURSION and nothing more, and the distinction is worth
            // stating because the obvious reading is wrong. Measured: depths 300, 900 and 5000 now
            // print this message instead of killing the page. `examples/algorithms/sudoku.cufe`
            // still kills it, and lowering the number would not save it — sudoku never reaches a
            // call depth of 100. Its stack goes on nested Execute frames BETWEEN calls (loops and
            // conditionals inside the body), and statement nesting is not what this counts.
            //
            // ★ The instrument that would cover both is RuntimeHelpers.TryEnsureSufficientExecution-
            // Stack(), which measures remaining stack rather than proxying it with a call count, and
            // which was MEASURED to work under wasm here — it reported exhaustion at depth 31,213
            // without killing the runtime. Using it means checking inside Execute/Evaluate, which is
            // a change to the interpreter and to the CLI, so it is not folded in here unasked.
            //
            // ⚠ Raising the wasm stack was tried and does NOT work. `-s STACK_SIZE=` was verified
            // to reach the emcc link (in emcc-link.rsp, via the SDK's own $(EmccStackSize)), and
            // 1MB and 16MB produce the SAME ceiling — so the emscripten stack is not the binding
            // limit and this cannot be fixed from the csproj.
            new CufetInterpreter(output, new StringReader(""), output, maxCallDepth: 100)
                .Execute(program);
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
    // could drift. One JSON object, or "" when the program is clean.
    [JSExport]
    internal static string Check(string source)
    {
        try
        {
            var tokens  = new CufetLexer(source).Tokenize();
            var program = new Parser(tokens).Parse();
            program = new TypeChecker().Check(program);
            return "";
        }
        catch (Exception e) when (e is LexerException or ParseException or TypeException)
        {
            // Written by hand rather than with JsonSerializer. Reflection-based serialization
            // needs metadata that trimming removes, so it throws PlatformNotSupported in a
            // trimmed WASM build — and three fields do not justify a source-generated context,
            // let alone shipping System.Text.Json to every visitor.
            return "{\"line\":" + LineOf(e)
                 + ",\"severity\":\"error\",\"message\":" + JsonString(e.Message) + "}";
        }
    }

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
            var checker = new TypeChecker();
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
