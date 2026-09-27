using Xunit;
using Xunit.Abstractions;
using static Cufet.Compiler.Tests.LexerParityTests;

namespace Cufet.Compiler.Tests;

public sealed class CompiledCufetChecker() : CompiledFrontEnd("checker");

/// <summary>
/// The checker written in Cufet (`tools/front-end/checker.cufe`) accepts what the one in C# accepts,
/// and refuses what it refuses in the same words.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE ANSWER KEY IS <c>TypeChecker.Check</c> ITSELF, run on each file alone
/// (<see cref="CheckerAnswer"/>): `ok`, or `error` and the whole message on one line. The FIRST
/// refusal is what is compared — C# stops there, so there is no second one to compare.
/// </para>
/// <para>
/// ★★ `unsupported` IS NOT A DIFFERENCE, AND IT IS NOT AGREEMENT. The Cufet checker says it for
/// any program holding something it cannot judge yet, and such a file is listed rather than
/// failed. What holds the checker to its growth is the FLOOR — how many must agree — raised as
/// it grows and never lowered.
/// </para>
/// </remarks>
public class CheckerParityTests(ITestOutputHelper output, CompiledCufetChecker compiled)
    : IClassFixture<CompiledCufetChecker>
{
    /// <summary>How many corpus files must agree. Raised as the checker grows; never lowered.</summary>
    private const int CorpusFloor = 13;

    // ── The comparison ──────────────────────────────────────────────────────

    private void Check(Dictionary<string, List<string>> got, IReadOnlyList<(string Name, string Source)> files,
                       int floor, string how)
    {
        int agreed = 0;
        var problems = new List<string>();
        var unsupported = new List<string>();
        foreach (var (name, source) in files)
        {
            string want = CheckerAnswer.Expected(source);
            string have = got.TryGetValue(name, out var lines) && lines.Count > 0 ? lines[0] : "<nothing printed>";
            if (have.StartsWith("unsupported\t", StringComparison.Ordinal))
                unsupported.Add($"{name}\t{have["unsupported\t".Length..]}");
            else if (have == want)
                agreed++;
            else
                problems.Add($"{name}:\n    C#:    {want}\n    Cufet: {have}");
        }

        output.WriteLine($"{how}: {agreed} of {files.Count} agree; {unsupported.Count} not yet:");
        foreach (var line in unsupported) output.WriteLine("    " + line);

        Assert.True(problems.Count == 0,
            $"{problems.Count} of {files.Count} differ {how}:\n{string.Join("\n", problems.Take(20))}");
        Assert.True(agreed >= floor, $"only {agreed} agree {how}, and the floor is {floor}.");
    }

    /// <summary>Programs the C# parser reads — a parse refusal leaves the checker nothing to say.</summary>
    private static List<(string, string)> Parsed(IEnumerable<(string Name, string Source)> files) =>
        files.Where(f => CheckerAnswer.Expected(f.Source) != "parse").ToList();

    /// <summary>Writes each source to a temporary directory and runs the compiled checker over them.</summary>
    private Dictionary<string, List<string>> RunOnSources(IReadOnlyList<(string Name, string Source)> sources, string label)
    {
        var dir = Directory.CreateTempSubdirectory($"cufet-checker-{label}-").FullName;
        try
        {
            foreach (var (name, source) in sources) File.WriteAllText(Path.Combine(dir, name), source);
            var got = new Dictionary<string, List<string>>();
            // In batches: a Windows command line has a length limit.
            foreach (var batch in sources.Select(s => s.Name).Chunk(200))
                foreach (var (k, v) in ByFile(Run(compiled.Exe, batch, dir))) got[k] = v;
            return got;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Hand-written programs ───────────────────────────────────────────────

    /// <summary>
    /// Small programs, accepted and refused, each of which the Cufet checker must JUDGE — none of
    /// them may come back `unsupported`. What the checker has learned to do is written down here.
    /// </summary>
    private static readonly string[] Judged =
    [
        // Names: undefined, a book, a book's type, ended with its block.
        "State 1.",
        "State mystery.",
        "State math.",
        "Define math as 5.",
        "State matrix.",
        "State step.",
        "If true:\n    Define y as 1.\nDone.\nState y.",
        "While false, repeat:\n    State z.\nDone.",
        "The q becomes 3.",
        // Defining twice, shadowing, and the two names every program is given.
        "Define x as 1.\nDefine x as 2.",
        "Define y as 1.\nIf true:\n    Define y as 2.\nDone.",
        "Define y as 1.\nIf true:\n    Define a shadow y as 2.\nDone.",
        "If true:\n    Define a shadow y as 2.\nDone.",
        "Define input as 3.",
        "Define output as 3.",
        "If true:\n    Define input as 3.\nDone.",
        // Reassigning: the type is for life, and a permanent name is fixed.
        "Define x as 1.\nThe x becomes \"a\".",
        "Define x as 1.\nThe x becomes 2.\nState x.",
        "Define x as 1 permanently.\nThe x becomes 2.",
        "Define n as 0.\nRepeat:\n    Increment n by 1.\nUntil n is 3.",
        "Define x as void.\nState x.",
        // A permanent name is known before its line.
        "Define big as 10 permanently.\nState big.",
        "State later.\nDefine later as 1 permanently.",
        // Operators.
        "State 1 + \"a\".",
        "State \"a\" - 1.",
        "State 1 is \"a\".",
        "State 1 is less than \"a\".",
        "State 1 and true.",
        "State true and \"a\".",
        "State not 5.",
        "State not \"a\".",
        "State -\"a\".",
        "Define flag as 0xFF.\nState flag and 0x0F.",
        "Define flag as 0xFF.\nState flag + 1.",
        "Define flag as 0xFF.\nState flag and 1.",
        // Joining and converting text, and a voidable where a value that is there goes.
        "State \"a\" joined to \"b\".",
        "State 1 joined to \"b\".",
        "State \"a\" joined to 2.",
        "State \"a\" joined to (\"5\" converted to number).",
        "Define n as \"5\" converted to number.\nState \"a\" joined to n.",
        "Define n as \"5\" converted to number.\nState n + 1.",
        "Define n as \"5\" converted to number.\nState n is greater than 1.",
        "Define n as \"5\" converted to number.\nState n converted to text.",
        "Define n as \"5\" converted to number.\nState \"{n}\".",
        "Define n as \"5\" converted to number.\nState (n but void is 0) + 1.",
        "Define n as \"5\" converted to number.\nState n but void is \"x\".",
        "State 5 but void is 0.",
        "State 5 converted to number.",
        "Define x as 1.\nState \"v: {x}\".",
        "Define x as 1.\nThe x becomes \"5\" converted to number.",
        "Define x as \"5\" converted to number.\nThe x becomes 3.",
        "Define n as \"5\" converted to number.\nIf n is 5, state 1.",
        // Narrowing: an arm, the Otherwise of a lone `is void`, a guard, and reassigning ending it.
        "Define n as \"5\" converted to number.\nIf n is not void:\n    State n + 1.\nDone.",
        "Define n as \"5\" converted to number.\nIf n is void:\n    State 0.\nDone.\nOtherwise:\n    State n + 1.\nDone.",
        "Define n as \"5\" converted to number.\nWhile true, repeat:\n    If n is void, stop.\n    State n + 1.\nDone.",
        "Define n as \"5\" converted to number.\nIf n is not void:\n    The n becomes void.\n    State n + 1.\nDone.",
        // Declared types.
        "Define the number n as 5.\nState n.",
        "Define the number n as \"a\".",
        "Define the voidable number n as 5.\nThe n becomes void.\nState n but void is 0.",
        // Series.
        "Define xs as a series with (1, 2, 3).\nState item 1 of xs + 1.",
        "Define xs as a series with (1, \"a\").",
        "Define xs as a series of numbers.\nInsert 1 into xs.",
        "Define xs as a series with ().",
        "Define xs as a series of text with (1).",
        "Define xs as a series with (1, 2).\nInsert \"a\" into xs.",
        "Define xs as a series with (1, 2).\nInsert 3 into xs.\nState the number of xs.",
        "Define xs as a series with (1, 2).\nFor each x in xs, repeat:\n    State x + 1.\nDone.",
        "Define xs as a series with (1, 2).\nFor each x in xs, repeat:\n    State x joined to \"a\".\nDone.",
        "Define xs as a series with (\"a\").\nFor each word in xs, repeat:\n    Insert word into xs.\nDone.",
        "For each x in 5, repeat:\n    State x.\nDone.",
        "Define t as \"abc\".\nState the number of t.",
        "Define xs as a series with (1, 2).\nState item 1.5 of xs.",
        "Define xs as a series with (1, 2).\nThe item 1 of xs becomes \"a\".",
        "Define xs as a series with (1, 2).\nRemove item 1 from xs.\nRemove 2 from xs.",
        "Define xs as a series with (1, 2).\nRemove \"a\" from xs.",
        "Define n as 5.\nRemove item 1 from n.",
        "Define n as 5.\nInsert 1 into n.",
        // Functions: calls and their arguments, returns, and reaching the end without one.
        "Bind number to twice, given (the number n):\n    Return n * 2.\nDone.\nState cast twice on (4).",
        "Bind number to twice, given (the number n):\n    Return n * 2.\nDone.\nState cast twice on (\"a\").",
        "Bind number to twice, given (the number n):\n    Return n * 2.\nDone.\nState cast twice on (1, 2).",
        "Bind number to twice, given (the number n):\n    Return n * 2.\nDone.\nState cast twice on ().",
        "Bind number to twice, given (the number n):\n    Return \"x\".\nDone.",
        "Bind number to twice, given (the number n):\n    Return.\nDone.",
        "Bind number to twice, given (the number n):\n    If n is 1, return 1.\nDone.",
        "Bind number to twice, given (the number n):\n    If n is 1:\n        Return 1.\n    Done.\n    Otherwise:\n        Return 2.\n    Done.\nDone.\nState cast twice on (1).",
        "Bind void to greet:\n    State \"hi\".\nDone.\nCast greet.\nState cast greet.",
        "Bind void to greet:\n    Return 5.\nDone.",
        "Bind void to greet:\n    Return a failure \"x\".\nDone.",
        "Bind number to add, given (the number x, the number y):\n    Return x + y.\nDone.\nDefine n as \"5\" converted to number.\nState cast add on (n, 2).",
        "Bind number to first:\n    Return cast second.\nDone.\nBind number to second:\n    Return 2.\nDone.\nState cast first.",
        "Cast nowhere on (5).",
        "State cast nowhere on (5).",
        "Define x as 5.\nCast x on (1).",
        // What a body sees: functions and permanent constants, not top-level data or a caller's modules.
        "Define total as 1.\nBind number to peek:\n    Return total.\nDone.",
        "Define total as 1 permanently.\nBind number to peek:\n    Return total.\nDone.\nState cast peek.",
        "Bind number to peek:\n    Return missing.\nDone.",
        "Bind number to peek:\n    Return math.\nDone.",
        "Bind number to peek, given (the number n):\n    The n becomes \"a\".\n    Return n.\nDone.",
        "Bind number to outer:\n    Define k as 3.\n    Bind number to inner:\n        Return k.\n    Done.\n    Return cast inner.\nDone.\nState cast outer.",
        "Bind number to outer:\n    Define k as 3.\n    Return k.\nDone.\nState k.",
        "Bind number to outer:\n    If true:\n        Define k as 3.\n    Done.\n    Return 1.\nDone.\nState k.",
        "Define input as 1.\nBind void to f:\n    State 1.\nDone.",
        "Bind void to f:\n    Define input as 5.\nDone.",
        "Define f as 3.\nBind void to f:\n    State 1.\nDone.",
        "Bind void to f:\n    State 1.\nDone.\nDefine f as 3.",
        // Named arguments, and a function reached through a value keeps its parameters' names.
        "Bind number to add, given (the number x, the number y):\n    Return x + y.\nDone.\nState cast add on (the x 1, the y 2).",
        "Bind number to add, given (the number x, the number y):\n    Return x + y.\nDone.\nState cast add on (the x 1, the z 2).",
        "Bind number to add, given (the number x, the number y):\n    Return x + y.\nDone.\nState cast add on (1, the x 2).",
        "Bind number to add, given (the number x, the number y):\n    Return x + y.\nDone.\nState cast add on (the x 1).",
        "Bind number to add, given (the number x, the number y):\n    Return x + y.\nDone.\nState cast add on (the x 1, the x 2).",
        "Bind number to add, given (the number x, the number y):\n    Return x + y.\nDone.\nState cast add on (1, 2, the y 3).",
        "Bind text to label, given (the text word):\n    Return word joined to \"!\".\nDone.\nDefine g as label.\nState cast g on (\"x\").",
        "Bind text to label, given (the text word):\n    Return word joined to \"!\".\nDone.\nDefine g as label.\nState cast g on (the word \"x\").",
        // Versions of one name that cannot be told apart.
        "Bind void to greet:\n    State \"hi\".\nDone.\nBind void to greet:\n    State \"ho\".\nDone.",
        "Bind void to f, given (the number count):\n    State count.\nDone.\nBind void to f:\n    State 1.\nDone.",
        "Bind void to f:\n    State 1.\nDone.\nBind number to f:\n    Return 1.\nDone.",
        // Objects: making one, its fields and methods, positions, embedding, and field writes.
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word \"a\" }.\nState x's word.\nState the word of x.",
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word 5 }.",
        "Define object leaf with (the text word).\nDefine x as a new leaf { }.",
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word \"a\", the size 3 }.",
        "Define x as a new twig { the word \"a\" }.",
        "Define x as a new math { }.",
        "Define x as a new rabbit { }.",
        "Define object math with (the text word).",
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word \"a\" }.\nState x's colour.",
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word \"a\" }.\nState the colour of x.",
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word \"a\" }.\nThe x's word becomes 5.",
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word \"a\" }.\nThe x's colour becomes 5.",
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word \"a\" }.\nThe word of x becomes 5.",
        "Define object leaf with (the text word permanently).\nDefine x as a new leaf { the word \"a\" }.\nThe x's word becomes \"b\".",
        "Define object leaf with (the text word, the number id permanently).\nDefine x as a new leaf { the word \"a\", the id 1 }.\nThe id of x becomes 2.",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        Return one's word.\n    Done.\nDone.\nDefine x as a new leaf { the word \"a\" }.\nState cast x's shout on (2).\nState cast shout on (x, 2).",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        Return one's word.\n    Done.\nDone.\nDefine x as a new leaf { the word \"a\" }.\nState cast x's shout on (\"no\").",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        Return one's word.\n    Done.\nDone.\nDefine x as a new leaf { the word \"a\" }.\nState cast shout on (x, \"no\").",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        Return one's word.\n    Done.\nDone.\nDefine x as a new leaf { the word \"a\" }.\nState x's shout.",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        Return one's word.\n    Done.\nDone.\nDefine x as a new leaf { the word \"a\" }.\nState cast whisper on (x).",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        Return word.\n    Done.\nDone.",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        If times is 1, return one's word.\n    Done.\nDone.",
        "Define object leaf with (the text word):\n    Bind number to shout:\n        Return one's word.\n    Done.\nDone.",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        Return one's word.\n    Done.\nDone.\nBind text to shout, given (the leaf l, the number n):\n    Return \"x\".\nDone.\nDefine x as a new leaf { the word \"a\" }.\nState cast shout on (x, 2).",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        Return one's word.\n    Done.\nDone.\nDefine x as a new leaf { the word \"a\" }.\nState cast x's shout on (the times 2).\nState cast shout on (x, the times 3).",
        "Define object leaf with (the text word):\n    Bind text to shout, given (the number times):\n        Return one's word.\n    Done.\nDone.\nDefine x as a new leaf { the word \"a\" }.\nState cast shout on (x, the loud 3).",
        "Define object pair with (number, text).\nDefine p as a new pair { 1, \"a\" }.\nState item 1 of p + 1.\nState item 3 of p.",
        "Define object pair with (number, text).\nDefine p as a new pair { 1 }.",
        "Define object pair with (number, text).\nDefine p as a new pair { 1, 2 }.",
        "Define object pair with (number, text).\nDefine p as a new pair { 1, \"a\" }.\nThe item 2 of p becomes 5.",
        "Define object person with (the text name).\nDefine object customer with (the number id) and as a person.\nDefine c as a new customer { the id 1, the name \"a\" }.\nState c's name.\nThe c's name becomes \"b\".\nState the person of c.",
        "Define object person with (the text name).\nDefine object customer with (the number id) and as a person.\nDefine c as a new customer { the id 1, the name \"a\" }.\nThe c's person becomes a new person { the name \"b\" }.",
        "Define object person with (the text name).\nDefine object customer with (the text name) and as a person.",
        "Define object customer with (the number id) and as a ghost.",
        "Define object leaf with (the text word).\nDefine object holder with (the leaf current).\nDefine h as a new holder { the current a new leaf { the word \"a\" } }.\nThe h's current becomes a new leaf { the word \"b\" }.\nThe h's current becomes 5.",
        "Define object holder with (the twig current).",
        "Bind void to f, given (the twig t):\n    State 1.\nDone.",
        "Define the twig x as 5.",
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word \"a\" }.\nDefine y as a new leaf { the word \"b\" }.\nState x is y.\nState x is 5.",
        "Define object leaf with (the text word).\nDefine x as a new leaf { the word \"a\" }.\nState x + 1.",
        "Define object leaf with (the text word).\nDefine object leaf with (the number size).\nDefine x as a new leaf { the size 1 }.\nState x's size.",
        "Define object leaf with (the text word).\nDefine xs as a series of leaf.\nInsert a new leaf { the word \"a\" } into xs.\nInsert 5 into xs.",
        "Define t as \"abc\".\nState the length of t.\nState the length of 5.",
        "Define t as \"abc\".\nState the size of t.",
        "Define t as 5.\nState the colour of t.",
        // A signature naming an unknown type twice leaves a BLANK: a template, set aside unchecked.
        "Bind series of thing to wrap, given (the thing x):\n    Return a series with (x).\nDone.\nState 1.",
        "Bind series of thing to wrap, given (the thing x):\n    Return mystery.\nDone.\nState 1.",
        "Bind void to outer:\n    Bind void to inner, given (the twig t):\n        State 1.\n    Done.\nDone.",
        "Bind thing to wrap, given (the thing x):\n    Return x.\nDone.\nBind thing to wrap, given (the thing y):\n    Return y.\nDone.",
    ];

    [Fact]
    public void EveryHandWrittenProgram_IsJudgedAsCSharpJudgesIt()
    {
        var sources = Judged.Select((s, i) => ($"judged-{i + 1}.cufe", s)).ToList();
        foreach (var (name, source) in sources)
            Assert.True(CheckerAnswer.Expected(source) != "parse", $"{name} does not parse: {source}");
        Check(RunOnSources(sources, "judged"), sources, sources.Count, "on hand-written programs");
    }

    // ── The corpus ──────────────────────────────────────────────────────────

    [Fact]
    public void TheCompiledChecker_JudgesEveryFileAsCSharpDoes()
    {
        var files = Corpus();
        Assert.True(files.Count >= 80, $"only {files.Count} Cufet files found under {RepoRoot}");
        var sources = Parsed(files.Select(n => (n, File.ReadAllText(Path.Combine(RepoRoot, n)))));
        Check(ByFile(Run(compiled.Exe, files, RepoRoot)), sources, CorpusFloor, "on the corpus");
    }
}
