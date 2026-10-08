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
    private const int BuiltFloor = 69;

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

    /// <summary>Output with its line breaks and tabs shown, so a difference in either is visible.</summary>
    private static string Show(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
