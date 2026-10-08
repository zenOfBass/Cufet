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
    private const int BuiltFloor = 44;

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
