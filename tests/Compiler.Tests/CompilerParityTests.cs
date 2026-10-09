using Cufet.Interpreter;
using Xunit;
using Xunit.Abstractions;
using static Cufet.Compiler.Tests.LexerParityTests;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// The compiler written in Cufet, built once — and the C runtime every program it builds links,
/// written once beside it for `--runtime`.
/// </summary>
public sealed class CompiledCufetCompiler : CompiledFrontEnd, IDisposable
{
    public string RuntimeFolder { get; }

    public CompiledCufetCompiler() : base("compiler")
    {
        // ★ The runtime is the same for every program, so any program's split gives it.
        RuntimeFolder = Directory.CreateTempSubdirectory("cufet-compiler-runtime-").FullName;
        var program = new TypeChecker().Check(new Parser(new CufetLexer("State 1.").Tokenize()).Parse());
        var (header, runtime, _) = new CodeGenerator().GenerateSplit(program);
        File.WriteAllText(Path.Combine(RuntimeFolder, RuntimeSplit.HeaderFileName), header);
        File.WriteAllText(Path.Combine(RuntimeFolder, RuntimeSplit.SourceFileName), runtime);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(RuntimeFolder, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// The compiler written in Cufet (`self-hosting/Cufet/compiler/compiler.cufe`) builds programs that
/// print what the interpreter prints.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE ANSWER KEY IS WHAT THE PROGRAM PRINTS, the way the oracle holds the two backends — the
/// user's ruling, 2026-10-07. The C this compiler writes may differ from `CodeGenerator`'s freely;
/// each program it builds is run, and its output compared byte for byte with the interpreter's.
/// </para>
/// <para>
/// ★★ `unsupported` IS NOT A DIFFERENCE, AND IT IS NOT AGREEMENT — as in <see cref="CheckerParityTests"/>.
/// The compiler says it of any program holding something it cannot build yet, and the FLOOR, raised as
/// it grows, is what holds it to its growth. Anything else — a refusal, C that gcc rejects, a program
/// that prints something else — fails the test.
/// </para>
/// </remarks>
public class CompilerParityTests(ITestOutputHelper output, CompiledCufetCompiler compiled)
    : PipelineTestBase, IClassFixture<CompiledCufetCompiler>
{
    /// <summary>How many hand-written programs must build and print alike. Raised as it grows; never lowered.</summary>
    private const int BuiltFloor = 113;

    private static string[] PreludeArgs =>
        ["--prelude", Path.Combine(RepoRoot, "src", "Interpreter", "Prelude").Replace('\\', '/')];

    /// <summary>
    /// Small programs the compiler must BUILD — what it has learned to do is written down here.
    /// </summary>
    private static readonly string[] Built =
    [
        // Text, numbers and the arithmetic on them.
        "State \"hello\".",
        "State 42.",
        "State 1.5.",
        "State 1.50.",
        "State 0.1 + 0.2.",
        "State 1234567890123456789.",
        "State 7 - 10.",
        "State -3.",
        "State 2 + 3 * 4.",
        "State (2 + 3) * 4.",
        "State 10 / 4.",
        "State 1 / 3.",
        "State 7 % 3.",
        "State -7 % 3.",
        // Names, defined and reassigned.
        "Define total as 5.\nState total.",
        "Define total as 5.\nThe total becomes total * 2.\nState total.",
        "Define word as \"x\".\nThe word becomes word joined to \"y\".\nState word.",
        "Define total as 0.5.\nThe total becomes total + 0.25.\nState total.\nState total * 4.",
        // Text made from other values.
        "Define first-name as \"Grace\".\nState \"hello, {first-name}\".",
        "Define count as 3.\nState \"{count} rabbits, {count * 2} ears\".",
        "State \"a\" joined to \"b\" joined to \"c\".",
        "Define score as 95.\nState \"Player: \" joined to score converted to text.",
        "Define ready as 2 is greater than 1.\nState \"ready: {ready}\".",
        // Facts, and comparing.
        "State true.",
        "State false.",
        "State 1 is 1.",
        "State 1 is not 2.",
        "State 3 is greater than 2.",
        "State 3 is less than 2.",
        "State 3 is not less than 3.",
        "State 3 >= 4.",
        "State 3 is not greater than 2.",
        "State 2 <= 2.",
        "State \"cat\" is \"cat\".",
        "State \"cat\" is not \"dog\".",
        "State true and false.",
        "State true or false.",
        "State not true.",
        // What a text literal can hold.
        "State \"tab\\there\".",
        "State \"line one\\nline two\".",
        "State \"a \\\"quoted\\\" word\".",
        "State \"back\\\\slash\".",
        "State \"café — naïve\".",
        "State <<C:\\Users\\me>>.",
        // Choosing and repeating: If, While, Repeat … until, Stop, Skip, Increment, and Judge on values.
        "Define n as 0.\nIf n is 0:\n    State \"zero\".\nDone.\nOtherwise if n is 1:\n    State \"one\".\nDone.\nOtherwise:\n    State \"many\".\nDone.",
        "Define n as 1.\nIf n is 0:\n    State \"zero\".\nDone.\nOtherwise if n is 1:\n    State \"one\".\nDone.\nOtherwise:\n    State \"many\".\nDone.",
        "Define n as 5.\nIf n is 0:\n    State \"zero\".\nDone.\nOtherwise if n is 1:\n    State \"one\".\nDone.\nOtherwise:\n    State \"many\".\nDone.",
        "Define n as 5.\nIf n is greater than 3, state \"big\".\nIf n is less than 3, state \"small\".",
        "Define n as 4.\nIf n is greater than 3 and n is less than 10:\n    State \"between\".\nDone.\nIf n is 1 or n is 4, state \"one or four\".",
        "Define n as 0.\nWhile n is less than 5, repeat:\n    Increment n by 1.\n    State n.\nDone.",
        "Define n as 0.\nWhile true, repeat:\n    Increment n by 1.\n    If n is 2, skip.\n    If n is 5, stop.\n    State n.\nDone.\nState \"done at {n}\".",
        "Define n as 10.\nRepeat:\n    Decrement n by 3.\n    State n.\nUntil n is less than 0.",
        "Define n as 0.\nRepeat:\n    Increment n by 1.\n    If n is 3, stop.\nUntil n is 10.\nState n.",
        "Define n as 0.\nRepeat:\n    Increment n by 1.\n    If n is 2, skip.\n    State n.\nUntil n is not less than 4.",
        "Define row as 1.\nWhile row is not greater than 3, repeat:\n    Define column as 1.\n    Define line as \"\".\n    While column is not greater than row, repeat:\n        The line becomes line joined to \"*\".\n        Increment column by 1.\n    Done.\n    State line.\n    Increment row by 1.\nDone.",
        "Define n as 1.\nWhile n is not greater than 15, repeat:\n    If n % 15 is 0:\n        State \"FizzBuzz\".\n    Done.\n    Otherwise if n % 3 is 0:\n        State \"Fizz\".\n    Done.\n    Otherwise if n % 5 is 0:\n        State \"Buzz\".\n    Done.\n    Otherwise:\n        State n.\n    Done.\n    Increment n by 1.\nDone.",
        "Define word as \"cd\".\nJudge word, where it is:\n    \"cd\", state \"change\".\n    \"ls\" or \"dir\", state \"list\".\n    Otherwise, state \"other\".\nDone.",
        "Define word as \"dir\".\nJudge word, where it is:\n    \"cd\", state \"change\".\n    \"ls\" or \"dir\", state \"list\".\n    Otherwise, state \"other\".\nDone.",
        "Define word as \"rm\".\nJudge word, where it is:\n    \"cd\", state \"change\".\n    \"ls\" or \"dir\", state \"list\".\n    Otherwise, state \"other\".\nDone.",
        "Define n as 2.5.\nJudge n, where it is:\n    1, state \"one\".\n    2.50, state \"two and a half\".\n    Otherwise, state \"else\".\nDone.",
        "Define n as 7.\nJudge n, where it is:\n    1, state \"one\".\n    2, state \"two\".\n    Otherwise, state \"neither: {it}\".\nDone.\nState \"after\".",
        "Define flag as false.\nJudge flag, where it is:\n    true, state \"yes\".\n    Otherwise, state \"no\".\nDone.",
        "Define n as 3.\nJudge n, where it is:\n    3:\n        State \"three\".\n        State it * 2.\n    Done.\n    Otherwise, state it.\nDone.",
        "Define n as 0.\nWhile n is less than 10, repeat:\n    Increment n by 1.\n    Judge n, where it is:\n        4, stop.\n        2, skip.\n        Otherwise, state it.\n    Done.\nDone.\nState n.",
        "Define n as 1.\nJudge n, where it is:\n    1:\n        Define word as \"outer\".\n        Judge word, where it is:\n            \"outer\", state \"inner sees {it}\".\n            Otherwise, state \"no\".\n        Done.\n        State it.\n    Done.\n    Otherwise, state \"no\".\nDone.",
        "If true:\n    Define x as 1.\n    State x.\nDone.\nIf true:\n    Define x as \"one\".\n    State x.\nDone.",
        "Define x as 1.\nIf true:\n    Define a shadow x as \"shadowed\".\n    State x.\nDone.\nState x.",
        "Define total as 0.\nDefine n as 1.\nWhile n is not greater than 100, repeat:\n    Increment total by n.\n    Increment n by 1.\nDone.\nState total.",
        "Define n as 27.\nDefine steps as 0.\nWhile n is not 1, repeat:\n    If n % 2 is 0, the n becomes n / 2.\n    Otherwise, the n becomes 3 * n + 1.\n    Increment steps by 1.\nDone.\nState \"steps: {steps}\".",
        // Functions: parameters, giving back, calls as values and as statements, recursion, and calling one declared further down.
        "Bind number to doubled, given (the number n):\n    Return n * 2.\nDone.\nState cast doubled on (21).",
        "Bind number to seven:\n    Return 7.\nDone.\nState cast seven.",
        "Bind text to greeting, given (the text who, the fact loud):\n    If loud, return \"HELLO {who}\".\n    Return \"hello {who}\".\nDone.\nState cast greeting on (\"grace\", true).\nState cast greeting on (\"hopper\", false).",
        "Bind void to shout, given (the text said):\n    State said joined to \"!\".\nDone.\nCast shout on (\"hey\").\nCast shout on (\"you\").",
        "Bind number to factorial, given (the number n):\n    If n is less than 2, return 1.\n    Return n * cast factorial on (n - 1).\nDone.\nState cast factorial on (10).",
        "Bind number to fibonacci, given (the number n):\n    If n is less than 2, return n.\n    Return cast fibonacci on (n - 1) + cast fibonacci on (n - 2).\nDone.\nState cast fibonacci on (20).",
        "State cast later on (4).\nBind number to later, given (the number n):\n    Return n + 1.\nDone.",
        "Bind fact to is-even, given (the number n):\n    If n is 0, return true.\n    Return cast is-odd on (n - 1).\nDone.\nBind fact to is-odd, given (the number n):\n    If n is 0, return false.\n    Return cast is-even on (n - 1).\nDone.\nState cast is-even on (10).\nState cast is-odd on (7).",
        "Bind number to largest, given (the number first, the number second):\n    If first is greater than second, return first.\n    Return second.\nDone.\nState cast largest on (3, 9).\nState cast largest on (cast largest on (1, 2), 0).",
        "Bind void to count-down, given (the number count):\n    Define n as count.\n    While n is greater than 0, repeat:\n        State n.\n        Decrement n by 1.\n    Done.\n    State \"liftoff\".\nDone.\nCast count-down on (3).",
        "Bind void to early, given (the number n):\n    If n is 1:\n        State \"one\".\n        Return.\n    Done.\n    State \"not one\".\nDone.\nCast early on (1).\nCast early on (2).",
        "Bind text to label, given (the number n):\n    Judge n, where it is:\n        1, return \"one\".\n        2, return \"two\".\n        Otherwise, return \"many\".\n    Done.\nDone.\nState cast label on (1).\nState cast label on (2).\nState cast label on (9).",
        "Bind number to sum-to, given (the number n):\n    Define total as 0.\n    Define i as 1.\n    While i is not greater than n, repeat:\n        Increment total by i.\n        Increment i by 1.\n    Done.\n    Return total.\nDone.\nState \"sum: {cast sum-to on (100)}\".",
        "Bind number to power, given (the number base, the number exponent):\n    If exponent is 0, return 1.\n    Return base * cast power on (base, exponent - 1).\nDone.\nState cast power on (2, 10).\nState cast power on (1.5, 3).",
        "Bind number to gcd, given (the number left, the number right):\n    If right is 0, return left.\n    Return cast gcd on (right, left % right).\nDone.\nState cast gcd on (1071, 462).",
        "Bind fact to is-prime, given (the number n):\n    If n is less than 2, return false.\n    Define d as 2.\n    While d * d is not greater than n, repeat:\n        If n % d is 0, return false.\n        Increment d by 1.\n    Done.\n    Return true.\nDone.\nDefine n as 1.\nWhile n is less than 30, repeat:\n    If cast is-prime on (n), state n.\n    Increment n by 1.\nDone.",
        "Bind text to repeated, given (the text piece, the number times):\n    Define out as \"\".\n    Define i as 0.\n    While i is less than times, repeat:\n        The out becomes out joined to piece.\n        Increment i by 1.\n    Done.\n    Return out.\nDone.\nState cast repeated on (\"ab\", 3).",
        "Bind number to helper, given (the number n):\n    Return n + 1.\nDone.\nBind number to twice, given (the number helper):\n    Return helper * 2.\nDone.\nState cast twice on (cast helper on (20)).",
        "Bind void to report, given (the number n):\n    State \"n is {n}\".\nDone.\nDefine n as 5.\nCast report on (n * 2).\nState n.",
        // Series of numbers, text and facts: made, ranged, looped over, reached into, changed, shared, and passed to and from functions.
        "Define scores as a series of number with (3, 1, 2).\nState scores.",
        "Define names as a series of text with (\"grace\", \"hopper\").\nState names.",
        "Define flags as a series of fact with (true, false).\nState flags.",
        "Define empty as a series of number.\nState empty.\nState the number of empty.",
        "Define scores as a series of number with (3, 1, 2).\nInsert 7 into scores.\nState the number of scores.\nState scores.",
        "Define scores as a series of number with (3, 1, 2).\nState item 2 of scores.\nState the first of scores.\nState the last of scores.",
        "Define scores as a series of number with (5, 6).\nInsert 4 into the start of scores.\nInsert 9 after item 1 of scores.\nState scores.",
        "Define scores as a series of number with (4, 9, 5, 6, 1).\nRemove item 2 from scores.\nRemove 6 from scores.\nState scores.\nRemove the last from scores.\nState scores.",
        "Define scores as a series of number with (1, 2, 3).\nThe item 2 of scores becomes 20.\nThe first of scores becomes 10.\nThe last of scores becomes 30.\nState scores.",
        "Define scores as a series of number with (3, 1, 2).\nFor each n in scores, repeat:\n    State n * 10.\nDone.",
        "Define names as a series of text.\nInsert \"grace\" into names.\nInsert \"hopper\" into names.\nFor each in names, repeat:\n    State \"hello, {it}\".\nDone.",
        "For each n in range 1 to 5, repeat:\n    State n.\nDone.",
        "For each n in range 1 to 10 counting by 3, repeat:\n    State n.\nDone.",
        "Define total as 0.\nDefine limit as 0.\nFor each n in range 1 to limit, repeat:\n    Increment total by 1.\nDone.\nState total.",
        "Define hundred as range 1 to 100.\nState the number of hundred.\nState the last of hundred.",
        "Define s as a series of number with (1, 2).\nDefine t as s.\nInsert 3 into t.\nState s.",
        "Define total as 0.\nFor each n in range 1 to 10, repeat:\n    If n % 2 is 0, skip.\n    If n is 9, stop.\n    Increment total by n.\nDone.\nState total.",
        "Define grid as a series of text.\nFor each row in range 1 to 3, repeat:\n    For each column in range 1 to 3, repeat:\n        Insert \"{row}{column}\" into grid.\n    Done.\nDone.\nState grid.",
        "Bind number to sum, given (the series of number xs):\n    Define total as 0.\n    For each in xs, repeat:\n        Increment total by it.\n    Done.\n    Return total.\nDone.\nState cast sum on (a series of number with (1, 2, 3, 4)).",
        "Bind series of number to evens-to, given (the number limit):\n    Define found as a series of number.\n    For each n in range 1 to limit, repeat:\n        If n % 2 is 0, insert n into found.\n    Done.\n    Return found.\nDone.\nState cast evens-to on (10).",
        "Bind void to grow, given (the series of text names):\n    Insert \"added\" into names.\nDone.\nDefine names as a series of text with (\"first\").\nCast grow on (names).\nState names.",
        "Define words as a series of text with (\"a\", \"b\", \"c\").\nDefine together as \"\".\nFor each word in words, repeat:\n    The together becomes together joined to word.\nDone.\nState together.",
        "Define xs as a series of number with (1, 2, 3).\nDefine copied as a series of number.\nFor each in xs, repeat:\n    Insert it * 2 into copied.\n    The first of xs becomes 0.\nDone.\nState xs.\nState copied.",
        "Define primes as a series of number.\nFor each candidate in range 2 to 30, repeat:\n    Define is-prime as true.\n    For each p in primes, repeat:\n        If candidate % p is 0:\n            The is-prime becomes false.\n            Stop.\n        Done.\n    Done.\n    If is-prime, insert candidate into primes.\nDone.\nState primes.",
        "Define xs as a series of number with (3, 1, 2).\nState item (the number of xs) of xs.\nState item 1 + 1 of xs.",
    ];

    [Fact]
    public void TheCompiledCompiler_BuildsProgramsThatPrintWhatTheInterpreterPrints()
    {
        // ★ Under TestScratch, which the antivirus exclusion covers — every program built here runs.
        var dir = Directory.CreateDirectory(
            Path.Combine(TestScratch.Root, "cufet-selfbuilt-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var sources = Built.Select((source, i) => (Name: $"p{i + 1:000}.cufe", Source: source)).ToList();
            foreach (var (name, source) in sources) File.WriteAllText(Path.Combine(dir, name), source);

            var runtime = compiled.RuntimeFolder.Replace('\\', '/');
            var got = ByFile(Run(compiled.Exe, ["--runtime", runtime, .. PreludeArgs, .. sources.Select(s => s.Name)], dir));

            int matched = 0;
            var problems = new List<string>();
            var unsupported = new List<string>();
            foreach (var (name, source) in sources)
            {
                string said = got.TryGetValue(name, out var lines) && lines.Count > 0 ? lines[0] : "<nothing printed>";
                if (said.StartsWith("unsupported\t", StringComparison.Ordinal))
                {
                    unsupported.Add($"{name}\t{said["unsupported\t".Length..]}");
                    continue;
                }
                if (said != "built")
                {
                    problems.Add($"{name}: {said}\n    {source.Replace("\n", "\n    ")}");
                    continue;
                }
                string program = Path.Combine(dir, Path.GetFileNameWithoutExtension(name)
                    + (OperatingSystem.IsWindows() ? ".exe" : ""));
                string want = InterpretRaw(source);
                string have;
                try { have = RunBinary(program); }
                catch (Xunit.Sdk.XunitException died) { problems.Add($"{name}: {died.Message}"); continue; }
                if (have == want) matched++;
                else problems.Add($"{name} printed differently:\n    interpreted: {Show(want)}\n    built:       {Show(have)}");
            }

            output.WriteLine($"{matched} of {sources.Count} built and print alike; {unsupported.Count} not yet:");
            foreach (var line in unsupported) output.WriteLine("    " + line);
            Assert.True(problems.Count == 0,
                $"{problems.Count} of {sources.Count} went wrong:\n{string.Join("\n", problems.Take(20))}");
            Assert.True(matched >= BuiltFloor, $"only {matched} built and print alike, and the floor is {BuiltFloor}.");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>
    /// Programs the interpreter stops with an error, each with words the error must carry.
    /// </summary>
    /// <remarks>
    /// ⚠ The C# compiler does not stop these — its loop over a series runs over the items there when
    /// it began, whatever the body does to the series (measured 2026-10-08). GRAMMAR says the loop
    /// refuses it, and the interpreter does; the oracle cannot see the difference, because it does
    /// not run programs that end in an error. So this compiler is held to the interpreter here.
    /// </remarks>
    private static readonly (string Source, string Says)[] Stopped =
    [
        ("Define xs as a series of number with (1, 2, 3).\nFor each in xs, repeat:\n    State it.\n    Insert it into xs.\nDone.\nState xs.",
         "was modified during a for-each loop"),
        ("Define xs as a series of number with (1, 2, 3).\nFor each n in xs, repeat:\n    State n.\n    If n is 2, remove the first from xs.\nDone.\nState xs.",
         "was modified during a for-each loop"),
        ("Define xs as a series of number with (1, 2, 3).\nFor each n in xs, repeat:\n    State n.\n    If n is 3, insert 4 into xs.\nDone.\nState xs.",
         "was modified during a for-each loop"),
    ];

    [Fact]
    public void TheCompiledCompiler_BuildsProgramsThatStopWhereTheInterpreterStops()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(TestScratch.Root, "cufet-selfstopped-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var sources = Stopped.Select((s, i) => (Name: $"s{i + 1:000}.cufe", s.Source, s.Says)).ToList();
            foreach (var (name, source, _) in sources) File.WriteAllText(Path.Combine(dir, name), source);

            var runtime = compiled.RuntimeFolder.Replace('\\', '/');
            var got = ByFile(Run(compiled.Exe, ["--runtime", runtime, .. PreludeArgs, .. sources.Select(s => s.Name)], dir));

            var problems = new List<string>();
            foreach (var (name, source, says) in sources)
            {
                string said = got.TryGetValue(name, out var lines) && lines.Count > 0 ? lines[0] : "<nothing printed>";
                if (said != "built") { problems.Add($"{name}: {said}"); continue; }
                // ⚠ Each must stop the interpreter too, or it tests nothing.
                Assert.Throws<RuntimeException>(() => InterpretRaw(source));

                var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(dir, Path.GetFileNameWithoutExtension(name)
                    + (OperatingSystem.IsWindows() ? ".exe" : "")))
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
                    UseShellExecute = false,
                };
                using var process = System.Diagnostics.Process.Start(psi)!;
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                // What it printed before it stopped, then that it stopped, and why.
                string want = InterpretThroughFaultRaw(source);
                if (stdout.Result != want)
                    problems.Add($"{name} printed differently before stopping:\n    interpreted: {Show(want)}\n    built:       {Show(stdout.Result)}");
                else if (process.ExitCode == 0)
                    problems.Add($"{name} did not stop: it exited 0");
                else if (!stderr.Result.Contains(says, StringComparison.Ordinal))
                    problems.Add($"{name} stopped without saying '{says}': {stderr.Result}");
            }
            Assert.True(problems.Count == 0,
                $"{problems.Count} of {sources.Count} went wrong:\n{string.Join("\n", problems)}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>Output with its line breaks and tabs shown, so a difference in either is visible.</summary>
    private static string Show(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
