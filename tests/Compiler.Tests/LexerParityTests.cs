using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// The lexer written in Cufet (`tools/lexer/`) gives the same tokens as the one in C#.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE ANSWER KEY IS <c>src/Lexer/Lexer.cs</c> ITSELF, called in-process. The Cufet lexer prints
/// one line per token — type, line, column, lexeme — and each file's lines are compared with the
/// same lines built from <c>new Lexer(source).Tokenize()</c>. A mismatch names the file and the first
/// token that differs, not the whole stream.
/// </para>
/// <para>
/// ⚠ COLUMNS: the C# lexer counts UTF-16 units and Cufet counts characters, so a character outside
/// the Basic Multilingual Plane pushes every later C# column on its line one further. The C# side is
/// converted here rather than the Cufet side taught to count wrongly. It is a difference in how the
/// two languages NUMBER a line, not in where a token is.
/// </para>
/// <para>
/// ⚠ A test that can vanish is not a test: every comparison asserts a floor on how many files it
/// compared, and the refusals assert that the C# lexer really did refuse each broken source.
/// </para>
/// </remarks>
public class LexerParityTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cufet.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    private static string CufetExe => Path.Combine(
        RepoRoot, "src", "App", "bin", "Debug", "net10.0",
        "Cufet.App" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));

    private static string LexerDir => Path.Combine(RepoRoot, "tools", "lexer");

    /// <summary>Every Cufet file in the repository, relative to its root, with forward slashes.</summary>
    private static List<string> Corpus() =>
        new[] { "examples", "tools", Path.Combine("src", "Interpreter", "Prelude") }
            .Select(d => Path.Combine(RepoRoot, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.cufe", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(RepoRoot, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    // ── The C# side ─────────────────────────────────────────────────────────

    /// <summary>What the Cufet lexer should print for <paramref name="source"/>.</summary>
    private static List<string> Expected(string source)
    {
        var lines = source.Split('\n');
        try
        {
            return new Cufet.Lexer.Lexer(source).Tokenize()
                .Select(t => $"{t.Type}\t{t.Line}\t{CodePointColumn(lines, t.Line, t.Column)}\t{Escaped(t.Lexeme)}")
                .ToList();
        }
        catch (Cufet.Lexer.LexerException e)
        {
            return [$"error\t{e.Line}\t{CodePointColumn(lines, e.Line, e.Column)}\t{e.Detail}"];
        }
    }

    /// <summary>A UTF-16 column, counted in characters instead.</summary>
    private static int CodePointColumn(string[] lines, int line, int column)
    {
        if (line - 1 >= lines.Length) return column;
        string text = lines[line - 1];
        int pairs = 0;
        for (int i = 0; i + 1 < text.Length && i < column - 1; i++)
            if (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1])) pairs++;
        return column - pairs;
    }

    private static string Escaped(string lexeme) =>
        lexeme.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");

    // ── The Cufet side ──────────────────────────────────────────────────────

    private static string Run(string exe, IEnumerable<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            WorkingDirectory       = workingDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(300_000);
        Assert.True(p.ExitCode == 0, $"the lexer exited {p.ExitCode}:\n{stderr.Result}");
        return stdout;
    }

    /// <summary>The lexer's output, split at its `file` headers.</summary>
    private static Dictionary<string, List<string>> ByFile(string output)
    {
        var files = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (var raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("file\t", StringComparison.Ordinal))
                files[line["file\t".Length..]] = current = [];
            else if (current is not null && line.Length > 0)
                current.Add(line);
        }
        return files;
    }

    /// <summary>The first difference in each file, or nothing when all of them agree.</summary>
    private static List<string> Differences(
        IEnumerable<(string Name, string Source)> files, Dictionary<string, List<string>> got)
    {
        var problems = new List<string>();
        foreach (var (name, source) in files)
        {
            var want = Expected(source);
            if (!got.TryGetValue(name, out var have))
            {
                problems.Add($"{name}: the Cufet lexer printed nothing for it");
                continue;
            }
            for (int i = 0; i < Math.Max(want.Count, have.Count); i++)
            {
                string w = i < want.Count ? want[i] : "<no more tokens>";
                string h = i < have.Count ? have[i] : "<no more tokens>";
                if (w == h) continue;
                problems.Add($"{name}, token {i + 1}:\n    C#:    {w}\n    Cufet: {h}");
                break;
            }
        }
        return problems;
    }

    private static IEnumerable<(string, string)> WithSources(IEnumerable<string> names) =>
        names.Select(n => (n, File.ReadAllText(Path.Combine(RepoRoot, n))));

    // ── The tests ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryFileInTheRepository_LexesTheSameInCufetAsInCSharp()
    {
        var files = Corpus();
        // ★ A floor, not an exact count — the corpus grows. It is here so that an empty listing
        // (a wrong root, a moved folder) fails rather than comparing nothing and passing.
        Assert.True(files.Count >= 80, $"only {files.Count} Cufet files found under {RepoRoot}");

        var got = ByFile(Run(CufetExe, ["tools/lexer/lexer.cufe", .. files], RepoRoot));
        var problems = Differences(WithSources(files), got);
        Assert.True(problems.Count == 0,
            $"{problems.Count} of {files.Count} files lex differently:\n{string.Join("\n", problems)}");
    }

    /// <summary>
    /// Sources the C# lexer refuses, one per refusal it can give. The Cufet lexer must refuse each
    /// at the same line and column, with the same words.
    /// </summary>
    private static readonly string[] Refused =
    [
        "State \"never closed.",
        "State \"a\\qb\".",
        "State \"trailing\\",
        "Add 4 to scores.",
        "Define Total as 1.",
        "Define x as 0b12.",
        "Define x as 0x.",
        "Define x as 0b1__0.",
        "Define x as 0b_1.",
        "Define x as 0xFFFFFFFFFFFFFFFFF.",
        "State alice'x.",
        "State 1.\n/* never\n   /* closed */\nState 2.",
        "State \"{}\".",
        "State \"{  }\".",
        "State \"{x.",
        "Define x as <<never closed.",
        "Define x as [never closed.",
        "State 1 # 2.",
        "State \"ok\".\r\nState \"line two {\r\n  ",
    ];

    [Fact]
    public void EveryRefusal_IsGivenAtTheSamePlaceInTheSameWords()
    {
        var dir = Directory.CreateTempSubdirectory("cufet-lexer-refusals-").FullName;
        try
        {
            var files = new List<(string Name, string Source)>();
            for (int i = 0; i < Refused.Length; i++)
            {
                string name = $"refused-{i + 1}.cufe";
                File.WriteAllText(Path.Combine(dir, name), Refused[i]);
                // ⚠ Each case has to be a refusal on the C# side too, or the case tests nothing.
                Assert.StartsWith("error\t", Expected(Refused[i])[0]);
                files.Add((name, Refused[i]));
            }

            var lexer = Path.Combine(LexerDir, "lexer.cufe");
            var got = ByFile(Run(CufetExe, [lexer, .. files.Select(f => f.Name)], dir));
            var problems = Differences(files, got);
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheCompiledLexer_AgreesWithCSharpToo()
    {
        // ★ The oracle runs every corpus program on both backends — but runs this one with no
        // arguments, which lexes nothing. This is the compiled backend actually lexing.
        var dir = Directory.CreateTempSubdirectory("cufet-lexer-build-").FullName;
        try
        {
            foreach (var f in Directory.GetFiles(LexerDir, "*.cufe"))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            // A blueprint marks the directory as a project, so the files see each other as they do
            // in tools/lexer/.
            File.WriteAllText(Path.Combine(dir, "blueprint.cufe"), "");
            Run(CufetExe, ["build", "lexer.cufe"], dir);

            var files = Corpus();
            Assert.True(files.Count >= 80, $"only {files.Count} Cufet files found under {RepoRoot}");
            string exe = Path.Combine(dir, "lexer" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));
            var got = ByFile(Run(exe, files, RepoRoot));
            var problems = Differences(WithSources(files), got);
            Assert.True(problems.Count == 0,
                $"{problems.Count} of {files.Count} files lex differently compiled:\n{string.Join("\n", problems)}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
