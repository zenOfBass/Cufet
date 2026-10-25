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
        RuntimeSplit.WriteHeaders(RuntimeFolder, header);
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
    private const int BuiltFloor = 440;

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
        // Objects: made, read, set, copied as values, compared, nested, holding a series, and their methods — inside the object or bound unto it.
        "Define object point with (the number across, the number up).\nDefine spot as a new point { the across 3, the up 4 }.\nState spot's across.\nState spot's up.",
        "Define object point with (the number across, the number up).\nDefine spot as a new point { the across 3, the up 4 }.\nState spot.",
        "Define object badge with (the text name, the fact active, the number level).\nState a new badge { the name \"grace\", the active true, the level 2 }.",
        "Define object empty with ().\nState a new empty { }.",
        "Define object point with (the number across, the number up).\nDefine spot as a new point { the across 3, the up 4 }.\nThe spot's across becomes 10.\nState spot.",
        "Define object point with (the number across, the number up).\nDefine spot as a new point { the across 3, the up 4 }.\nDefine copy as spot.\nThe copy's up becomes 0.\nState spot.\nState copy.",
        "Define object point with (the number across, the number up).\nDefine one-spot as a new point { the across 1, the up 2 }.\nDefine two-spot as a new point { the across 1, the up 2 }.\nState one-spot is two-spot.\nThe two-spot's up becomes 9.\nState one-spot is two-spot.\nState one-spot is not two-spot.",
        "Define object point with (the number across, the number up):\n    Bind number to sum:\n        Return one's across + one's up.\n    Done.\n    Bind void to shift, given (the number amount):\n        One's across becomes one's across + amount.\n    Done.\nDone.\nDefine spot as a new point { the across 3, the up 4 }.\nState cast spot's sum.\nCast spot's shift on (10).\nState spot.\nState cast spot's sum.",
        "Define object point with (the number across, the number up).\nBind number to across-of unto point:\n    Return one's across.\nDone.\nDefine spot as a new point { the across 7, the up 1 }.\nState cast spot's across-of.",
        "Define object point with (the number across, the number up).\nBind point to moved, given (the point start-point, the number amount):\n    Return a new point { the across start-point's across + amount, the up start-point's up }.\nDone.\nDefine spot as a new point { the across 1, the up 1 }.\nState cast moved on (spot, 5).\nState spot.",
        "Define object point with (the number across, the number up).\nBind void to clobber, given (the point target):\n    The target's across becomes 99.\n    State target.\nDone.\nDefine spot as a new point { the across 1, the up 1 }.\nCast clobber on (spot).\nState spot.",
        "Define object point with (the number across, the number up).\nDefine object line with (the point head, the point tail).\nDefine route as a new line { the head a new point { the across 0, the up 0 }, the tail a new point { the across 3, the up 4 } }.\nState route.\nState route's tail's up.\nThe route's head becomes a new point { the across 5, the up 5 }.\nState route.",
        "Define object bag with (the text label, the series of number items).\nDefine first-bag as a new bag { the label \"a\", the items a series of number with (1, 2) }.\nDefine second-bag as first-bag.\nInsert 3 into second-bag's items.\nThe second-bag's label becomes \"b\".\nState first-bag.\nState second-bag.",
        "Define object counter with (the number count):\n    Bind void to tick:\n        One's count becomes one's count + 1.\n    Done.\n    Bind void to tick-twice:\n        Cast one's tick.\n        Cast one's tick.\n    Done.\nDone.\nDefine clicks as a new counter { the count 0 }.\nCast clicks's tick-twice.\nCast clicks's tick.\nState clicks's count.",
        "Define object account with (the text owner, the number balance):\n    Bind fact to can-pay, given (the number amount):\n        Return one's balance is not less than amount.\n    Done.\n    Bind void to pay, given (the number amount):\n        If cast one's can-pay on (amount):\n            One's balance becomes one's balance - amount.\n            State \"paid {amount}\".\n        Done.\n        Otherwise:\n            State \"{one's owner} cannot pay {amount}\".\n        Done.\n    Done.\nDone.\nDefine wallet as a new account { the owner \"grace\", the balance 10 }.\nCast wallet's pay on (4).\nCast wallet's pay on (9).\nState wallet's balance.",
        "Define object point with (the number across, the number up).\nDefine spots as a series of number.\nDefine here as a new point { the across 2, the up 3 }.\nFor each n in range 1 to 3, repeat:\n    Insert here's across * n into spots.\nDone.\nState spots.",
        "Define object point with (the number across, the number up):\n    Bind number to sum:\n        Return one's across + one's up.\n    Done.\n    Bind void to shift, given (the number amount):\n        One's across becomes one's across + amount.\n    Done.\nDone.\nBind number to total-of, given (the point first-point, the point second-point):\n    Return cast first-point's sum + cast second-point's sum.\nDone.\nState cast total-of on (a new point { the across 1, the up 2 }, a new point { the across 3, the up 4 }).",
        "Define object shelf with (the series of text titles):\n    Bind void to add, given (the text title):\n        Insert title into one's titles.\n    Done.\n    Bind number to count:\n        Return the number of one's titles.\n    Done.\nDone.\nDefine books as a new shelf { the titles a series of text }.\nCast books's add on (\"dune\").\nCast books's add on (\"emma\").\nState cast books's count.\nState books.",
        "Define object pair with (the text left, the text right).\nDefine one-pair as a new pair { the left \"a\", the right \"b\" }.\nJudge one-pair's left, where it is:\n    \"a\", state \"starts with a\".\n    Otherwise, state \"something else\".\nDone.",
        "Define object point with (the number across, the number up).\nDefine spot as a new point { the across 1, the up 2 }.\nIf spot is (a new point { the across 1, the up 2 }), state \"same\".",
        // Pull blocks, regions, series whose items name their type, and series of objects — trees of them included.
        "Pull a book on collections.\n    State \"inside\".\nDone.\nState \"after\".",
        "Pull a book on collections.\n    Bind number to twice, given (the number n):\n        Return n * 2.\n    Done.\n    State cast twice on (21).\nDone.",
        "State cast twice on (4).\nPull a book on collections.\n    Bind number to twice, given (the number n):\n        Return n * 2.\n    Done.\nDone.",
        "Pull a book on collections.\n    Define object point with (the number across, the number up).\nDone.\nDefine spot as a new point { the across 1, the up 2 }.\nState spot.",
        "Pull a book on collections.\n    Pull a book on math.\n        State \"nested\".\n    Done.\nDone.",
        "Define total as 0.\nPull a book on collections.\n    Define step as 5.\n    The total becomes total + step.\nDone.\nState total.",
        "Pull a rabbit.\n    Define words as a series of text with (\"a\", \"b\").\n    State words.\nDone.",
        "Define kept as a series of text.\nDefine word as \"x\".\nPull a rabbit.\n    Insert word joined to \"y\" into kept.\n    Define made as \"a\" joined to word.\n    The word becomes made.\nDone.\nState kept.\nState word.",
        "Bind number to counted, given (the number limit):\n    Define total as 0.\n    Pull a rabbit.\n        For each n in range 1 to limit, repeat:\n            If n is 4, return total.\n            Increment total by n.\n        Done.\n    Done.\n    Return total.\nDone.\nState cast counted on (10).\nState cast counted on (2).",
        "Pull a rabbit as hopper.\n    State \"named\".\nDone.",
        "Define scores as a series with (10, 20, 30).\nState scores.\nState the number of scores.",
        "Define names as a series with (\"grace\", \"hopper\").\nFor each in names, repeat:\n    State it.\nDone.",
        "Define flags as a series with (true, false, true).\nState flags.",
        "Define object point with (the number across, the number up).\nDefine spots as a series of point with (a new point { the across 1, the up 2 }, a new point { the across 3, the up 4 }).\nState spots.\nState item 2 of spots.\nState the number of spots.",
        "Define object point with (the number across, the number up).\nDefine spots as a series of point.\nFor each n in range 1 to 3, repeat:\n    Insert a new point { the across n, the up n * n } into spots.\nDone.\nFor each spot in spots, repeat:\n    State spot's up.\nDone.",
        "Define object point with (the number across, the number up).\nDefine spots as a series of point with (a new point { the across 1, the up 2 }).\nDefine first-spot as item 1 of spots.\nThe first-spot's up becomes 99.\nState spots.\nState first-spot.",
        "Define object point with (the number across, the number up).\nDefine spots as a series of point with (a new point { the across 1, the up 2 }, a new point { the across 3, the up 4 }).\nRemove a new point { the across 1, the up 2 } from spots.\nState spots.",
        "Define object tree with (the text label, the series of tree children).\nDefine leaf as a new tree { the label \"leaf\", the children a series of tree }.\nDefine root as a new tree { the label \"root\", the children a series of tree with (leaf, leaf) }.\nState root.\nState the number of root's children.",
        "Define object tree with (the text label, the series of tree children):\n    Bind number to size:\n        Define total as 1.\n        For each child in one's children, repeat:\n            Increment total by cast child's size.\n        Done.\n        Return total.\n    Done.\nDone.\nDefine leaf as a new tree { the label \"leaf\", the children a series of tree }.\nDefine middle as a new tree { the label \"middle\", the children a series of tree with (leaf, leaf) }.\nDefine root as a new tree { the label \"root\", the children a series of tree with (middle, leaf) }.\nState cast root's size.",
        "Define object point with (the number across, the number up).\nBind series of point to diagonal, given (the number count):\n    Define found as a series of point.\n    For each n in range 1 to count, repeat:\n        Insert a new point { the across n, the up n } into found.\n    Done.\n    Return found.\nDone.\nState cast diagonal on (3).",
        "Define object point with (the number across, the number up).\nDefine one-row as a series of point with (a new point { the across 1, the up 1 }).\nDefine two-row as a series of point with (a new point { the across 1, the up 1 }).\nState one-row is two-row.",
        // Interfaces — one copy of a function per object passed for one — their defaults, and modules pulled and called.
        "Define shape as an interface for {\n    The number function area,\n    The text function describe\n}.\nBind text to describe unto shape:\n    Return \"area {(cast area on one) converted to text}\".\nDone.\nDefine object square with (the number side) and shape.\nBind number to area unto square:\n    Return one's side * one's side.\nDone.\nDefine object circle with (the number radius) and shape:\n    Bind number to area:\n        Return 3 * one's radius * one's radius.\n    Done.\n    Bind text to describe:\n        Return \"a circle\".\n    Done.\nDone.\nBind void to show, given (the shape thing):\n    State cast thing's describe.\n    State cast thing's area.\nDone.\nCast show on (a new square { the side 5 }).\nCast show on (a new circle { the radius 2 }).",
        "Define shape as an interface for {\n    The number function area,\n    The text function describe\n}.\nBind text to describe unto shape:\n    Return \"area {(cast area on one) converted to text}\".\nDone.\nDefine object square with (the number side) and shape.\nBind number to area unto square:\n    Return one's side * one's side.\nDone.\nDefine object circle with (the number radius) and shape:\n    Bind number to area:\n        Return 3 * one's radius * one's radius.\n    Done.\n    Bind text to describe:\n        Return \"a circle\".\n    Done.\nDone.\nBind number to doubled-area, given (the shape thing):\n    Return cast thing's area * 2.\nDone.\nState cast doubled-area on (a new square { the side 3 }).\nState cast doubled-area on (a new square { the side 4 }).\nState cast doubled-area on (a new circle { the radius 1 }).",
        "Define shape as an interface for {\n    The number function area,\n    The text function describe\n}.\nBind text to describe unto shape:\n    Return \"area {(cast area on one) converted to text}\".\nDone.\nDefine object square with (the number side) and shape.\nBind number to area unto square:\n    Return one's side * one's side.\nDone.\nDefine object circle with (the number radius) and shape:\n    Bind number to area:\n        Return 3 * one's radius * one's radius.\n    Done.\n    Bind text to describe:\n        Return \"a circle\".\n    Done.\nDone.\nBind number to total-area, given (the shape first-shape, the shape second-shape):\n    Return cast first-shape's area + cast second-shape's area.\nDone.\nState cast total-area on (a new square { the side 2 }, a new circle { the radius 1 }).\nState cast total-area on (a new circle { the radius 1 }, a new circle { the radius 2 }).",
        "Define shape as an interface for {\n    The number function area,\n    The text function describe\n}.\nBind text to describe unto shape:\n    Return \"area {(cast area on one) converted to text}\".\nDone.\nDefine object square with (the number side) and shape.\nBind number to area unto square:\n    Return one's side * one's side.\nDone.\nDefine object circle with (the number radius) and shape:\n    Bind number to area:\n        Return 3 * one's radius * one's radius.\n    Done.\n    Bind text to describe:\n        Return \"a circle\".\n    Done.\nDone.\nDefine tile as a new square { the side 6 }.\nState cast tile's describe.\nState cast area on (tile).",
        "Define greeter as an interface for { The text function greet }.\nDefine object person with (the text name) and greeter:\n    Bind text to greet:\n        Return \"hi, \" joined to one's name.\n    Done.\nDone.\nDefine object robot with (the number serial) and greeter:\n    Bind text to greet:\n        Return \"BEEP {one's serial}\".\n    Done.\nDone.\nBind void to welcome, given (the greeter someone, the number times):\n    For each n in range 1 to times, repeat:\n        State cast someone's greet.\n    Done.\nDone.\nCast welcome on (a new person { the name \"grace\" }, 2).\nCast welcome on (a new robot { the serial 7 }, 1).",
        "Define object shapes with () and module:\n    Define object point with (the number across, the number up).\n    Bind point to origin:\n        Return a new point { the across 3, the up 4 }.\n    Done.\n    Bind number to twice, given (the number n):\n        Return n * 2.\n    Done.\nDone.\nPull a shapes.\n    Define here as cast shapes's origin.\n    State here.\n    State cast shapes's twice on (21).\nDone.",
        "Define object shapes with () and module:\n    Define object point with (the number across, the number up).\n    Bind point to origin:\n        Return a new point { the across 3, the up 4 }.\n    Done.\n    Bind number to twice, given (the number n):\n        Return n * 2.\n    Done.\nDone.\nPull a shapes.\n    Define spots as a series of point with (cast shapes's origin, a new point { the across 1, the up 1 }).\n    State spots.\nDone.",
        "Define object shapes with () and module:\n    Define object point with (the number across, the number up).\n    Bind point to origin:\n        Return a new point { the across 3, the up 4 }.\n    Done.\n    Bind number to twice, given (the number n):\n        Return n * 2.\n    Done.\nDone.\nBind number to sum-of, given (the number n):\n    Return n + n.\nDone.\nPull a shapes.\n    State cast sum-of on (cast shapes's twice on (5)).\nDone.",
        // Closed unions: made, changed, compared, printed, tested with is a, narrowed in If and by elimination, judged by type, held in a series, passed and given back.
        "Define the (number or text) held as 5.\nState held.\nThe held becomes \"five\".\nState held.",
        "Define the (number or text) held as 5.\nIf held is a number, state held + 1.\nOtherwise, state held joined to \"!\".",
        "Define the (number or text) held as \"x\".\nIf held is a number, state held + 1.\nOtherwise, state held joined to \"!\".",
        "Define the (number or text) held as 5.\nIf held is not a number, state \"text: \" joined to held.\nOtherwise, state held * 10.",
        "Define the (number or text or fact) held as true.\nIf held is a number, state held + 1.\nOtherwise if held is a text, state held joined to \"?\".\nOtherwise, state held converted to text.",
        "Define the (number or text or fact) held as \"t\".\nIf held is a number or held is a fact:\n    State \"number or fact\".\nDone.\nOtherwise:\n    State held joined to \" is text\".\nDone.",
        "Define the (number or text) held as 5.\nState held is 5.\nState held is \"5\".\nState held is not 6.\nThe held becomes \"5\".\nState held is \"5\".",
        "Define the (number or text) one-value as 5.\nDefine the (number or text) two-value as 5.\nState one-value is two-value.\nThe two-value becomes \"5\".\nState one-value is two-value.",
        "Define object cat with (the text name).\nDefine object dog with (the number tricks).\nBind text to describe, given (the (cat or dog) pet):\n    Judge pet, where it is:\n        A cat, return \"cat {it's name}\".\n        A dog, return \"dog with {it's tricks}\".\n    Done.\nDone.\nState cast describe on (a new cat { the name \"tom\" }).\nState cast describe on (a new dog { the tricks 3 }).",
        "Define object cat with (the text name).\nDefine object dog with (the number tricks).\nDefine the (cat or dog) pet as a new dog { the tricks 2 }.\nState pet.\nIf pet is a dog, state pet's tricks * 2.\nThe pet becomes a new cat { the name \"felix\" }.\nIf pet is a cat, state pet's name.",
        "Define object cat with (the text name).\nDefine object dog with (the number tricks).\nBind (cat or dog) to adopt, given (the fact wants-cat):\n    If wants-cat, return a new cat { the name \"tom\" }.\n    Return a new dog { the tricks 1 }.\nDone.\nState cast adopt on (true).\nState cast adopt on (false).",
        "Define the (number or text) held as 4.\nJudge held, where it is:\n    A number, state it + 1.\n    A text, state it joined to \"!\".\nDone.",
        "Define the (number or text or fact) held as false.\nJudge held, where it is:\n    A number, state \"number\".\n    Otherwise, state \"not a number\".\nDone.",
        "Define the (number or text or fact) held as \"word\".\nJudge held, where it is:\n    A number or a fact, state \"number or fact\".\n    A text, state it joined to \"!\".\nDone.",
        "Define pieces as a series of (number or text) with (1, \"two\", 3).\nState pieces.\nFor each piece in pieces, repeat:\n    If piece is a number, state piece * 100.\n    Otherwise, state piece.\nDone.",
        "Define pieces as a series of (number or text).\nInsert 5 into pieces.\nInsert \"x\" into pieces.\nState pieces.\nState the number of pieces.",
        "Bind void to show, given (the (number or text) value):\n    If value is a number, state \"number {value}\".\n    Otherwise, state \"text {value}\".\nDone.\nCast show on (3).\nCast show on (\"z\").",
        "Define the (number or text) held as 5.\nIf held is a number:\n    State held + 1.\n    The held becomes \"changed\".\n    State held.\nDone.",
        "Define total as 7.\nState total is a number.\nState total is a text.\nState total is not a text.",
        // Voidables, getters, shared constants, the math book built from its own source, and numbers of up to 29 digits.
        "Bind voidable number to halve, given (the number n):\n    If n % 2 is not 0, return void.\n    Return n / 2.\nDone.\nState cast halve on (8).\nState cast halve on (3).",
        "Bind voidable number to halve, given (the number n):\n    If n % 2 is not 0, return void.\n    Return n / 2.\nDone.\nState (cast halve on (3) but void is 0).\nState (cast halve on (10) but void is 0).",
        "Bind voidable number to halve, given (the number n):\n    If n % 2 is not 0, return void.\n    Return n / 2.\nDone.\nDefine a-half as cast halve on (8).\nIf a-half is not void, state a-half + 1.\nIf a-half is void, state \"nothing\".",
        "Bind voidable number to halve, given (the number n):\n    If n % 2 is not 0, return void.\n    Return n / 2.\nDone.\nDefine a-half as cast halve on (7).\nIf a-half is void, state \"nothing\".\nOtherwise, state a-half + 1.",
        "Define the voidable text label as void.\nState label.\nThe label becomes \"set\".\nState label.\nIf label is not void, state label joined to \"!\".",
        "Bind voidable number to halve, given (the number n):\n    If n % 2 is not 0, return void.\n    Return n / 2.\nDone.\nState cast halve on (4) is void.\nState cast halve on (5) is void.\nDefine one-half as cast halve on (4).\nDefine two-half as cast halve on (4).\nState one-half is two-half.",
        "Define separator as \", \" permanently.\nBind text to joined-pair, given (the text left, the text right):\n    Return left joined to separator joined to right.\nDone.\nState cast joined-pair on (\"a\", \"b\").",
        "Define limit as 3 permanently.\nBind void to count-up:\n    For each n in range 1 to limit, repeat:\n        State n.\n    Done.\nDone.\nCast count-up.\nState limit * 10.",
        "Pull a rabbit.\n    Define greeting as \"hi\" permanently.\n    Bind text to greet, given (the text who):\n        Return greeting joined to \" \" joined to who.\n    Done.\n    State cast greet on (\"grace\").\nDone.",
        "Define object box with (the number size):\n    Get doubled as number:\n        Return one's size * 2.\n    Done.\n    Get tripled as number, one's size * 3.\nDone.\nDefine b as a new box { the size 4 }.\nState b's doubled.\nState b's tripled.\nState b's doubled + b's tripled.",
        "Define object box with (the number size).\nGet area unto box as number:\n    Return one's size * one's size.\nDone.\nDefine b as a new box { the size 5 }.\nState b's area.",
        "Define object box with (the number size):\n    Get label as text, \"box of {one's size}\".\nDone.\nBind box to made, given (the number size-wanted):\n    Return a new box { the size size-wanted }.\nDone.\nState (cast made on (7))'s label.",
        "Pull a book on math.\n    State math's pi.\n    State math's e.\nDone.",
        "Pull a book on math.\n    State cast math's square-root on (2).\n    State cast math's square-root on (-1).\n    State (cast math's square-root on (16) but void is 0).\nDone.",
        "Pull a book on math.\n    State cast math's floor on (-2.5).\n    State cast math's ceiling on (-2.5).\n    State cast math's round on (2.5).\n    State cast math's round on (-0.5).\n    State cast math's absolute-value on (-7).\nDone.",
        "Pull a book on math.\n    State cast math's log on (10).\n    State cast math's exp on (1).\n    State cast math's power on (2, 10).\n    State cast math's power on (2, 0.5).\nDone.",
        "Pull a book on math.\n    Define radius as 2.\n    State math's pi * radius * radius.\nDone.",
        "State 79228162514264337593543950335.\nState 12345678901234567890123.\nState -98765432109876543210.\nState 0.0000000000000000000000000001.",
        // Maps: a map from … to …, a map with (…), lookups as voidables, setting, has a key / an entry, Remove, the size of, For each over the pairs.
        "Define ages as a map from text to number.\nIn ages, the entry for \"alice\" becomes 30.\nIn ages, the entry for \"bob\" becomes 41.\nState ages.\nState the size of ages.",
        "Define ages as a map with (\"alice\": 30, \"bob\": 41).\nState the entry for \"alice\" in ages.\nState the entry for \"carol\" in ages.\nState from ages, the entry for \"bob\".",
        "Define ages as a map with (\"alice\": 30).\nState (the entry for \"alice\" in ages but void is 0) + 1.\nState (the entry for \"zed\" in ages but void is -1).",
        "Define ages as a map with (\"alice\": 30).\nDefine found as the entry for \"alice\" in ages.\nIf found is not void, state found * 2.\nOtherwise, state \"none\".\nDefine missing as the entry for \"x\" in ages.\nIf missing is void, state \"no x\".",
        "Define ages as a map with (\"alice\": 30, \"bob\": 41).\nIf ages has a key for \"alice\", state \"has alice\".\nIf ages has an entry for \"carol\", state \"has carol\".\nOtherwise, state \"no carol\".\nState ages has a key for \"bob\".",
        "Define ages as a map with (\"a\": 1, \"b\": 2, \"c\": 3).\nRemove \"b\" from ages.\nState ages.\nIn ages, the entry for \"b\" becomes 20.\nState ages.\nIn ages, the entry for \"a\" becomes 10.\nState ages.",
        "Define ages as a map with (\"a\": 1, \"b\": 2).\nFor each pair in ages, repeat:\n    State the key of pair.\n    State the value of pair.\nDone.\nFor each pair in ages, state pair.",
        "Define counts as a map with (\"a\": 1, \"b\": 2).\nFor each pair in counts, repeat:\n    In counts, the entry for the key of pair becomes the value of pair * 10.\nDone.\nState counts.",
        "Define counts as a map with (\"a\": 1).\nFor each pair in counts, repeat:\n    In counts, the entry for \"z\" becomes 0.\nDone.\nState counts.",
        "Define words as a map with (1: \"one\", 2: \"two\").\nState words.\nState the entry for 2 in words.\nDefine flags as a map with (true: \"yes\", false: \"no\").\nState the entry for false in flags.",
        "Define left as a map with (\"a\": 1).\nDefine right as a map with (\"a\": 1).\nState left is right.\nDefine alias as left.\nState left is alias.\nIn alias, the entry for \"b\" becomes 2.\nState left.",
        "Define maybe as a map from text to voidable number.\nIn maybe, the entry for \"x\" becomes void.\nIn maybe, the entry for \"y\" becomes 5.\nState maybe.\nState maybe has a key for \"x\".\nState maybe has an entry for \"x\".\nState the entry for \"x\" in maybe.\nState the entry for \"y\" in maybe.",
        "Bind map from text to number to tally, given (the series of text words):\n    Define counts as a map from text to number.\n    For each word in words, repeat:\n        Define current as the entry for word in counts but void is 0.\n        In counts, the entry for word becomes current + 1.\n    Done.\n    Return counts.\nDone.\nState cast tally on (a series of text with (\"x\", \"y\", \"x\", \"z\", \"x\")).",
        "Bind void to bump, given (the map from text to number counts, the text which):\n    In counts, the entry for which becomes (the entry for which in counts but void is 0) + 1.\nDone.\nDefine counts as a map from text to number.\nCast bump on (counts, \"q\").\nCast bump on (counts, \"q\").\nState counts.",
        "Define object edge with (the text edge-dest, the number edge-weight).\nDefine adj as a map from text to series of edge.\nDefine fallback as a series of edge.\nDefine edges as (the entry for \"A\" in adj) but void is fallback.\nInsert a new edge { the edge-dest \"B\", the edge-weight 4 } into edges.\nIn adj, the entry for \"A\" becomes edges.\nState adj.\nState the number of (the entry for \"A\" in adj but void is fallback).",
        "Define groups as a map from text to series of text.\nIn groups, the entry for \"fruit\" becomes a series of text with (\"apple\", \"pear\").\nFor each pair in groups, repeat:\n    For each item-name in the value of pair, state \"{the key of pair}: {item-name}\".\nDone.",
        "Define nested as a map from text to map from text to number.\nDefine inner as a map with (\"x\": 1).\nIn nested, the entry for \"outer\" becomes inner.\nState nested.\nState the entry for \"outer\" in nested.",
        "Define object box with (the text label, the map from text to number stock).\nDefine b as a new box { the label \"crate\", the stock a map with (\"nails\": 100) }.\nIn b's stock, the entry for \"screws\" becomes 50.\nState b's stock.\nState b.",
        "Define scores as a map from text to (number or text).\nIn scores, the entry for \"a\" becomes 1.\nIn scores, the entry for \"b\" becomes \"high\".\nState scores.\nState the entry for \"b\" in scores.",
        "Define empty as a map from text to number.\nState empty.\nState the size of empty.\nFor each pair in empty, state pair.\nState \"done\".",
        // `the <field> of <object>`, which means `<object>'s <field>`.
        "Define object box with (the number size, the text label):\n    Get doubled as number, one's size * 2.\nDone.\nDefine b as a new box { the size 3, the label \"crate\" }.\nState the size of b.\nState the label of b joined to \"!\".\nState the doubled of b.",
        "Define object link with (the text name, the number weight).\nDefine object chain with (the link head).\nDefine c as a new chain { the head a new link { the name \"first\", the weight 2 } }.\nState the name of the head of c.\nState the weight of (the head of c) + 1.",
        // Records — made, read by name and by place, set, copied as values, compared, in series and maps; text — length, characters from … to, contains, starts/ends with, position, replace, trimmed, split; the arguments; Exit; and patterns, built from the regex book's own source.
        "Define car as a record with (\"hatchback\", the make \"Honda\", the year 2021).\nState the first of car.\nState the make of car.\nState the year of car + 1.\nState car.",
        "Define car as a record with (the make \"Honda\", the year 2021).\nThe make of car becomes \"Toyota\".\nState car.\nDefine truck as car.\nThe year of truck becomes 1999.\nState car.\nState truck.\nState car is truck.",
        "Define pair-of as a record with (1, \"one\").\nState the first of pair-of.\nThe first of pair-of becomes 2.\nState pair-of.",
        "Define left as a record with (the x 1, the y 2).\nDefine right as a record with (the y 2, the x 1).\nState left is right.\nState right.",
        "Define parts as a series of records like (the number kind, the text ch).\nInsert a record with (the kind 1, the ch \"a\") into parts.\nInsert a record with (the ch \"b\", the kind 2) into parts.\nFor each part in parts, state \"{the ch of part}{the kind of part}\".\nState parts.\nState the kind of (item 2 of parts).",
        "Bind record with (the number low, the number high) to span-of, given (the series of number values):\n    Define low as item 1 of values.\n    Define high as item 1 of values.\n    For each value-seen in values, repeat:\n        If value-seen < low, the low becomes value-seen.\n        If value-seen > high, the high becomes value-seen.\n    Done.\n    Return a record with (the low low, the high high).\nDone.\nDefine found as cast span-of on (a series with (4, 9, 2, 7)).\nState the low of found.\nState the high of found.\nState found.",
        "Define nested as a record with (the inner a record with (the n 5), the label \"outer\").\nState the n of the inner of nested.\nState nested.",
        "Define scores as a map from text to record like (the number wins, the number losses).\nIn scores, the entry for \"hopper\" becomes a record with (the wins 3, the losses 1).\nState scores.\nDefine found as the entry for \"hopper\" in scores.\nIf found is not void, state the wins of found.",
        "Define word as \"hello\".\nState the length of word.\nState the length of \"\".\nState the characters from 2 to 4 of word.\nState the characters from 3 to the end of word.",
        "Define word as \"hello\".\nState word contains \"ell\".\nState word contains \"z\".\nState word starts with \"he\".\nState word ends with \"he\".",
        "Define pieces as \"a,b,,c\" split by \",\".\nState pieces.\nState the number of pieces.\nFor each piece in \"one two\" split by \" \", state piece.",
        "State replace \"l\" with \"L\" in \"hello\".\nState \"  padded  \" trimmed joined to \"|\".\nState the position of \"l\" in \"hello\".\nState the position of \"z\" in \"hello\".\nState (the position of \"o\" in \"hello\" but void is 0) + 1.",
        "Define word as \"héllo wörld\".\nState the length of word.\nState the characters from 2 to 2 of word.\nState the characters from 8 to 8 of word.",
        "Define word as \"abc\".\nState the characters from 2 to 9 of word.",
        "State the arguments.\nState the number of the arguments.\nFor each passed in the arguments, state passed.",
        "State \"before\".\nExit.\nState \"after\".",
        "Pull a book on regex.\n    Define regex digits as [^[0-9]+$].\n    State cast digits on (\"123\").\n    State cast digits on (\"12a\").\n    State cast digits on (\"\").\nDone.",
        "Pull a book on regex.\n    Define regex core as [^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$].\n    For each version in a series of text with (\"1.2.3\", \"01.2.3\", \"1.2\", \"10.20.30\"), repeat:\n        State \"{version}: {cast core on (version)}\".\n    Done.\nDone.",
        "Pull a book on regex.\n    Define regex vowel as [[aeiou]].\n    Define regex word-ish as [^\\w+$].\n    State cast vowel on (\"rhythm\").\n    State cast vowel on (\"rabbit\").\n    State cast word-ish on (\"hopper_2\").\n    State cast word-ish on (\"two words\").\nDone.",
        // Failures — or failure, return a failure, or pass the failure off (as a value and as a statement), but on failure, Try and In case of failure (nested, in loops), the message and category of a failure; converted to number; reading a file that is not there; narrowing after a guard.
        "Bind number or failure to to-positive, given (the number n):\n    If n is less than 1, return a failure \"{n} is not positive\" of category \"range\".\n    Return n.\nDone.\nState (cast to-positive on (3) but on failure 0).\nState (cast to-positive on (-3) but on failure 0).",
        "Bind number or failure to to-positive, given (the number n):\n    If n is less than 1, return a failure \"{n} is not positive\" of category \"range\".\n    Return n.\nDone.\nBind number or failure to doubled, given (the number n):\n    Define p as cast to-positive on (n) or pass the failure off.\n    Return p * 2.\nDone.\nState (cast doubled on (4) but on failure -1).\nState (cast doubled on (0) but on failure -1).",
        "Bind number or failure to to-positive, given (the number n):\n    If n is less than 1, return a failure \"{n} is not positive\" of category \"range\".\n    Return n.\nDone.\nBind number or failure to doubled, given (the number n):\n    Define p as cast to-positive on (n) or pass the failure off.\n    Return p * 2.\nDone.\nTry to:\n    State cast doubled on (5).\n    State cast doubled on (-2).\n    State \"not reached\".\nDone.\nIn case of failure:\n    State the message of the failure.\n    State the category of the failure.\nDone.\nState \"after\".",
        "Bind void or failure to check, given (the number n):\n    If n is 0, return a failure \"zero\".\nDone.\nTry to:\n    Cast check on (1).\n    State \"one passed\".\n    Cast check on (0).\n    State \"not reached\".\nDone.\nIn case of failure:\n    State the message of the failure.\n    State the category of the failure.\n    State the failure.\nDone.",
        "Bind void or failure to check, given (the number n):\n    If n is 0, return a failure \"zero\".\nDone.\nBind void or failure to check-both, given (the number n):\n    Cast check on (n) or pass the failure off.\n    Cast check on (n - 1) or pass the failure off.\n    Return.\nDone.\nTry to:\n    Cast check-both on (5).\n    State \"5 passed\".\n    Cast check-both on (1).\nDone.\nIn case of failure:\n    State \"failed: {the message of the failure}\".\nDone.",
        "Bind number or failure to to-positive, given (the number n):\n    If n is less than 1, return a failure \"{n} is not positive\" of category \"range\".\n    Return n.\nDone.\nFor each n in a series with (2, 0, 3), repeat:\n    Try to:\n        Define p as cast to-positive on (n).\n        State p * 10.\n    Done.\n    In case of failure:\n        State \"skipped {n}\".\n    Done.\nDone.",
        "Bind number or failure to to-positive, given (the number n):\n    If n is less than 1, return a failure \"{n} is not positive\" of category \"range\".\n    Return n.\nDone.\nTry to:\n    Try to:\n        State cast to-positive on (-1).\n    Done.\n    In case of failure:\n        State \"inner: {the message of the failure}\".\n        State cast to-positive on (-2).\n    Done.\nDone.\nIn case of failure:\n    State \"outer: {the message of the failure}\".\nDone.",
        "Define err as a failure \"plain\".\nState err.\nState the message of err.\nState the category of err.\nDefine tagged as a failure \"tagged\" of category \"kind\".\nState the category of tagged.",
        "Bind text or failure to label-of, given (the number n):\n    If n is 0, return a failure \"no label for zero\".\n    Return \"label {n}\".\nDone.\nState (cast label-of on (2) but on failure \"none\").\nState (cast label-of on (0) but on failure \"none\").",
        "Define object ledger with (the number balance).\nBind void or failure to withdraw unto ledger, given (the number amount):\n    If amount is greater than one's balance, return a failure \"only {one's balance} left\".\n    Decrement one's balance by amount.\nDone.\nDefine account as a new ledger { the balance 10 }.\nTry to:\n    Cast account's withdraw on (4).\n    Cast account's withdraw on (9).\nDone.\nIn case of failure:\n    State \"{the message of the failure}, and {account's balance} is still there\".\nDone.",
        "Try to:\n    Define body as read all from the file \"nope.txt\".\n    State body.\nDone.\nIn case of failure:\n    State the message of the failure.\nDone.",
        "State \"42\" converted to number.\nState \"x\" converted to number.\nState \" 7 \" converted to number.\nState \"1e3\" converted to number.\nState \"-0.50\" converted to number.\nState (\"12\" converted to number but void is 0) + 1.",
        "Bind number or failure to parse-it, given (the text written):\n    Define parsed as written converted to number.\n    If parsed is void, return a failure \"'{written}' is not a number\" of category \"parse\".\n    Return parsed.\nDone.\nTry to:\n    State cast parse-it on (\"12.5\").\n    State cast parse-it on (\"warm\").\nDone.\nIn case of failure:\n    State the message of the failure.\nDone.",
        "Define whole as \"kept\".\nTry to:\n    The whole becomes read all from the file \"absent-whole.txt\".\nDone.\nIn case of failure:\n    State \"could not read: {the message of the failure}\".\nDone.\nState whole.",
        "Bind series of text or failure to lines-of, given (the text file-path):\n    Define found as read all lines from the file file-path or pass the failure off.\n    Return found.\nDone.\nState the number of (cast lines-of on (\"missing.txt\") but on failure a series of text).\nTry to:\n    State cast lines-of on (\"also-missing.txt\").\nDone.\nIn case of failure:\n    State the message of the failure.\nDone.",
        // Chases — made, inserted into (text or a code point; at the end, the start, or after a place), read by place, set, removed from, looped over, compared, converted to text; and a series of maps.
        "Pull a book on collections.\n    Define out as a chase.\n    Insert \"hello\" into out.\n    Insert \", world\" into out.\n    State out converted to text.\n    State the number of out.\n    State out.\nDone.",
        "Pull a book on collections.\n    Define out as a chase.\n    Insert \"bc\" into out.\n    Insert \"a\" into the start of out.\n    Insert \"X\" after item 1 of out.\n    State out converted to text.\nDone.",
        "Pull a book on collections.\n    Define out as a chase.\n    Insert 72 into out.\n    Insert 105 into out.\n    Insert 33 into the start of out.\n    State out converted to text.\nDone.",
        "Pull a book on collections.\n    Define out as a chase.\n    Insert \"hello\" into out.\n    State item 1 of out.\n    State the first of out.\n    State the last of out.\n    The item 1 of out becomes \"j\".\n    State out converted to text.\nDone.",
        "Pull a book on collections.\n    Define out as a chase.\n    Insert \"hello\" into out.\n    Remove item 2 from out.\n    Remove the last from out.\n    State out converted to text.\n    Remove \"l\" from out.\n    State out converted to text.\nDone.",
        "Pull a book on collections.\n    Define out as a chase.\n    Insert \"héllo wörld\" into out.\n    State the number of out.\n    State item 2 of out.\n    For each letter in out, repeat:\n        If letter is \"ö\", state \"found it\".\n    Done.\nDone.",
        "Pull a book on collections.\n    Define out as a chase.\n    Insert \"abc\" into out.\n    For each in out, state it.\n    Define copy as out.\n    Insert \"d\" into copy.\n    State out converted to text.\nDone.",
        "Pull a book on collections.\n    Define left as a chase.\n    Define right as a chase.\n    Insert \"ab\" into left.\n    Insert \"ab\" into right.\n    State left is right.\n    Insert \"c\" into right.\n    State left is right.\nDone.",
        "Pull a book on collections.\n    Bind text to shouted, given (the text word):\n        Define out as a chase.\n        For each letter in a series of text with (\"h\", \"e\", \"y\"), repeat:\n            Insert letter into out.\n            Insert \"!\" into out.\n        Done.\n        Insert word into out.\n        Return out converted to text.\n    Done.\n    State cast shouted on (\" you\").\nDone.",
        "Pull a book on collections.\n    Define out as a chase.\n    State out converted to text is \"\".\n    State the number of out.\n    State out.\nDone.",
        "Pull a book on collections.\n    Define scopes as a series of map from text to number.\n    Insert a map from text to number into scopes.\n    In item 1 of scopes, the entry for \"x\" becomes 1.\n    Insert a map with (\"y\": 2) into scopes.\n    State scopes.\n    State the number of scopes.\n    State the entry for \"x\" in item 1 of scopes.\n    Remove the last from scopes.\n    State scopes.\nDone.",
        "Pull a book on collections.\n    Bind number to depth-of, given (the series of map from text to text scopes, the text name):\n        Define place as the number of scopes.\n        While place is greater than 0, repeat:\n            If (item place of scopes) has a key for name, return place.\n            Decrement place by 1.\n        Done.\n        Return 0.\n    Done.\n    Define scopes as a series of map from text to text with (a map with (\"a\": \"outer\"), a map with (\"b\": \"inner\")).\n    State cast depth-of on (scopes, \"a\").\n    State cast depth-of on (scopes, \"b\").\n    State cast depth-of on (scopes, \"z\").\nDone.",
        // Casing — in uppercase and in lowercase, through the case table in the shared runtime.
        "State \"Hello, World! 42\" in uppercase.\nState \"Hello, World! 42\" in lowercase.\nState the length of (\"\" in uppercase).",
        "State \"héllo wörld\" in uppercase.\nState \"ÉCOLE ÀÇ\" in lowercase.\nState \"straße\" in uppercase.\nState \"ĀăĂā\" in lowercase.",
        "Define word as \"MiXeD\".\nDefine lower as word in lowercase.\nIf lower is \"mixed\", state \"matched\".\nState (word in uppercase) joined to \"!\".",
        // Choosing a value with when … otherwise, chained and as a union; sorted, in reverse, by a field, of a chase; methods called on an item of a series, a field, a new object or a call's result; the first and last characters of a text; the current directory.
        "Define n as 5.\nDefine label as \"big\" when n is greater than 3, otherwise \"small\".\nState label.\nState (1 when n is 2, otherwise 2) + 10.",
        "Define n as 2.\nDefine name as \"one\" when n is 1, otherwise \"two\" when n is 2, otherwise \"many\".\nState name.\nThe n becomes 9.\nState \"one\" when n is 1, otherwise \"two\" when n is 2, otherwise \"many\".",
        "Define n as 5.\nDefine mixed as 1 when n is 5, otherwise \"x\".\nState mixed.\nIf mixed is a number, state mixed + 1.",
        "Bind number to loud, given (the number n):\n    State \"called {n}\".\n    Return n.\nDone.\nDefine flag as true.\nDefine picked as 1 when flag, otherwise cast loud on (2).\nState picked.\nThe flag becomes false.\nState 1 when flag, otherwise cast loud on (3).",
        "Bind text to plural-of, given (the number count):\n    Return \"item\" when count is 1, otherwise \"items\".\nDone.\nFor each count in range 0 to 2, state \"{count} {cast plural-of on (count)}\".",
        "Define words as a series of text with (\"pear\", \"apple\", \"fig\").\nState words sorted.\nState words sorted in reverse.\nState words.\nDefine nums as a series of number with (3, 1, 2, 1.5, -4).\nState nums sorted.\nState nums sorted in reverse.",
        "Define entries as a series of records like (the text word, the number count).\nInsert a record with (the word \"b\", the count 2) into entries.\nInsert a record with (the word \"a\", the count 5) into entries.\nInsert a record with (the word \"c\", the count 2) into entries.\nState entries sorted by the count.\nState entries sorted by the count in reverse.\nState entries sorted by the word.",
        "Define object player with (the text name, the number score).\nDefine players as a series of player.\nInsert a new player { the name \"hopper\", the score 7 } into players.\nInsert a new player { the name \"grace\", the score 9 } into players.\nInsert a new player { the name \"ada\", the score 7 } into players.\nFor each top in players sorted by the score in reverse, state \"{top's name} {top's score}\".",
        "Define empty as a series of number.\nState empty sorted.\nDefine one-item as a series of text with (\"only\").\nState one-item sorted in reverse.",
        "Pull a book on collections.\n    Define out as a chase.\n    Insert \"banana\" into out.\n    State (out sorted) converted to text.\n    State (out sorted in reverse) converted to text.\nDone.",
        "State the first 2 characters of \"hello\".\nState the last 3 characters of \"hello\".\nState the first 9 characters of \"hi\".\nState the last 0 characters of \"hi\" is \"\".\nState the first 1 characters of \"héllo\" joined to the last 4 characters of \"héllo\".",
        "Define here as the current directory.\nState here is void.\nIf here is not void, state the length of here is greater than 0.",
        "Define object box with (the number size):\n    Bind void to grow:\n        Increment one's size by 1.\n    Done.\n    Bind number to doubled:\n        Return one's size * 2.\n    Done.\nDone.\nDefine boxes as a series of box with (a new box { the size 4 }, a new box { the size 1 }).\nCast (item 1 of boxes)'s grow.\nCast (the last of boxes)'s grow.\nState boxes.\nState cast (item 1 of boxes)'s doubled.",
        "Define object box with (the number size):\n    Bind void to grow:\n        Increment one's size by 1.\n    Done.\n    Bind number to doubled:\n        Return one's size * 2.\n    Done.\nDone.\nDefine object pair with (the box held).\nDefine p as a new pair { the held a new box { the size 1 } }.\nCast (p's held)'s grow.\nState p.",
        "Define object box with (the number size):\n    Bind void to grow:\n        Increment one's size by 1.\n    Done.\n    Bind number to doubled:\n        Return one's size * 2.\n    Done.\nDone.\nState cast (a new box { the size 7 })'s doubled.\nBind box to made, given (the number size-wanted):\n    Return a new box { the size size-wanted }.\nDone.\nState cast (cast made on (3))'s doubled.",
        "Define object box with (the number size):\n    Bind void to grow:\n        Increment one's size by 1.\n    Done.\n    Bind number to doubled:\n        Return one's size * 2.\n    Done.\nDone.\nDefine boxes as a series of box with (a new box { the size 2 }).\nFor each place in range 1 to 3, repeat:\n    Cast (item 1 of boxes)'s grow.\nDone.\nState boxes.",
        // Exceptions — In case of exception, the exception renamed, Suppress, raised again by default, nested, beside In case of failure, and a Try left early by Return, Stop, Skip or a failure passed off; path checks.
        "Try to:\n    State 1 / 0.\n    State \"not reached\".\nDone.\nIn case of exception:\n    State the message of the exception.\n    State the exception.\n    Suppress the exception.\nDone.\nState \"after\".",
        "Try to:\n    Define xs as a series of number with (1).\n    State item 3 of xs.\nDone.\nIn case of exception (the trouble):\n    State \"caught: {the message of the trouble}\".\n    Suppress the exception.\nDone.\nState \"after\".",
        "Bind number to risky, given (the number n):\n    Return 10 / n.\nDone.\nTry to:\n    State cast risky on (2).\n    State cast risky on (0).\nDone.\nIn case of exception:\n    State \"from a call: {the message of the exception}\".\n    Suppress the exception.\nDone.",
        "Try to:\n    Try to:\n        State 1 / 0.\n    Done.\n    In case of exception:\n        State \"inner\".\n    Done.\nDone.\nIn case of exception:\n    State \"outer: {the message of the exception}\".\n    Suppress the exception.\nDone.\nState \"done\".",
        "Bind number to early, given (the number n):\n    Try to:\n        If n is 1, return 5.\n        State 1 / 0.\n    Done.\n    In case of exception:\n        Suppress the exception.\n    Done.\n    Return 0.\nDone.\nState cast early on (1).\nState cast early on (2).\nTry to:\n    State 1 / 0.\nDone.\nIn case of exception:\n    State \"later caught: {the message of the exception}\".\n    Suppress the exception.\nDone.\n",
        "For each n in range 1 to 3, repeat:\n    Try to:\n        If n is 2, stop.\n        State n.\n    Done.\n    In case of exception:\n        Suppress the exception.\n    Done.\nDone.\nTry to:\n    State 1 / 0.\nDone.\nIn case of exception:\n    State \"later caught: {the message of the exception}\".\n    Suppress the exception.\nDone.\n",
        "For each n in range 1 to 3, repeat:\n    Try to:\n        If n is 2, skip.\n        State n.\n    Done.\n    In case of exception:\n        Suppress the exception.\n    Done.\nDone.\nTry to:\n    State 1 / 0.\nDone.\nIn case of exception:\n    State \"later caught: {the message of the exception}\".\n    Suppress the exception.\nDone.\n",
        "Bind number or failure to checked, given (the number n):\n    If n is 0, return a failure \"zero\".\n    Return n.\nDone.\nBind number or failure to passes, given (the number n):\n    Try to:\n        Define got as cast checked on (n) or pass the failure off.\n        Return got.\n    Done.\n    In case of exception:\n        Suppress the exception.\n    Done.\n    Return -1.\nDone.\nState (cast passes on (0) but on failure 99).\nTry to:\n    State 1 / 0.\nDone.\nIn case of exception:\n    State \"later caught: {the message of the exception}\".\n    Suppress the exception.\nDone.\n",
        "Bind number or failure to checked, given (the number n):\n    If n is 0, return a failure \"zero\".\n    Return n.\nDone.\nFor each n in a series with (0, 2, 5), repeat:\n    Try to:\n        Define got as cast checked on (n).\n        State 10 / (got - 2).\n    Done.\n    In case of failure:\n        State \"failed: {the message of the failure}\".\n    Done.\n    In case of exception:\n        State \"faulted: {the message of the exception}\".\n        Suppress the exception.\n    Done.\nDone.",
        "Try to:\n    State 1 / 0.\nDone.\nIn case of exception:\n    State \"handled once\".\n    Try to:\n        State 2 / 0.\n    Done.\n    In case of exception:\n        State \"handled inside the handler\".\n        Suppress the exception.\n    Done.\n    Suppress the exception.\nDone.\nState \"after both\".",
        "State the path \".\" exists.\nState the path \".\" is a directory.\nState the path \".\" is a file.\nState the path \"surely-not-here.txt\" exists.\nState the path \"surely-not-here.txt\" is a file.",
        // A Try left early by Return or Stop, then a fault in a Try that was already open — a frame left behind would catch it instead (found by sabotage: the first probes always faulted in a NEW Try, which hides one).
        "Bind number to early, given (the number n):\n    Try to:\n        If n is 1, return 5.\n        State 1 / 0.\n    Done.\n    In case of exception:\n        Suppress the exception.\n    Done.\n    Return 0.\nDone.\nTry to:\n    State cast early on (1).\n    State 1 / 0.\nDone.\nIn case of exception:\n    State \"the outer Try caught it: {the message of the exception}\".\n    Suppress the exception.\nDone.",
        "Try to:\n    For each n in range 1 to 3, repeat:\n        Try to:\n            If n is 2, stop.\n        Done.\n        In case of exception:\n            Suppress the exception.\n        Done.\n    Done.\n    State 1 / 0.\nDone.\nIn case of exception:\n    State \"the outer Try caught it after the stop\".\n    Suppress the exception.\nDone.",
        // A folder's contents, which can fail; the environment variable.
        "State the environment variable \"SURELY_NOT_SET_XYZ\".\nState (the environment variable \"SURELY_NOT_SET_XYZ\") is void.\nDefine os-name as the environment variable \"OS\" but void is \"none\".\nState the length of os-name is greater than 0.",
        "Try to:\n    Define found as the contents of the directory \"nowhere-at-all\".\n    State found.\nDone.\nIn case of failure:\n    State the message of the failure.\nDone.",
        "Try to:\n    Define found as the contents of the directory \".\".\n    State the number of found is greater than 0.\nDone.\nIn case of failure:\n    State \"could not list\".\nDone.",
        "Bind series of text or failure to listed, given (the text folder):\n    Define found as the contents of the directory folder or pass the failure off.\n    Return found.\nDone.\nState the number of (cast listed on (\"also-nowhere\") but on failure a series of text).",
        // Running a program — its exit code, output and errors, or a failure when it cannot start; writing a file; a voidable compared with a value of what it holds (found by the compiler compiling itself).
        "Define ages as a map with (\"a\": 1).\nState (the entry for \"a\" in ages) is 1.\nState (the entry for \"b\" in ages) is 1.\nState 1 is (the entry for \"a\" in ages).\nState (the entry for \"a\" in ages) is not 2.\nDefine label as a map with (\"k\": \"true\").\nIf (the entry for \"k\" in label) is \"true\", state \"yes\".\nIf (the entry for \"z\" in label) is \"true\", state \"no\".",
        "Try to:\n    Write \"x\" to the file \"surely-no-such-folder-here/f.txt\".\n    State \"not reached\".\nDone.\nIn case of failure:\n    State the message of the failure.\nDone.\nTry to:\n    Append \"x\" to the file \"surely-no-such-folder-here/g.txt\".\nDone.\nIn case of failure:\n    State \"append: {the message of the failure}\".\nDone.",
        "Define flags as a series of text with (\"-dumpversion\").\nTry to:\n    Define outcome as run \"gcc\" with arguments flags.\n    State the exit-code of outcome.\n    State the length of the output of outcome is greater than 0.\n    State the errors of outcome.\nDone.\nIn case of failure:\n    State the message of the failure.\nDone.",
        "Try to:\n    Define outcome as run \"gcc\" with arguments a series of text with (\"-x\", \"c\", \"-fsyntax-only\", \"no-such-file.c\").\n    State the exit-code of outcome is 0.\n    State the length of the errors of outcome is greater than 0.\nDone.\nIn case of failure:\n    State \"could not start\".\nDone.",
        "Try to:\n    Define outcome as run \"surely-no-such-program-anywhere\" with arguments a series of text.\n    State \"not reached\".\nDone.\nIn case of failure:\n    State \"failed to start\".\nDone.",
        "Bind number or failure to exit-of, given (the series of text flags):\n    Define outcome as run \"gcc\" with arguments flags or pass the failure off.\n    Return the exit-code of outcome.\nDone.\nState (cast exit-of on (a series of text with (\"-dumpversion\")) but on failure -1).",
        // Regions — a rabbit frees what it made, and keeps what got out: stores into something older, grown past capacity, a value of the rabbit's own handed out, nested rabbits, Return, Stop, Skip, a failure and an exception out of one.
        "For each round in range 1 to 50, repeat:\n    Pull a rabbit.\n        Define words as a series of text.\n        For each n in range 1 to 20, repeat:\n            Insert \"word {n}\" into words.\n        Done.\n        If round is 50, state the number of words.\n    Done.\nDone.",
        "Define label as \"\".\nDefine kept as a series of text.\nDefine counts as a map from text to number.\nDefine object box with (the text name).\nDefine b as a new box { the name \"first\" }.\nPull a rabbit.\n    For each n in range 1 to 20, repeat:\n        The label becomes \"label {n}\".\n        Insert \"kept {n}\" into kept.\n        In counts, the entry for \"k{n}\" becomes n * 2.\n    Done.\n    The name of b becomes \"made {label}\".\nDone.\nState label.\nState the number of kept.\nState item 20 of kept.\nState the entry for \"k20\" in counts.\nState b.",
        "Define held as \"\".\nPull a rabbit.\n    Define inner as a series of text.\n    For each n in range 1 to 12, repeat:\n        Insert \"inner {n}\" into inner.\n    Done.\n    Define one-word as item 12 of inner joined to \" and more\".\n    The held becomes one-word.\nDone.\nState held.",
        "Define outer as a series of text.\nPull a rabbit.\n    Define inner as a series of text with (\"a\", \"b\", \"c\").\n    For each item-word in inner, repeat:\n        Insert item-word joined to \"!\" into outer.\n        Insert item-word into outer.\n    Done.\nDone.\nState outer.",
        "Define outer as a series of text.\nPull a rabbit.\n    Define middle as a series of text.\n    Pull a rabbit.\n        Insert \"deep\" into middle.\n        Insert \"deeper\" into outer.\n    Done.\n    Insert item 1 of middle into outer.\nDone.\nState outer.",
        "Bind series of text to made, given (the number count):\n    Pull a rabbit.\n        Define found as a series of text.\n        For each n in range 1 to count, repeat:\n            Insert \"made {n}\" into found.\n        Done.\n        Return found.\n    Done.\nDone.\nDefine got as cast made on (10).\nState the number of got.\nState item 10 of got.",
        "Bind text to first-long, given (the series of text words):\n    For each word in words, repeat:\n        Pull a rabbit.\n            Define shouted as word in uppercase.\n            If the length of shouted is greater than 3, return shouted joined to \"!\".\n        Done.\n    Done.\n    Return \"none\".\nDone.\nState cast first-long on (a series of text with (\"a\", \"bb\", \"cccc\", \"ddddd\")).",
        "Define kept as a series of text.\nFor each n in range 1 to 10, repeat:\n    Pull a rabbit.\n        Define here as \"n{n}\".\n        If n is 3, skip.\n        If n is 6, stop.\n        Insert here into kept.\n    Done.\nDone.\nState kept.",
        "Bind number or failure to checked, given (the number n):\n    If n is 0, return a failure \"zero at {n}\".\n    Return n.\nDone.\nDefine kept as a series of text.\nTry to:\n    Pull a rabbit.\n        Insert \"before\" into kept.\n        Define got as cast checked on (0).\n        Insert \"not reached\" into kept.\n    Done.\nDone.\nIn case of failure:\n    State the message of the failure.\nDone.\nState kept.",
        "Define kept as a series of text.\nTry to:\n    Pull a rabbit.\n        Insert \"made {1 + 1}\" into kept.\n        State 1 / 0.\n    Done.\nDone.\nIn case of exception:\n    State \"caught\".\n    Suppress the exception.\nDone.\nState kept.\nInsert \"after\" into kept.\nState kept.",
        "Bind text to joined-up, given (the series of text parts):\n    Define out as \"\".\n    For each part in parts, the out becomes out joined to part.\n    Return out.\nDone.\nDefine result as \"\".\nPull a rabbit.\n    Define pieces as a series of text with (\"x\", \"y\", \"z\").\n    The result becomes cast joined-up on (pieces).\nDone.\nState result.",
        // Functions as values — a named function stored, passed, in a series and printed; lambdas; a function returning a function; captures as snapshots, a series captured live; a Bind inside a body calling itself; sorted by a function.
        "Bind number to double, given (the number x): Return x * 2. Done.\nDefine twice as double.\nState cast twice on (21).\nBind number to apply, given (the number value, the number function transform given (the number)):\n    Return cast transform on (value).\nDone.\nState cast apply on (5, double).\nState twice.",
        "Bind number to double, given (the number x): Return x * 2. Done.\nDefine triple as a function given (the number x): Return x * 3. Done.\nDefine ops as a series of number function given (the number) with (double, triple).\nState cast the first of ops on (5).\nFor each op in ops, repeat:\n    State cast op on (10).\nDone.\nState the number of ops.",
        "Bind number to apply, given (the number value, the number function transform given (the number)):\n    Return cast transform on (value).\nDone.\nState cast apply on (10, a function given (the number x): Return x * 2. Done).\nDefine shout as a function given (the text word): Return word in uppercase joined to \"!\". Done.\nState cast shout on (\"hey\").\nDefine say as a function given (the text word): State \"said {word}\". Done.\nCast say on (\"hi\").",
        "Bind number function given (the number) to make-adder, given (the number n):\n    Bind number to adder, given (the number x):\n        Return x + n.\n    Done.\n    Return adder.\nDone.\nDefine add-five as cast make-adder on (5).\nDefine add-ten as cast make-adder on (10).\nState cast add-five on (1).\nState cast add-ten on (1).\nState cast add-five on (100).",
        "Define rate as 2.\nDefine scaled as a function given (the number x): Return x * rate. Done.\nThe rate becomes 100.\nState cast scaled on (5).\nDefine label as \"before\".\nDefine show as a function given (the text tail): Return label joined to tail. Done.\nThe label becomes \"after\".\nState cast show on (\"!\").",
        "Define seen as a series of text.\nDefine note as a function given (the text word): Insert word into seen. Done.\nCast note on (\"a\").\nCast note on (\"b\").\nState seen.\nDefine object box with (the number size).\nDefine b as a new box { the size 1 }.\nDefine grow as a function given (the number by-much): The size of b becomes b's size + by-much. Done.\nCast grow on (4).\nState b.",
        "Bind number to sum-to, given (the number limit):\n    Bind number to go, given (the number n):\n        If n is 0, return 0.\n        Return n + cast go on (n - 1).\n    Done.\n    Return cast go on (limit).\nDone.\nState cast sum-to on (10).",
        "Bind number function given (the number) to make-scaler, given (the number factor):\n    Return a function given (the number x):\n        Define inner as a function given (the number y): Return y * factor. Done.\n        Return cast inner on (x) + 1.\n    Done.\nDone.\nDefine by-three as cast make-scaler on (3).\nState cast by-three on (5).",
        "Bind number to len-of, given (the text word):\n    State \"key of {word}\".\n    Return the length of word.\nDone.\nDefine words as a series of text with (\"pear\", \"fig\", \"banana\", \"kiwi\").\nState words sorted by len-of.\nState words sorted by a function given (the text w): Return the length of w. Done.\nState words sorted by len-of in reverse.\nState words sorted by a function given (the text w): Return the characters from 2 to 2 of w. Done.",
        "Bind number to double, given (the number x): Return x * 2. Done.\nDefine negate as a function given (the number x): Return 0 - x. Done.\nDefine chosen as double.\nFor each n in range 1 to 3, repeat:\n    If n is 3, the chosen becomes negate.\n    State cast chosen on (n).\nDone.",
        // Objects: defaults, blanks, setters, makers, function fields; a series literal in order.
        "Define object job with (the text title, the number copies with default 1, the text finish with default \"plain\").\nDefine memo as a new job { the title \"memo\" }.\nDefine poster as a new job { the title \"poster\", the copies 25, the finish \"gloss\" }.\nState memo.\nState poster.\nState \"{memo's copies} {poster's finish}\".\nDefine twin as a new job { the title \"memo\", the copies 1 }.\nState memo is twin.",
        "Define object bag with (the text owner, the series of text things with default a series of text).\nDefine one-bag as a new bag { the owner \"ada\" }.\nDefine two-bag as a new bag { the owner \"grace\" }.\nInsert \"pen\" into one-bag's things.\nState one-bag.\nState two-bag.",
        "Define counter as a series of number.\nBind number to next-id, given (the series of number seen):\n    Insert 0 into seen.\n    Return the number of seen * 10.\nDone.\nDefine object ticket with (the text name, the number id with default cast next-id on (counter)).\nDefine tickets as a series of ticket with (a new ticket { the name \"a\" }, a new ticket { the name \"b\" }, a new ticket { the name \"c\", the id 7 }).\nFor each t in tickets, repeat:\n    State \"{t's name} {t's id}\".\nDone.\nState the number of counter.",
        "Define object stack of element with (the series of element items):\n    Bind void to push, given (the element value):\n        Insert value into one's items.\n    Done.\n    Bind number to how-many:\n        Return the number of one's items.\n    Done.\nDone.\nDefine counts as a new stack of number { the items a series of number }.\nCast push on (counts, 5).\nCast push on (counts, 7).\nDefine names as a new stack of text { the items a series of text }.\nCast push on (names, \"Ada\").\nState cast how-many on counts.\nState counts.\nState names's items.\nDefine object pair of left-thing of right-thing with (the left-thing one-side, the right-thing other-side).\nDefine labelled as a new pair of number of text { the one-side 7, the other-side \"seven\" }.\nState \"{labelled's one-side} is {labelled's other-side}\".\nState labelled.",
        "Bind series of element to first-two, given (the series of element xs):\n    Define out as a series of element.\n    Insert the first of xs into out.\n    Insert item 2 of xs into out.\n    Return out.\nDone.\nBind voidable element to first-or-none, given (the series of element xs):\n    If the number of xs is 0, return void.\n    Return the first of xs.\nDone.\nDefine numbers as a series of number with (3, 6, 9).\nDefine words as a series of text with (\"red\", \"green\", \"blue\").\nState cast first-two on (numbers).\nState cast first-two on (words).\nState (cast first-or-none on (numbers)) but void is 0.\nState (cast first-or-none on (a series of text)) but void is \"(empty)\".",
        "Define object point with (the number x, the number y).\nDefine object box of thing with (the thing inside, the text label).\nDefine b as a new box of point { the inside a new point { the x 1, the y 2 }, the label \"box\" }.\nState b.\nState b's inside's x + b's inside's y.",
        "Define object tank with (the number litres, the number capacity):\n    Get fullness as number:\n        Return one's litres * 100 / one's capacity.\n    Done.\n    Set litres given (the number wanted):\n        If wanted is greater than one's capacity, one's litres becomes one's capacity.\n        Otherwise, one's litres becomes wanted.\n    Done.\n    Bind void to top-up:\n        One's litres becomes one's litres + 1000.\n    Done.\nDone.\nDefine reservoir as a new tank { the litres 0, the capacity 200 }.\nThe litres of reservoir becomes 50.\nState reservoir's fullness.\nThe reservoir's litres becomes 500.\nState reservoir.\nThe litres of reservoir becomes 10.\nCast top-up on (reservoir).\nState reservoir's litres.",
        "Define object gauge with (the number reading, the number writes).\nSet reading unto gauge given (the number wanted):\n    One's writes becomes one's writes + 1.\n    One's reading becomes wanted * 2.\nDone.\nDefine object panel with (the gauge left, the text name).\nDefine p as a new panel { the left a new gauge { the reading 0, the writes 0 }, the name \"p\" }.\nThe reading of p's left becomes 4.\nState p.\nDefine gauges as a series of gauge with (a new gauge { the reading 1, the writes 0 }).\nFor each g in gauges, repeat:\n    The reading of g becomes 9.\n    State g.\nDone.",
        "Bind number to tick, given (the series of number seen):\n    Insert 0 into seen.\n    State \"tick {the number of seen}\".\n    Return the number of seen.\nDone.\nDefine c as a series of number.\nState a series of number with (cast tick on (c), cast tick on (c), cast tick on (c)).",
        "Define object point with (the number x, the number y).\nBind making a point to origin:\n    Return a new point { the x 0, the y 0 }.\nDone.\nBind making a point or failure to from-pair, given (the text s):\n    Define parts as s split by \",\".\n    If the number of parts is not 2, return a failure \"expected x,y\".\n    Return a new point { the x 3, the y 4 }.\nDone.\nState cast origin.\nDefine p as cast from-pair on (\"3,4\") but on failure cast origin.\nState p.\nDefine q as cast from-pair on (\"oops\") but on failure cast origin.\nState q's x.",
        "Bind number to gift-wrap, given (the number price): Return price + 3. Done.\nBind number to halve, given (the number price): Return price / 2. Done.\nDefine object charge with (the text label, the number function apply given (the number)).\nDefine charges as a series of charge with (a new charge { the label \"wrap\", the apply gift-wrap }, a new charge { the label \"half\", the apply halve }).\nDefine total as 10.\nFor each c in charges, repeat:\n    The total becomes cast the apply of c on (total).\n    State \"{c's label}: {total}\".\nDone.\nDefine one-charge as the first of charges.\nThe apply of one-charge becomes a function given (the number n): Return n * 100. Done.\nState cast the apply of one-charge on (2).\nState one-charge.",
        "Define object reporter with (the text name, the text function show given (the series of number)):\n    Bind text to render, given (the series of number values):\n        Return \"{one's name}: {cast the show of one on (values)}\".\n    Done.\nDone.\nDefine r as a new reporter { the name \"sum\", the show a function given (the series of number xs):\n    Define total as 0.\n    For each x in xs, increment total by x.\n    Return total converted to text.\nDone }.\nState cast render on (r, a series of number with (1, 2, 3)).",
        // Operands worked out left to right: an object literal, a call, operators, joins, methods, function values, a Try.
        "Bind number to tick, given (the series of number seen):\n    Insert 0 into seen.\n    State \"tick {the number of seen}\".\n    Return the number of seen.\nDone.\nDefine c as a series of number.\nDefine object two with (the number first-one, the number second-one).\nState a new two { the first-one cast tick on (c), the second-one cast tick on (c) }.",
        "Bind number to tick, given (the series of number seen):\n    Insert 0 into seen.\n    State \"tick {the number of seen}\".\n    Return the number of seen.\nDone.\nDefine c as a series of number.\nBind number to add, given (the number x, the number y):\n    Return x * 10 + y.\nDone.\nState cast add on (cast tick on (c), cast tick on (c)).\nState cast tick on (c) - cast tick on (c).\nState cast tick on (c) is less than cast tick on (c).",
        "Bind number to tick, given (the series of number seen):\n    Insert 0 into seen.\n    State \"tick {the number of seen}\".\n    Return the number of seen.\nDone.\nDefine c as a series of number.\nState \"{cast tick on (c)} then {cast tick on (c)}\".\nState (cast tick on (c) converted to text) joined to (cast tick on (c) converted to text).",
        "Bind number to tick, given (the series of number seen):\n    Insert 0 into seen.\n    State \"tick {the number of seen}\".\n    Return the number of seen.\nDone.\nDefine c as a series of number.\nDefine object pad with (the number base):\n    Bind number to mix, given (the number x, the number y):\n        Return one's base + x * 10 + y.\n    Done.\nDone.\nDefine p as a new pad { the base 100 }.\nState cast mix on (p, cast tick on (c), cast tick on (c)).\nState cast p's mix on (cast tick on (c), cast tick on (c)).\nDefine f as a function given (the number x, the number y): Return x * 10 + y. Done.\nState cast f on (cast tick on (c), cast tick on (c)).",
        "Bind number to tick, given (the series of number seen):\n    Insert 0 into seen.\n    State \"tick {the number of seen}\".\n    Return the number of seen.\nDone.\nDefine c as a series of number.\nBind number or failure to checked, given (the number x):\n    State \"checking {x}\".\n    If x is greater than 2, return a failure \"too big\".\n    Return x.\nDone.\nBind number to add, given (the number x, the number y):\n    Return x * 10 + y.\nDone.\nTry to:\n    State cast add on (cast checked on (1), cast checked on (2)).\n    State cast add on (cast checked on (3), cast checked on (cast tick on (c))).\nDone.\nIn case of failure:\n    State \"failed: {the message of the failure}\".\nDone.\nState the number of c.",
        "Bind number to tick, given (the series of number seen):\n    Insert 0 into seen.\n    State \"tick {the number of seen}\".\n    Return the number of seen.\nDone.\nDefine c as a series of number.\nIf cast tick on (c) is 5 and cast tick on (c) is 2, State \"both\".\nState the number of c.",
        // Defaults in an object with a blank, carried into each filling.
        "Define object point with (the number x, the number y).\nDefine object box of thing with (the thing inside, the text label with default \"box\").\nDefine b as a new box of point { the inside a new point { the x 1, the y 2 } }.\nDefine c as a new box of number { the inside 5, the label \"crate\" }.\nState b.\nState c.",
        "Define object stack of element with (the text name, the series of element items with default a series of element).\nDefine one-stack as a new stack of number { the name \"a\" }.\nDefine two-stack as a new stack of number { the name \"b\" }.\nInsert 3 into one-stack's items.\nState one-stack.\nState two-stack.\nDefine words as a new stack of text { the name \"w\" }.\nInsert \"hi\" into words's items.\nState words.",
        // Regions as values — a named rabbit, an object that is a region — stashes, series of series and of voidables, a closure capturing 'one'.
        "Bind void to show, given (the rabbit r):\n    State r.\nDone.\nPull a rabbit as hopper.\n    State hopper.\n    Cast show on (hopper).\n    Pull a rabbit as grace.\n        Cast show on (grace).\n        State hopper.\n    Done.\nDone.",
        "Bind number to counting-up, given (the rabbit helper, the number first-value):\n    Define next as first-value.\n    Repeat:\n        Have helper bury next.\n        The next becomes next + 1.\n    Until false.\nDone.\nPull a rabbit as hopper.\n    Define counter as cast counting-up on (hopper, 3).\n    State unbury counter.\n    State unbury counter.\n    Define other as cast counting-up on (hopper, 100).\n    State unbury other.\n    State unbury counter.\n    State unbury other.\nDone.",
        "Bind text to long-words-in, given (the rabbit helper, the series of text words):\n    For each word in words, repeat:\n        If the length of word is less than 4, skip.\n        Have helper bury word.\n    Done.\nDone.\nPull a rabbit as hopper.\n    Define long as cast long-words-in on (hopper, a series of text with (\"a\", \"tree\", \"of\", \"gardens\", \"is\", \"here\")).\n    State unbury long but void is \"-\".\n    State unbury long but void is \"-\".\n    State unbury long but void is \"-\".\n    State unbury long but void is \"-\".\n    Define again as cast long-words-in on (hopper, a series of text with (\"apple\", \"no\", \"melon\")).\n    For each word in again, repeat:\n        State \"word: {word}\".\n    Done.\nDone.",
        "Bind number to counting-up, given (the rabbit helper, the number first-value):\n    Define next as first-value.\n    Repeat:\n        Have helper bury next.\n        The next becomes next + 1.\n    Until false.\nDone.\nBind void to take-three, given (the stash of number source, the text label):\n    For each n in range 1 to 3, repeat:\n        State \"{label}: {unbury source but void is 0}\".\n    Done.\nDone.\nPull a rabbit as hopper.\n    Cast take-three on (cast counting-up on (hopper, 10), \"tens\").\n    Define stashes as a series of stash of number with (cast counting-up on (hopper, 1), cast counting-up on (hopper, 50)).\n    For each one-stash in stashes, repeat:\n        State unbury one-stash but void is 0.\n        State unbury one-stash but void is 0.\n    Done.\nDone.",
        "Bind number to counting-up, given (the rabbit helper, the number first-value):\n    Define next as first-value.\n    Repeat:\n        Have helper bury next.\n        The next becomes next + 1.\n    Until false.\nDone.\nDefine object counter-from with (the number first-beat, the text name):\n    Bind number to ticks, given (the rabbit helper):\n        Define next as one's first-beat.\n        Repeat:\n            Have helper bury next.\n            The next becomes next + 1.\n        Until false.\n    Done.\nDone.\nBind number to evens-of, given (the rabbit helper):\n    Define inner as cast counting-up on (helper, 1).\n    For each value in inner, repeat:\n        If value is greater than 8:\n            Stop.\n        Done.\n        If value % 2 is 0:\n            Have helper bury value.\n        Done.\n    Done.\nDone.\nPull a rabbit as hopper.\n    Define c as a new counter-from { the first-beat 7, the name \"seven\" }.\n    Define ticker as cast ticks on (c, hopper).\n    State unbury ticker.\n    State unbury ticker.\n    Define evens as cast evens-of on (hopper).\n    For each even in evens, repeat:\n        State \"even {even}\".\n    Done.\nDone.",
        "Define object workshop with () and region.\nDefine object job with (the text title, the number copies with default 1).\nBind number to tickets-from, given (the workshop bench, the number first-ticket):\n    Define next as first-ticket.\n    Repeat:\n        Have bench bury next.\n        The next becomes next + 1.\n    Until false.\nDone.\nDefine kept as a series of text.\nState \"open\".\nPull workshop as floor.\n    Define tickets as cast tickets-from on (floor, 100).\n    Define memo as a new job { the title \"memo\" }.\n    State \"ticket {unbury tickets but void is 0}: {memo's title}, {memo's copies}\".\n    State \"ticket {unbury tickets but void is 0}\".\n    Insert \"from the floor\" joined to \" too\" into kept.\nDone.\nState kept.\nState \"closed\".",
        "Define object box with (the number size):\n    Bind number function given (the number) to scaler:\n        Return a function given (the number x): Return x * one's size. Done.\n    Done.\n    Bind void to grow:\n        One's size becomes one's size + 1.\n    Done.\nDone.\nDefine b as a new box { the size 3 }.\nDefine by-three as cast scaler on (b).\nCast grow on (b).\nState cast by-three on (10).\nState cast (cast scaler on (b)) on (10).",
        "Define grid as a series of series of number with (a series of number with (1, 2), a series of number with (3, 4, 5)).\nInsert a series of number with (6) into grid.\nState grid.\nState the number of item 2 of grid.\nInsert 9 into item 1 of grid.\nState item 1 of grid.\nDefine total as 0.\nFor each row in grid, repeat:\n    For each n in row, increment total by n.\nDone.\nState total.\nDefine same-grid as a series of series of number with (a series of number with (1, 2, 9), a series of number with (3, 4, 5), a series of number with (6)).\nState grid is same-grid.\nDefine maybes as a series of voidable number.\nInsert 1 into maybes.\nInsert void into maybes.\nInsert 3 into maybes.\nState maybes.\nFor each m in maybes, repeat:\n    State m but void is -1.\nDone.\nDefine words as a series of series of text.\nInsert a series of text with (\"a\", \"b\") into words.\nState words.",
        // A `For each` over a call drains one stash, made before the loop.
        "Bind number to counting-up, given (the rabbit helper, the number first-value):\n    Define next as first-value.\n    Repeat:\n        Have helper bury next.\n        The next becomes next + 1.\n    Until false.\nDone.\nBind number to evens-of, given (the rabbit helper):\n    Define inner as cast counting-up on (helper, 1).\n    For each value in inner, repeat:\n        If value is greater than 8:\n            Stop.\n        Done.\n        If value % 2 is 0:\n            Have helper bury value.\n        Done.\n    Done.\nDone.\nPull a rabbit as hopper.\n    Define n as 0.\n    For each even in cast evens-of on (hopper), repeat:\n        State \"even {even}\".\n        Increment n by 1.\n        If n is 6, stop.\n    Done.\nDone.",
        "Bind number to counting-up, given (the rabbit helper, the number first-value):\n    Define next as first-value.\n    Repeat:\n        Have helper bury next.\n        The next becomes next + 1.\n    Until false.\nDone.\nBind number to evens-of, given (the rabbit helper):\n    Define tries as 0.\n    For each value in cast counting-up on (helper, 1), repeat:\n        Increment tries by 1.\n        If value is greater than 8 or tries is 20:\n            Stop.\n        Done.\n        If value % 2 is 0:\n            Have helper bury value.\n        Done.\n    Done.\nDone.\nPull a rabbit as hopper.\n    Define evens as cast evens-of on (hopper).\n    For each even in evens, repeat:\n        State \"even {even}\".\n    Done.\n    For each word in a series of text with (\"x\", \"y\"), repeat:\n        State word.\n    Done.\nDone.",
        // Bits: literals, arithmetic, gates, shifts, conversions, comparing, widths, held in a series, map and object.
        "Define high as 0xFF.\nDefine low as 0b1010.\nState high.\nState low.\nState high + low.\nState low + high.\nState high - low.\nState low * 0b11.\nState high / 0b10.\nState high % 0b111.",
        "Define flags as 0b1111.\nState flags and not 0b0100.\nState 0xFF and 0b1010.\nState 0b1010 and 0xFF.\nState 0b1100 or 0b0011.\nState 0b1100 xor 0b1010.\nState not 0b0000.",
        "State 0b0001 shifted left by 3.\nState 0xFF shifted right by 4.\nState 0b00001111 shifted left by 2.\nState 0b1 shifted left by 2.\nState 0b0 shifted left by 2.\nDefine steps as 3.\nState 0b1 shifted left by (steps + 1).",
        "State 255 converted to hex.\nState 10 converted to binary.\nState 0xFF converted to number.\nState (0xFF converted to number) + 1.\nState 0xFF converted to text.\nDefine mask as 0b101.\nState \"mask is {mask}\".\nDefine total as 200 + 55.\nState total converted to hex.",
        "State 0xFF is 0b11111111.\nState 0x00FF is 0xFF.\nState 0b1 is not 0b10.\nState 0b0101 is less than 0x6.\nState 0xA is greater than 0b1001.\nState 0b11 is not greater than 0x3.",
        "State the width of 0x0F.\nState the width of 0b101.\nState 0b0 at 3 bits.\nDefine n as 6.\nState 0b1 at n bits.\nState 0b00000001 at 2 bits.\nState the width of (0b1 at n bits).",
        "Define object flagset with (the text name, the bits mask).\nDefine modes as a series of flagset with (a new flagset { the name \"read\", the mask 0b100 }, a new flagset { the name \"write\", the mask 0b010 }).\nFor each mode in modes, repeat:\n    State \"{mode's name} = {mode's mask}\".\nDone.\nDefine by-name as a map from text to bits with (\"exec\" : 0b001).\nState the entry for \"exec\" in by-name but void is 0b0.\nState the entry for \"none\" in by-name but void is 0b0.\nBind bits to both, given (the bits left-mask, the bits right-mask):\n    Return left-mask or right-mask.\nDone.\nState cast both on (0b100, 0b001).\nDefine masks as a series of bits with (0x1, 0x2, 0x4).\nState masks.\nDefine combined as 0x0.\nFor each m in masks, the combined becomes combined or m.\nState combined.\nState modes.",
        // Sets, streams, operator overloads, and a voidable union narrowed twice.
        "Define seen as a set of text with (\"b\", \"a\").\nInsert \"c\" into seen.\nInsert \"a\" into seen.\nState seen.\nState seen has \"a\".\nState seen has \"z\".\nState the number of seen.\nFor each s in seen, repeat:\n    State s.\nDone.\nDefine nums as a set of number.\nState nums.\nDefine other as a set of text with (\"a\", \"b\", \"c\").\nState seen is other.\nState seen is seen.",
        "Bind void to note, given (the set of number marks, the number value):\n    Insert value into marks.\nDone.\nDefine marks as a set of number.\nFor each n in a series of number with (3, 1, 3, 2, 1), repeat:\n    If marks has n, state \"again {n}\".\n    Otherwise, cast note on (marks, n).\nDone.\nState marks.\nState the number of marks.",
        "Define first as read a line from the input.\nState first but void is \"(nobody)\".\nState \"[{read all from the input}]\".\nState the number of (read all lines from the input).\nIf (read a line from the input) is void, state \"still nobody\".",
        "Write \"a\" to the output.\nWrite \"b\" to the output.\nState \"c\".\nWrite \"d\" joined to \"\\n\" to the output.\nBind void to say, given (the writable stream of text target, the text what):\n    Write what to target.\nDone.\nCast say on (the output, \"through a parameter\\n\").",
        "Try to:\n    With the file \"no-such-file.txt\" open for reading as src:\n        State read all from src.\n    Done.\nDone.\nIn case of failure:\n    State \"could not open: {the message of the failure}\".\nDone.\nState \"after\".",
        "Define object vec2 with (the number x, the number y).\nBind overloading +, given (the lhs is a vec2, the rhs is a vec2):\n    Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.\nDone.\nBind overloading *, given (the lhs is a vec2, the rhs is a number):\n    Return a new vec2 { the x lhs's x * rhs, the y lhs's y * rhs }.\nDone.\nBind overloading *, given (the lhs is a number, the rhs is a vec2):\n    Return a new vec2 { the x rhs's x * lhs, the y rhs's y * lhs }.\nDone.\nBind overloading *, given (the lhs is a vec2, the rhs is a vec2):\n    Return lhs's x * rhs's x + lhs's y * rhs's y.\nDone.\nDefine here as a new vec2 { the x 3, the y 4 }.\nDefine step as a new vec2 { the x 1, the y 2 }.\nState here + step.\nState here * 2.\nState 2 * here.\nState here * step.\nState here + step + step.\nState (here + step) * 10.\nState 3 * 4.\nBind vec2 to doubled, given (the vec2 v):\n    Return v + v.\nDone.\nState cast doubled on (here).",
        "Define object money with (the number cents).\nBind overloading -, given (the lhs is a money, the rhs is a money):\n    If lhs's cents is less than rhs's cents:\n        Return a new money { the cents 0 }.\n    Done.\n    Return a new money { the cents lhs's cents - rhs's cents }.\nDone.\nBind overloading /, given (the lhs is a money, the rhs is a number):\n    Return a new money { the cents lhs's cents / rhs }.\nDone.\nDefine wallet as a new money { the cents 500 }.\nState wallet - a new money { the cents 120 }.\nState wallet - a new money { the cents 900 }.\nState wallet / 4.",
        "Define settings as a map from text to (text or number) with (\"host\" : \"localhost\", \"port\" : 80).\nDefine host as the entry for \"host\" in settings.\nIf host is not void, if host is a text, state \"connecting to {host}\".\nDefine port as the entry for \"port\" in settings.\nIf port is not void, if port is a number, state \"port {port + 1}\".\nIf host is not void:\n    If host is a text:\n        State \"block form {host}\".\n    Done.\nDone.",
        // Tasks and channels: each printing in one order only.
        "Pull a rabbit.\n    Define base as 40.\n    Have rabbit start a task as adder:\n        Return base + 2.\n    Done.\n    State the awaited result of adder.\n    State \"after\".\nDone.\nState \"done\".",
        "Pull a rabbit.\n    Have rabbit start a task:\n        State \"in the task\".\n    Done.\nDone.\nState \"after the rabbit\".",
        "Pull a rabbit.\n    Define ch as a channel of number.\n    Have rabbit start a task:\n        For each n in range 1 to 4, repeat:\n            Send n * 10 through ch.\n        Done.\n        Close ch.\n    Done.\n    Define got as the delivery from ch.\n    While got is not void, repeat:\n        State got.\n        The got becomes the delivery from ch.\n    Done.\nDone.",
        "Pull a rabbit.\n    Define words as a series of text with (\"a\", \"bb\", \"ccc\").\n    Have rabbit start a task as shouted:\n        Define out as a series of text.\n        For each w in words, insert w in uppercase into out.\n        Return out.\n    Done.\n    Define back as the awaited result of shouted.\n    State back.\n    State words.\nDone.",
        "Pull a rabbit.\n    Define ch-list as a channel of series of series of number.\n    Define ch-map as a channel of map from text to series of number.\n    Have rabbit start a task as producer:\n        Define inner as a series of number with (10, 20, 30).\n        Define outer as a series of series of number.\n        Insert inner into outer.\n        Send outer through ch-list.\n        Insert 999 into inner.\n        Close ch-list.\n        Define data as a map from text to series of number.\n        In data, the entry for \"batch\" becomes inner.\n        Send data through ch-map.\n        Close ch-map.\n    Done.\n    Define received as the delivery from ch-list.\n    While received is not void, repeat:\n        Define holding as received but void is (a series of series of number).\n        State holding.\n        The received becomes the delivery from ch-list.\n    Done.\n    Define delivered as the delivery from ch-map.\n    While delivered is not void, repeat:\n        State delivered but void is (a map from text to series of number).\n        The delivered becomes the delivery from ch-map.\n    Done.\nDone.",
        "Define object point with (the number x, the number y).\nPull a rabbit.\n    Define ch as a channel of point.\n    Have rabbit start a task:\n        Send a new point { the x 1, the y 2 } through ch.\n        Send a new point { the x 3, the y 4 } through ch.\n        Close ch.\n    Done.\n    Define p as the delivery from ch.\n    While p is not void, repeat:\n        State p but void is (a new point { the x 0, the y 0 }).\n        The p becomes the delivery from ch.\n    Done.\n    Have rabbit start a task as pair:\n        Return a record with (the left 5, the right \"five\").\n    Done.\n    State the awaited result of pair.\nDone.",
        "Pull a rabbit.\n    Have rabbit start a task as first:\n        Return 6.\n    Done.\n    Have rabbit start a task as second:\n        Return (the awaited result of first) * 7.\n    Done.\n    State the awaited result of second.\nDone.",
        "Bind number to square, given (the number n):\n    Return n * n.\nDone.\nFor each round in range 1 to 2, repeat:\n    Pull a rabbit.\n        Define next-round as round + 1.\n        Have rabbit start a task as worker:\n            Return cast square on (next-round).\n        Done.\n        State \"round {round}: {the awaited result of worker}\".\n    Done.\nDone.",
        // Matrices, and maps keyed by a record of keys.
        "Pull a book on collections.\n    Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n    State m.\n    State item at (2, 3) of m.\n    The item at (1, 2) of m becomes 20.\n    State m.\n    State \"{the rows of m} by {the columns of m}\".\nDone.",
        "Pull a book on collections.\n    Bind matrix to made, given (the number h, the number w):\n        Return a matrix with h by w filled with 7.\n    Done.\n    Bind void to marked, given (the matrix board):\n        The item at (the rows of board, the columns of board) of board becomes -1.\n    Done.\n    Define z as a matrix with 2 by 3.\n    State z.\n    Define m as cast made on (3, 2).\n    Cast marked on (m).\n    State m.\nDone.",
        "Pull a book on collections.\n    Define m as a matrix with ((1, 2), (3, 4)).\n    State m * 2.\n    State 3 * m.\n    State m * 0.5.\n    State m.\nDone.",
        "Pull a book on collections.\n    Define m as a matrix with ((1, 2), (3, 4)).\n    Define n as a matrix with ((5, 6), (7, 8)).\n    Define wide as a matrix with ((1, 2, 3), (4, 5, 6)).\n    Try to:\n        State m + n.\n        State n - m.\n        State m * n.\n        State m * wide.\n    Done.\n    In case of failure:\n        State \"failed: {the message of the failure}\".\n    Done.\nDone.",
        "Pull a book on collections.\n    Define m as a matrix with ((1, 2), (3, 4)).\n    Define wide as a matrix with ((1, 2, 3), (4, 5, 6)).\n    Try to:\n        State m + wide.\n    Done.\n    In case of failure:\n        State \"{the message of the failure} [{the category of the failure but void is \"none\"}]\".\n    Done.\n    Try to:\n        State m - wide.\n    Done.\n    In case of failure:\n        State the message of the failure.\n    Done.\n    Try to:\n        State wide * m.\n    Done.\n    In case of failure:\n        State the message of the failure.\n    Done.\n    State \"after\".\nDone.",
        "Pull a book on collections.\n    Bind matrix or failure to summed, given (the matrix left, the matrix right):\n        Define total as left + right or pass the failure off.\n        Return total * 10.\n    Done.\n    Define m as a matrix with ((1, 2), (3, 4)).\n    Define wide as a matrix with ((1, 2, 3), (4, 5, 6)).\n    State m + wide but on failure m.\n    State m * m but on failure wide.\n    Define back as cast summed on (m, m) but on failure wide.\n    State back.\n    Define gone as cast summed on (m, wide) but on failure wide.\n    State gone.\n    Try to:\n        State cast summed on (wide, m).\n    Done.\n    In case of failure:\n        State \"caught: {the message of the failure}\".\n    Done.\nDone.",
        "Pull a book on collections.\n    Define m as a matrix with ((1, 2), (3, 4)).\n    Define same as m.\n    Define other as a matrix with ((1, 2), (3, 4)).\n    State m is same.\n    State m is other.\n    State m is not other.\n    The item at (1, 1) of same becomes 9.\n    State m.\nDone.",
        "Pull a book on collections.\n    Bind number to said, given (the number n):\n        State \"working out {n}\".\n        Return n.\n    Done.\n    Bind matrix to grid, given (the text why):\n        State \"the matrix for {why}\".\n        Return a matrix with ((1, 2), (3, 4)).\n    Done.\n    The item at (cast said on (1), cast said on (2)) of cast grid on (\"a set\") becomes cast said on (5).\n    State item at (cast said on (2), cast said on (1)) of cast grid on (\"a read\").\n    State a matrix with cast said on (1) by cast said on (2) filled with cast said on (3).\n    State a matrix with ((cast said on (1), cast said on (2)), (cast said on (3), cast said on (4))).\n    State cast grid on (\"scaled\") * cast said on (2).\n    State cast said on (3) * cast grid on (\"scaled again\").\nDone.",
        "Pull a book on collections.\n    Define m as a matrix with 3 by 4.\n    For each r in range 1 to the rows of m, repeat:\n        For each c in range 1 to the columns of m, repeat:\n            The item at (r, c) of m becomes r * 10 + c.\n        Done.\n    Done.\n    Define total as 0.\n    For each r in range 1 to the rows of m, repeat:\n        For each c in range 1 to the columns of m, repeat:\n            Increment total by item at (r, c) of m.\n        Done.\n    Done.\n    State m.\n    State total.\nDone.",
        "Pull a book on collections.\n    Define kept as a matrix with 1 by 1.\n    Pull a rabbit.\n        Define made as a matrix with ((1, 2), (3, 4)).\n        The kept becomes made * 3.\n    Done.\n    State kept.\nDone.",
        "Pull a book on collections.\n    Define object board with (the text name, the matrix cells).\n    Define b as a new board { the name \"tiny\", the cells a matrix with ((0, 1), (1, 0)) }.\n    The item at (1, 1) of b's cells becomes 5.\n    State b's name.\n    State b's cells.\n    State item at (2, 1) of b's cells.\nDone.",
        "Pull a book on collections.\n    Bind matrix or failure to squared, given (the matrix m):\n        Return m * m or pass the failure off.\n    Done.\n    Try to:\n        State cast squared on (a matrix with ((1, 1), (1, 0))).\n        State cast squared on (a matrix with ((1, 2, 3), (4, 5, 6))).\n    Done.\n    In case of failure:\n        State \"failed: {the message of the failure}\".\n    Done.\nDone.",
        "Define grid as a map from record like (number, number) to text.\nIn grid, the entry for a record with (0, 0) becomes \"start\".\nIn grid, the entry for a record with (3, 4) becomes \"treasure\".\nIn grid, the entry for a record with (0, 0) becomes \"home\".\nState the size of grid.\nState the entry for a record with (3, 4) in grid but void is \"none\".\nState the entry for a record with (4, 3) in grid but void is \"none\".\nState grid has a key for a record with (0, 0).\nState grid.\nRemove a record with (0, 0) from grid.\nFor each pair in grid, state \"{the first of the key of pair} {the second of the key of pair} {the value of pair}\".",
        "Define spots as a map from record like (the number x, the text tag) to number.\nIn spots, the entry for a record with (the x 1, the tag \"a\") becomes 10.\nIn spots, the entry for a record with (the tag \"a\", the x 1) becomes 20.\nState spots.\nDefine deep as a map from record like (number, record like (text, fact)) to text.\nIn deep, the entry for a record with (1, a record with (\"x\", true)) becomes \"found\".\nState the entry for a record with (1, a record with (\"x\", true)) in deep but void is \"none\".\nState the entry for a record with (1, a record with (\"x\", false)) in deep but void is \"none\".",
        "Define flags as a map from bits to text.\nIn flags, the entry for 0xA becomes \"ten\".\nState the entry for 0b1010 in flags but void is \"none\".\nDefine maybe as a map from voidable number to text.\nIn maybe, the entry for void becomes \"nothing\".\nIn maybe, the entry for 3 becomes \"three\".\nState maybe.\nDefine either as a map from (number or text) to text.\nIn either, the entry for 7 becomes \"number seven\".\nIn either, the entry for \"seven\" becomes \"text seven\".\nState either.\nState the entry for \"seven\" in either but void is \"none\".",
        // Unmakers: every way out of a block.
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nIf true:\n    Define outer as a new res { the name \"outer\" }.\n    For each n in range 1 to 4, repeat:\n        Define inner as a new res { the name \"inner {n}\" }.\n        If n is 1, skip.\n        If n is 3, stop.\n        State \"through {n}\".\n    Done.\n    State \"after the loop\".\nDone.",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nIf true:\n    Define first as a new res { the name \"first\" }.\n    Define second as a new res { the name \"second\" }.\n    State \"in the block\".\nDone.\nState \"after\".",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nBind void to opens:\n    Define inside as a new res { the name \"function body\" }.\n    State \"in the function\".\nDone.\nDefine top as a new res { the name \"top\" }.\nCast opens.\nState \"end\".",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nFor each n in range 1 to 4, repeat:\n    Define per-round as a new res { the name \"round {n}\" }.\n    If n is 2, skip.\n    If n is 3, stop.\n    State \"through {n}\".\nDone.\nDefine k as 0.\nWhile k is less than 2, repeat:\n    Define held as a new res { the name \"while {k}\" }.\n    Increment k by 1.\nDone.\nState \"after the loops\".",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nBind number to found, given (the number wanted):\n    If true:\n        Define outer as a new res { the name \"outer\" }.\n        For each n in range 1 to 5, repeat:\n            Define inner as a new res { the name \"inner {n}\" }.\n            If n is wanted, return n * 10.\n        Done.\n    Done.\n    Return 0.\nDone.\nState cast found on (2).\nState cast found on (9).",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nBind number or failure to checked, given (the number n):\n    If n is greater than 2, return a failure \"too big\".\n    Return n.\nDone.\nTry to:\n    If true:\n        Define guard as a new res { the name \"guard\" }.\n        State cast checked on (1).\n        State cast checked on (5).\n        State \"not reached\".\n    Done.\nDone.\nIn case of failure:\n    Define handler as a new res { the name \"handler\" }.\n    State \"failed: {the message of the failure}\".\nDone.\nState \"after\".",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nDefine zero as 0.\nTry to:\n    If true:\n        Define guard as a new res { the name \"guard\" }.\n        State 1 / zero.\n    Done.\nDone.\nIn case of exception:\n    State \"caught\".\n    Suppress the exception.\nDone.\nState \"after\".",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nBind res to made:\n    If true:\n        Define made-one as a new res { the name \"made\" }.\n        Return made-one.\n    Done.\n    Return a new res { the name \"never\" }.\nDone.\nIf true:\n    Define original as a new res { the name \"original\" }.\n    Define copy as original.\n    Define got as cast made.\n    State \"got {got's name}\".\nDone.",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nPull a rabbit as hopper.\n    Define shared as a new res { the name \"rabbit's\" }.\n    Have hopper start a task as job:\n        Define own as a new res { the name \"task's\" }.\n        Return 7.\n    Done.\n    State \"awaited {the awaited result of job}\".\nDone.\nState \"after\".",
        "Define object counter with (the text label, the number count):\n    Bind text to described:\n        Return \"{one's label} at {one's count}\".\n    Done.\nDone.\nBind unmaking a counter to report:\n    State \"unmade {cast described on one}\".\nDone.\nPull a book on collections.\n    Define kept as a new counter { the label \"book\", the count 1 }.\n    Define words as a series of text with (\"a\").\n    State \"in the pull\".\nDone.\nDefine pick as 2.\nJudge pick, where it is:\n    1, state \"one\".\n    2:\n        Define armed as a new counter { the label \"arm\", the count 2 }.\n        The count of armed becomes 3.\n    Done.\n    Otherwise, state \"other\".\nDone.\nState \"end\".",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nDefine object lock with (the number id).\nBind unmaking a lock to releasing:\n    State \"released {one's id}\".\nDone.\nIf true:\n    Define a-res as a new res { the name \"r1\" }.\n    Define a-lock as a new lock { the id 1 }.\n    Define b-res as a new res { the name \"r2\" }.\n    If true:\n        Define b-lock as a new lock { the id 2 }.\n    Done.\n    State \"middle\".\nDone.",
        "Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nPull a rabbit as hopper.\n    Define shared as a new res { the name \"rabbit's\" }.\n    Define go as a channel of number.\n    Have hopper start a task:\n        Define got as the delivery from go.\n        For each n in range 1 to 200, repeat:\n            State \"line {n}\".\n        Done.\n    Done.\n    Send 1 through go.\nDone.\nState \"after\".",
        // Task pipes: function stages, each fed what the one before outputs.
        "Bind void to emit-numbers:\n    Output 1.\n    Output 2.\n    Output 3.\n    Output 4.\nDone.\nBind void to keep-even:\n    For each n from the input:\n        If n % 2 is 0, output n.\n    Done.\nDone.\nBind void to show:\n    For each n from the input:\n        State \"kept {n converted to text}\".\n    Done.\nDone.\nemit-numbers | keep-even | show.\nState \"after the pipe\".",
        "Bind void to emit-words:\n    Output \"a\".\n    Output \"bbb\".\n    Output \"cc\".\nDone.\nBind void to lengths:\n    For each w from the input:\n        Output the length of w.\n    Done.\nDone.\nBind void to show:\n    For each n from the input:\n        State \"len {n}\".\n    Done.\nDone.\nemit-words | lengths | show.",
        "Bind void to report, given (the series of number sums):\n    Bind void to emit-sums, for each s in sums, output s.\n    Bind void to keep-large, for each s from the input, if s is greater than 20, output s.\n    Bind void to print-labelled, for each s from the input, state \"large: {s}\".\n    emit-sums | keep-large | print-labelled.\n    State \"reported\".\nDone.\nCast report on (a series of number with (15, 40, 65, 7)).\nCast report on (a series of number with (21)).",
        "Bind void to counts:\n    For each n in range 1 to 4, output n.\nDone.\nBind void to squares:\n    For each n from the input:\n        Define squared as n * n.\n        Output squared.\n    Done.\nDone.\nBind void to grouped:\n    Define held as a series of number.\n    For each n from the input:\n        Insert n into held.\n        If the number of held is 2:\n            Output held.\n            The held becomes a series of number.\n        Done.\n    Done.\nDone.\nBind void to shown:\n    For each pair from the input, state pair.\nDone.\ncounts | squares | grouped | shown.",
        "Bind void to people:\n    Output a record with (the name \"ada\", the age 36).\n    Output a record with (the name \"bob\", the age 12).\n    Output a record with (the name \"cy\", the age 50).\n    Output a record with (the name \"di\", the age 70).\nDone.\nBind void to adults:\n    For each person from the input:\n        If the age of person is less than 18, skip.\n        Output the name of person.\n    Done.\nDone.\nBind void to first-two:\n    Define seen as 0.\n    For each name from the input:\n        State name.\n        Increment seen by 1.\n        If seen is 2, stop.\n    Done.\n    State \"seen {seen}\".\nDone.\npeople | adults | first-two.",
        "Bind void to some:\n    Output 3.\n    Output 1.\nDone.\nBind void to doubled:\n    For each n from the input, output n * 2.\nDone.\nBind void to said:\n    For each n from the input, state n.\nDone.\nBind void to twice:\n    some | said.\n    some | doubled | said.\nDone.\nFor each round in range 1 to 2, repeat:\n    State \"round {round}\".\n    Cast twice.\nDone.",
        "Bind void to letters:\n    For each letter in a series of text with (\"x\", \"y\", \"z\"), output letter in uppercase.\nDone.\nBind void to printed:\n    For each letter from the input, state \"[{letter}]\".\nDone.\nletters | printed.",
        // The chance book: each prints only what the draw does not decide — the backends' generators differ.
        "Pull a book on chance.\n    Define outside as 0.\n    Define fractional as 0.\n    For each n in range 1 to 1000, repeat:\n        Define roll as a random number from 1 to 6.\n        If roll is less than 1 or roll is greater than 6, increment outside by 1.\n        If roll % 1 is not 0, increment fractional by 1.\n    Done.\n    State \"outside {outside}, fractional {fractional}\".\n    State a random number from 4 to 4.\n    State a random number from 2.5 to 2.9.\n    State a random number from -2.5 to -2.1.\n    State a random number from -1 to -1.\nDone.",
        "Pull a book on chance.\n    Define seen as a series of number with (0, 0, 0).\n    For each n in range 1 to 600, repeat:\n        Define face as a random number from 1 to 3.\n        Item face of seen becomes item face of seen + 1.\n    Done.\n    State \"every face: {item 1 of seen is greater than 0 and item 2 of seen is greater than 0 and item 3 of seen is greater than 0}\".\n    State \"total: {item 1 of seen + item 2 of seen + item 3 of seen}\".\n    Define heads as 0.\n    Define tails as 0.\n    For each n in range 1 to 400, repeat:\n        If a random guess, increment heads by 1.\n        Otherwise, increment tails by 1.\n    Done.\n    State \"both: {heads is greater than 0 and tails is greater than 0}, total {heads + tails}\".\nDone.",
        "Pull a book on chance.\n    Define names as a series of text with (\"ada\", \"bob\", \"cy\").\n    Define strays as 0.\n    For each n in range 1 to 300, repeat:\n        Define pick as a random item from names but void is \"none\".\n        If pick is not \"ada\" and pick is not \"bob\" and pick is not \"cy\", increment strays by 1.\n    Done.\n    State \"strays: {strays}\".\n    Define empty as a series of text.\n    State a random item from empty but void is \"nothing to pick\".\n    State a random item from a series of number with (7) but void is 0.\n    Define pairs as a series of records like (text, number) with (a record with (\"x\", 1)).\n    Define drawn as a random item from pairs.\n    If drawn is not void, state the first of drawn.\nDone.",
        "Pull a book on chance.\n    Define original as a series of number with (5, 3, 9, 1, 7).\n    For each n in range 1 to 50, repeat:\n        Define mixed as randomly shuffled original.\n        If (mixed sorted) is not (original sorted), state \"lost an item\".\n        If the number of mixed is not 5, state \"wrong length\".\n    Done.\n    State original.\n    Define kept as randomly shuffled original.\n    Insert 100 into kept.\n    State the number of original.\n    State randomly shuffled a series of text with (\"only\").\n    Define nothing as a series of number.\n    State randomly shuffled nothing.\nDone.",
        "Pull a book on chance.\n    Bind series of number to draws:\n        Define out as a series of number.\n        For each n in range 1 to 20, insert a random number from 1 to 1000 into out.\n        Insert (a random item from a series of number with (10, 20, 30) but void is 0) into out.\n        For each kept in randomly shuffled a series of number with (1, 2, 3, 4), insert kept into out.\n        If a random guess, insert 1 into out.\n        Otherwise, insert 0 into out.\n        Return out.\n    Done.\n    Seed the chance with 42.\n    Define first as cast draws.\n    Seed the chance with 42.\n    Define second as cast draws.\n    State first is second.\n    Seed the chance with 42.7.\n    Define cut as cast draws.\n    State cut is first.\nDone.",
        // `cufet` axioms: one that gives something back is a function; a cited one is source.
        "Pull a book on cufet.\n    Define cufet number summed, given (the series of number values), as [\n        Define the total as 0.\n        For each value in the values, repeat:\n            The total becomes the total + value.\n        Done.\n        Return the total.\n    ].\n    Define cufet series of text doubled, given (the text word, the number times), as [\n        Define out as a series of text.\n        For each n in range 1 to the times, insert the word into out.\n        Return out.\n    ].\n    State cast summed on (a series of number with (1, 2, 3, 4)).\n    State cast doubled on (\"ha\", 3).\nDone.",
        "Pull a book on cufet.\n    Define cufet shapes as [\n        Define object box with (the number wide, the number high):\n            Bind number to area:\n                Return one's wide * one's high.\n            Done.\n        Done.\n        Define the unit as 10 permanently.\n        Bind number to scaled, given (the number value):\n            Return value * the unit.\n        Done.\n    ].\n    Cite shapes.\n    Define small as a new box { the wide 2, the high 3 }.\n    State cast area on (small).\n    State cast scaled on (cast area on (small)).\n    State the unit.\nDone.",
        "Pull a book on cufet.\n    Define cufet fresh-count as [\n        Define the count as 1.\n    ].\n    Bind number to raised:\n        Cite fresh-count.\n        The count becomes the count * 7.\n        Return the count.\n    Done.\n    Bind number to untouched:\n        Cite fresh-count.\n        Return the count.\n    Done.\n    State cast raised on ().\n    State cast untouched on ().\n    State cast raised on ().\nDone.",
        "Pull a book on cufet.\n    Define cufet limits as [\n        Define the ceiling as 100 permanently.\n    ].\n    Cite limits.\n    Define cufet number capped, given (the number value), as [\n        If value is greater than the ceiling, return the ceiling.\n        Return value.\n    ].\n    Define cufet number capped-twice, given (the number value), as [\n        Return cast capped on (value * 2).\n    ].\n    State cast capped-twice on (30).\n    State cast capped-twice on (70).\nDone.",
        "Pull a book on cufet.\n    Bind number to outer, given (the number base):\n        Define cufet number inner, given (the number step), as [\n            Return step * 3.\n        ].\n        Return cast inner on (base) + 1.\n    Done.\n    State cast outer on (4).\nDone.",
        // C-language axioms: what crosses each way, addresses and their release, axioms as values.
        "Pull a book on the c-language.\n    Define c-language number answer as [6 * 7].\n    Define c-language number third as [\"abcdef\"[2]].\n    Define c-language number text-length, given (the text subject), as [strlen(the subject)].\n    Define c-language number compare, given (the text left, the text right), as [strcmp(the left, the right)].\n    Define c-language number scaled, given (the number value, the fact doubled), as [the doubled ? the value * 2 : the value].\n    State cast answer.\n    State cast third.\n    State cast text-length on (\"hello, world\").\n    State (cast compare on (\"apple\", \"banana\")) < 0.\n    State cast scaled on (21, true).\n    State cast scaled on (21, false).\n    State cast scaled on (-5, true).\nDone.",
        "Pull a book on the c-language.\n    Define c-language fact smaller as [1 < 2].\n    Define c-language voidable text greeting as [\"hello from C\"].\n    Define c-language voidable text nothing as [(char*)0].\n    Define c-language voidable number half as [0.5 * 3].\n    Define c-language voidable number broken as [0.0 / 0.0].\n    Define c-language number biggest as [(unsigned long long)-1].\n    Define c-language number lowest as [-9000000000LL].\n    State cast smaller.\n    State cast greeting but void is \"none\".\n    State cast nothing but void is \"none\".\n    State cast half but void is -1.\n    State cast broken but void is -1.\n    State cast biggest.\n    State cast lowest.\nDone.",
        "Pull a book on the c-language.\n    Define c-language number picked, given (the number read, the number read-only), as [/* the caller owns this */ The read * 100 + the read-only].\n    State cast picked on (3, 4).\n    Define c-language number spaced, given (the number value), as [the    value + 1].\n    State cast spaced on (9).\nDone.",
        "Pull a rabbit.\n    Pull a book on the c-language.\n        Define c-language number hand-back, given (the address held), as [(free(the held), 0)].\n        Define c-language voidable address copy-of, given (the text subject), as [strdup(the subject)], and free it with hand-back.\n        Define c-language voidable address nowhere as [(void*)0].\n        For each word in a series of text with (\"one\", \"two\", \"three\"), repeat:\n            Define held as cast copy-of on (word).\n            State (the text at held) but void is \"(nothing)\".\n        Done.\n        Define kept as cast copy-of on (\"kept\").\n        State kept.\n        State (the text at kept) but void is \"(nothing)\".\n        State (the text at cast nowhere) but void is \"(nothing)\".\n        State cast nowhere is void.\n    Done.\nDone.\nState \"after\".",
        "Pull a book on the c-language.\n    Define c-language number length-of, given (the text subject), as [(int)strlen(the subject)].\n    Define c-language number first-code, given (the text subject), as [(int)(the subject)[0]].\n    Define the measures as a series of c-language number axiom given (the text) with (length-of, first-code).\n    For each measure in the measures, repeat:\n        State cast measure on (\"abc\").\n    Done.\n    Bind number to twice-over, given (the c-language number axiom given (the text) job, the text subject):\n        Return (cast job on (subject)) * 2.\n    Done.\n    State cast twice-over on (length-of, \"abcd\").\n    State cast twice-over on (first-code, \"A\").\n    Define chosen as length-of.\n    State cast chosen on (\"hello\").\n    State chosen.\n    State the measures.\nDone.",
        "Pull a book on the c-language.\n    Define c-language helpers as [\n        static long long twice(long long value) { return 2 * value; }\n        static const char* word_for(long long n) { return n > 10 ? \"many\" : \"few\"; }\n    ].\n    Define c-language number doubled, given (the number value), as [twice(the value)].\n    Define c-language voidable text described, given (the number value), as [word_for(the value)].\n    State cast doubled on (21).\n    State cast described on (cast doubled on (3)) but void is \"?\".\n    State cast described on (cast doubled on (30)) but void is \"?\".\nDone.",
        "Pull a book on the c-language.\n    Bind number to said, given (the number n):\n        State \"working out {n}\".\n        Return n.\n    Done.\n    Define c-language number combined, given (the number left, the number right), as [the left * 10 + the right].\n    State cast combined on (cast said on (1), cast said on (2)).\nDone.",
        // Operands of texts, maps, sets and collection statements, worked out once each in the interpreter's order (2026-10-24)
        "Bind number to at, given (the series of number seen, the number made):\n    Insert 0 into seen.\n    State \"at {the number of seen} {made}\".\n    Return made.\nDone.\nBind text to word, given (the series of number seen, the text made):\n    Insert 0 into seen.\n    State \"word {the number of seen} {made}\".\n    Return made.\nDone.\nBind map from text to number to table, given (the series of number seen, the map from text to number held):\n    Insert 0 into seen.\n    State \"table {the number of seen}\".\n    Return held.\nDone.\nBind series of number to row, given (the series of number seen, the series of number held):\n    Insert 0 into seen.\n    State \"row {the number of seen}\".\n    Return held.\nDone.\nDefine c as a series of number.\n\nState \"-- texts\".\nIf cast word on (c, \"hello\") contains cast word on (c, \"ell\"), state \"contains\".\nIf cast word on (c, \"hello\") starts with cast word on (c, \"he\"), state \"starts\".\nIf cast word on (c, \"hello\") ends with cast word on (c, \"lo\"), state \"ends\".\nState the position of cast word on (c, \"l\") in cast word on (c, \"hello\").\nState cast word on (c, \"a,b\") split by cast word on (c, \",\").\nState replace cast word on (c, \"a\") with cast word on (c, \"X\") in cast word on (c, \"banana\").\nState the characters from cast at on (c, 2) to cast at on (c, 3) of cast word on (c, \"hello\").\nState the characters from cast at on (c, 4) to the end of cast word on (c, \"hello\").\nState the first (cast at on (c, 2)) characters of cast word on (c, \"hello\").\nState the last (cast at on (c, 2)) characters of cast word on (c, \"hello\").\n\nState \"-- maps\".\nDefine m as a map with (\"a\" : 1, \"b\" : 2).\nState the entry for cast word on (c, \"a\") in cast table on (c, m).\nState from cast table on (c, m), the entry for cast word on (c, \"b\").\nIf cast table on (c, m) has a key for cast word on (c, \"a\"), state \"key\".\nIf cast table on (c, m) has an entry for cast word on (c, \"a\"), state \"entry\".\nIn cast table on (c, m), the entry for cast word on (c, \"z\") becomes cast at on (c, 9).\nState m.\nRemove cast word on (c, \"z\") from cast table on (c, m).\nState m.\n\nState \"-- series\".\nDefine s as a series of number with (1, 2, 3).\nInsert cast at on (c, 7) into cast row on (c, s).\nInsert cast at on (c, 8) after item (cast at on (c, 1)) of cast row on (c, s).\nInsert cast at on (c, 6) into the start of cast row on (c, s).\nItem (cast at on (c, 2)) of cast row on (c, s) becomes cast at on (c, 5).\nState s.\nState item (cast at on (c, 3)) of cast row on (c, s).\nRemove item (cast at on (c, 1)) from cast row on (c, s).\nState s.\n\nState \"-- sets\".\nPull a book on collections.\n    Bind set of text to bag, given (the series of number seen, the set of text held):\n        Insert 0 into seen.\n        State \"bag {the number of seen}\".\n        Return held.\n    Done.\n    Define seen-words as a set of text with (\"a\").\n    If cast bag on (c, seen-words) has cast word on (c, \"a\"), state \"has\".\n    Insert cast word on (c, \"q\") into cast bag on (c, seen-words).\n    State seen-words.\nDone.\n",
        "Bind number to at, given (the series of number seen, the number made):\n    Insert 0 into seen.\n    State \"at {the number of seen} {made}\".\n    Return made.\nDone.\nBind text to word, given (the series of number seen, the text made):\n    Insert 0 into seen.\n    State \"word {the number of seen} {made}\".\n    Return made.\nDone.\nBind series of number to row, given (the series of number seen, the series of number held):\n    Insert 0 into seen.\n    State \"row {the number of seen}\".\n    Return held.\nDone.\nDefine c as a series of number.\n\nState \"-- one side a call\".\nDefine s as a series of number with (1, 2, 3, 2).\nState item 1 of cast row on (c, s).\nState the last of cast row on (c, s).\nItem 1 of cast row on (c, s) becomes 10.\nInsert 4 into cast row on (c, s).\nRemove 2 from cast row on (c, s).\nRemove item 1 from cast row on (c, s).\nState s.\n\nState \"-- remove a value\".\nRemove cast at on (c, 3) from cast row on (c, s).\nState s.\n\nPull a book on collections.\n    Bind chase to buffer, given (the series of number seen, the chase held):\n        Insert 0 into seen.\n        State \"buffer {the number of seen}\".\n        Return held.\n    Done.\n    Bind set of text to bag, given (the series of number seen, the set of text held):\n        Insert 0 into seen.\n        State \"bag {the number of seen}\".\n        Return held.\n    Done.\n    State \"-- chases\".\n    Define out as a chase.\n    Insert cast word on (c, \"he\") into cast buffer on (c, out).\n    Insert cast word on (c, \"!\") into the start of cast buffer on (c, out).\n    Insert cast word on (c, \"y\") after item (cast at on (c, 2)) of cast buffer on (c, out).\n    Insert cast at on (c, 72) into cast buffer on (c, out).\n    Insert cast at on (c, 73) after item (cast at on (c, 1)) of cast buffer on (c, out).\n    State out converted to text.\n    Item (cast at on (c, 1)) of cast buffer on (c, out) becomes cast word on (c, \"?\").\n    The last of cast buffer on (c, out) becomes cast word on (c, \"Z\").\n    State out converted to text.\n    State item (cast at on (c, 2)) of cast buffer on (c, out).\n    Remove item (cast at on (c, 1)) from cast buffer on (c, out).\n    Remove cast word on (c, \"y\") from cast buffer on (c, out).\n    Remove the last from cast buffer on (c, out).\n    State out converted to text.\n\n    State \"-- one side a call, sets\".\n    Define seen-words as a set of text with (\"a\").\n    If cast bag on (c, seen-words) has \"a\", state \"has\".\n    Insert \"b\" into cast bag on (c, seen-words).\n    State seen-words.\nDone.\n",
        "Bind number to at, given (the series of number seen, the number made):\n    Insert 0 into seen.\n    State \"at {the number of seen} {made}\".\n    Return made.\nDone.\nBind text to word, given (the series of number seen, the text made):\n    Insert 0 into seen.\n    State \"word {the number of seen} {made}\".\n    Return made.\nDone.\nBind number to add, given (the number x, the number y):\n    Return x * 10 + y.\nDone.\nBind number or failure to checked, given (the series of number seen, the number x):\n    Insert 0 into seen.\n    State \"checking {the number of seen} {x}\".\n    If x is greater than 5, return a failure \"too big\".\n    Return x.\nDone.\nDefine c as a series of number.\n\nState \"-- hoisting\".\nState cast add on (cast at on (c, 1), the position of cast word on (c, \"l\") in cast word on (c, \"hello\") but void is 0).\nState \"{cast at on (c, 4)} {item 2 of (cast word on (c, \"a,b\") split by \",\")}\".\n\nState \"-- in a try\".\nDefine m as a map with (\"a\" : 1).\nDefine s as a series of number with (1, 2, 3).\nTry to:\n    In m, the entry for cast word on (c, \"k\") becomes cast checked on (c, 2).\n    Item (cast checked on (c, 1)) of s becomes cast checked on (c, 4).\n    Insert cast checked on (c, 3) after item (cast checked on (c, 2)) of s.\n    State replace cast word on (c, \"1\") with cast word on (c, \"x\") in \"{cast checked on (c, 1)}1\".\n    Item (cast checked on (c, 1)) of s becomes cast checked on (c, 9).\n    State \"not reached\".\nDone.\nIn case of failure:\n    State \"failed: {the message of the failure}\".\nDone.\nState m.\nState s.\n",
        // A preliminary step runs where its expression is written, and only when it runs (2026-10-25)
        "Bind number to at, given (the series of number seen, the number made):\n    Insert 0 into seen.\n    State \"at {the number of seen} {made}\".\n    Return made.\nDone.\nBind voidable number to maybe, given (the series of number seen, the number made):\n    Insert 0 into seen.\n    State \"maybe {the number of seen} {made}\".\n    Return made.\nDone.\nBind number to add, given (the number x, the number y):\n    Return x * 10 + y.\nDone.\nBind number or failure to checked, given (the series of number seen, the number x):\n    Insert 0 into seen.\n    State \"checking {the number of seen} {x}\".\n    Return x.\nDone.\nDefine c as a series of number.\nState cast add on (cast at on (c, 1), cast maybe on (c, 2) but void is 0).\nState cast at on (c, 1) + (cast maybe on (c, 2) but void is 0).\nTry to:\n    State cast add on (cast at on (c, 1), cast checked on (c, 2)).\n    State cast at on (c, 1) + cast checked on (c, 2).\nDone.\nIn case of failure:\n    State \"failed\".\nDone.\n",
        "Bind number to at, given (the series of number seen, the number made):\n    Insert 0 into seen.\n    State \"at {the number of seen} {made}\".\n    Return made.\nDone.\nBind voidable number to maybe, given (the series of number seen, the number made):\n    Insert 0 into seen.\n    State \"maybe {the number of seen} {made}\".\n    Return made.\nDone.\nBind number or failure to checked, given (the series of number seen, the number x):\n    Insert 0 into seen.\n    State \"checking {the number of seen} {x}\".\n    Return x.\nDone.\nBind number to add, given (the number x, the number y):\n    Return x * 10 + y.\nDone.\nDefine c as a series of number.\n\nState \"-- lazy\".\nIf cast at on (c, 1) is 5 and (cast maybe on (c, 2) but void is 0) is 2, state \"both\".\nIf cast at on (c, 1) is 5 and (cast checked on (c, 2) but on failure 0) is 2, state \"both\".\nState the number of c.\n\nState \"-- literals\".\nState cast add on (cast at on (c, 1), item 1 of (a series of number with (cast at on (c, 2)))).\nState cast add on (cast at on (c, 1), the entry for \"a\" in (a map with (\"a\" : cast at on (c, 2))) but void is 0).\nState cast add on (cast at on (c, 1), cast checked on (c, 2) but on failure 0).\n",
        "Bind number to at, given (the series of number seen, the number made):\n    Insert 0 into seen.\n    State \"at {the number of seen} {made}\".\n    Return made.\nDone.\nBind voidable number to maybe, given (the series of number seen, the number made):\n    Insert 0 into seen.\n    State \"maybe {the number of seen} {made}\".\n    Return made.\nDone.\nBind text to word, given (the series of number seen, the text made):\n    Insert 0 into seen.\n    State \"word {the number of seen} {made}\".\n    Return made.\nDone.\nDefine c as a series of number.\n\nState \"-- otherwise if\".\nIf cast at on (c, 1) is 1, state \"first\".\nOtherwise if (cast maybe on (c, 2) but void is 0) is 2, state \"second\".\nIf cast at on (c, 1) is 2, state \"first\".\nOtherwise if (cast maybe on (c, 2) but void is 0) is 2, state \"second\".\n\nState \"-- until\".\nDefine n as 0.\nRepeat:\n    Increment n by 1.\nUntil (cast maybe on (c, n) but void is 0) is 3.\n\nState \"-- while\".\nDefine k as 0.\nWhile (cast maybe on (c, k) but void is 0) is less than 2, repeat:\n    Increment k by 1.\nDone.\n\nState \"-- set literal\".\nPull a book on collections.\n    Define seen-words as a set of text with (cast word on (c, \"a\"), cast word on (c, \"b\")).\n    State seen-words.\nDone.\n",
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

    /// <summary>Examples whose output is legitimately not reproducible — ExampleOracleTests'
    /// NonDeterministicSkips, for the same reasons: the two backends' random generators differ, and
    /// which worker takes which item is the scheduler's to decide.</summary>
    private static readonly HashSet<string> NotReproducible = ["markov.cufe", "work-queue.cufe"];

    /// <summary>How many programs of `examples/` must build and print alike. Raised as it grows; never lowered.</summary>
    private const int CorpusFloor = 62;

    /// <remarks>
    /// ★ The corpus is copied first: the compiler writes each program beside its source. Each program
    /// it builds runs in its own folder, and the interpreter runs the same file there, as `cufet` would.
    /// </remarks>
    [Fact]
    public void TheCompiledCompiler_BuildsCorpusProgramsThatPrintWhatTheInterpreterPrints()
    {
        var dir = Path.Combine(TestScratch.Root, "cufet-selfcorpus-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyFolder(Path.Combine(RepoRoot, "examples"), dir);
            var files = Directory.GetFiles(dir, "*.cufe", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("blueprint.cufe", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToList();
            Assert.True(files.Count >= 50, $"only {files.Count} programs in examples/");

            var runtime = compiled.RuntimeFolder.Replace('\\', '/');
            var got = ByFile(Run(compiled.Exe, ["--runtime", runtime, .. PreludeArgs, .. files], dir));

            int matched = 0;
            var problems = new List<string>();
            var unsupported = new List<string>();
            foreach (var file in files)
            {
                string name = Path.GetRelativePath(dir, file).Replace('\\', '/');
                string said = got.TryGetValue(file, out var lines) && lines.Count > 0 ? lines[0] : "<nothing printed>";
                if (said.StartsWith("unsupported\t", StringComparison.Ordinal))
                {
                    unsupported.Add($"{name}\t{said["unsupported\t".Length..]}");
                    continue;
                }
                var folder = Path.GetDirectoryName(file)!;
                // ★ A library is no program: neither backend runs one, and the interpreter must
                // refuse it too — exit 2, "nothing to run" — or the compiler has called a program one.
                if (said.StartsWith("library\t", StringComparison.Ordinal))
                {
                    if (RunIn(CufetExe, [file], folder).Exit != 2) problems.Add($"{name}: called a library, but the interpreter runs it");
                    continue;
                }
                if (said != "built") { problems.Add($"{name}: {said}"); continue; }
                var (want, wantExit) = RunIn(CufetExe, [file], folder);
                var (have, haveExit) = RunIn(Path.ChangeExtension(file, OperatingSystem.IsWindows() ? ".exe" : null), [], folder);
                // ★ The two whose output is the platform's to decide get ExampleOracleTests' weaker bar:
                // both run, and both run cleanly.
                if (NotReproducible.Contains(Path.GetFileName(file)))
                {
                    if (wantExit == 0 && haveExit == 0) matched++;
                    else problems.Add($"{name} did not run cleanly (exit {wantExit} interpreted, {haveExit} built)");
                    continue;
                }
                if (have == want && haveExit == wantExit) matched++;
                else problems.Add($"{name} printed differently (exit {wantExit} interpreted, {haveExit} built):\n    interpreted: {Show(want)}\n    built:       {Show(have)}");
            }

            output.WriteLine($"{matched} of {files.Count} corpus programs built and print alike; {unsupported.Count} not yet:");
            foreach (var line in unsupported) output.WriteLine("    " + line);
            Assert.True(problems.Count == 0,
                $"{problems.Count} of {files.Count} went wrong:\n{string.Join("\n", problems.Take(20))}");
            Assert.True(matched >= CorpusFloor, $"only {matched} corpus programs built and print alike, and the floor is {CorpusFloor}.");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>A program's output and exit code, run in a folder with nothing on its input.</summary>
    private static (string Output, int Exit) RunIn(string exe, IEnumerable<string> arguments, string folder)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false, WorkingDirectory = folder,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProgramTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            throw new Xunit.Sdk.XunitException(
                $"{Path.GetFileName(exe)} did not finish within {ProgramTimeoutMs / 1000} s, and was stopped.");
        }
        _ = stderr.Result;
        return (stdout.Result, process.ExitCode);
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>
    /// Programs the interpreter stops with an error, each with words the error must carry.
    /// </summary>
    /// <remarks>
    /// ⚠ Held to the interpreter, not to the C# compiler: until 2026-10-08 that one did not stop these
    /// — its loop ran over the items there when it began, whatever the body did — and the oracle
    /// could not see it, because it does not run programs that end in an error. This test found it;
    /// `PipelineSeriesTests.ALoopThatChangesWhatItLoopsOver_StopsAsTheInterpreterDoes` holds it now.
    /// </remarks>
    private static readonly (string Source, string Says)[] Stopped =
    [
        ("Define xs as a series of number with (1, 2, 3).\nFor each in xs, repeat:\n    State it.\n    Insert it into xs.\nDone.\nState xs.",
         "was modified during a for-each loop"),
        ("Define xs as a series of number with (1, 2, 3).\nFor each n in xs, repeat:\n    State n.\n    If n is 2, remove the first from xs.\nDone.\nState xs.",
         "was modified during a for-each loop"),
        ("Define xs as a series of number with (1, 2, 3).\nFor each n in xs, repeat:\n    State n.\n    If n is 3, insert 4 into xs.\nDone.\nState xs.",
         "was modified during a for-each loop"),
        ("Define counts as a map with (\"a\": 1, \"b\": 2).\nFor each pair in counts, repeat:\n    State the key of pair.\n    In counts, the entry for \"z\" becomes 0.\nDone.\nState counts.",
         "'counts' was modified during a for-each loop"),
        ("Define ages as a map with (\"a\": 1).\nState ages.\nRemove \"zzz\" from ages.\nState \"not reached\".",
         "Key not found in map on line 3."),
        ("Pull a book on collections.\n    Define out as a chase.\n    Insert \"ab\" into out.\n    The item 1 of out becomes \"xy\".\n    State \"not reached\".\nDone.",
         "Setting one position needs exactly one character"),
        ("Pull a book on collections.\n    Define out as a chase.\n    Insert \"ab\" into out.\n    State item 5 of out.\nDone.",
         "'out' has 2 items"),
        ("Pull a book on collections.\n    Define out as a chase.\n    Insert \"ab\" into out.\n    For each letter in out, repeat:\n        Insert \"c\" into out.\n    Done.\nDone.",
         "'out' was modified during a for-each loop"),
        ("Try to:\n    State 1 / 0.\nDone.\nIn case of exception:\n    State \"handled, and raised again\".\nDone.\nState \"not reached\".",
         "Division by zero"),
        ("Try to:\n    Define xs as a series of number.\n    State the last of xs.\nDone.\nIn case of exception:\n    State \"seen\".\nDone.",
         "'xs' is empty"),
        // A Try left early, then a fault with no Try at all: the program stops, rather than jumping
        // into the frame of a Try that is gone.
        ("Bind number to early, given (the number n):\n    Try to:\n        If n is 1, return 5.\n    Done.\n    In case of exception:\n        Suppress the exception.\n    Done.\n    Return 0.\nDone.\nState cast early on (1).\nState 1 / 0.\nState \"not reached\".",
         "Division by zero"),
        // Bits that cannot be: a width too narrow for the value, a division by zero, a shift past 64 bits.
        ("Define n as 1.\nState \"before\".\nState 0xFF at n bits.",
         "1 bits cannot hold this value"),
        ("Define zero as 0b0.\nState \"before\".\nState 0b1010 / zero.",
         "Division by zero on line 3."),
        ("Define zero as 0b0.\nState 0b1010 % zero.",
         "Modulo by zero on line 2."),
        ("State \"before\".\nState 0b1 shifted left by 70.",
         "does not fit in 64 bits"),
        // A set changed while looped over; a file that cannot be opened, with no Try to catch it.
        ("Define seen as a set of text with (\"a\", \"b\").\nFor each s in seen, repeat:\n    State s.\n    Insert s joined to \"!\" into seen.\nDone.",
         "was modified during a for-each loop"),
        ("State \"before\".\nWith the file \"no-such-file.txt\" open for reading as src:\n    State read all from src.\nDone.\nState \"never\".",
         "the file 'no-such-file.txt' was not found"),
        // A task that faults with nobody awaiting it: its rabbit raises it at its Done.
        ("State \"before\".\nPull a rabbit.\n    Define zero as 0.\n    Have rabbit start a task:\n        State 10 / zero.\n    Done.\nDone.\nState \"never\".",
         "Division by zero on line 5."),
        // A matrix read or written outside its sizes, or made with a size that is no count.
        ("Pull a book on collections.\n    Define m as a matrix with ((1, 2), (3, 4)).\n    State \"before\".\n    State item at (3, 1) of m.\nDone.",
         "Row index 3 is out of range — this matrix has 2 row(s) (line 4)."),
        ("Pull a book on collections.\n    Define m as a matrix with ((1, 2), (3, 4)).\n    The item at (1, 0) of m becomes 1.\nDone.",
         "Column index 0 is out of range — this matrix has 2 column(s) (line 3)."),
        ("Pull a book on collections.\n    Define h as 1.5.\n    State a matrix with h by 2.\nDone.",
         "Matrix row count must be a positive whole number, but got 1.5 (line 3)."),
        ("Pull a book on collections.\n    Define w as 0.\n    State a matrix with 2 by w filled with 1.\nDone.",
         "Matrix column count must be a positive whole number, but got 0 (line 3)."),
        // A fault that ends the program still unmakes what the blocks it leaves hold.
        ("Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nDefine zero as 0.\nIf true:\n    Define doomed as a new res { the name \"doomed\" }.\n    State \"before\".\n    State 1 / zero.\nDone.",
         "Division by zero on line 9."),
        ("Define object res with (the text name).\nBind unmaking a res to closing:\n    State \"closed {one's name}\".\nDone.\nBind void to opens:\n    Define inside as a new res { the name \"function body\" }.\nDone.\nDefine top as a new res { the name \"top\" }.\nCast opens.\nDefine zero as 0.\nState \"before\".\nState 1 / zero.",
         "Division by zero on line 12."),
        // A random number whose low is above its high.
        ("Pull a book on chance.\n    Define low as 9.\n    State \"before\".\n    State a random number from low to 3.\nDone.",
         "Random number range is invalid: low (9) is greater than high (3) (line 4)."),
        // A number handed to C that is not a whole one.
        ("Pull a book on the c-language.\n    Define c-language number plus-one, given (the number value), as [the value + 1].\n    State cast plus-one on (2).\n    State cast plus-one on (2.5).\nDone.",
         "Foreign source takes whole numbers, but got 2.5. This happened on line 4."),
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
                if (!process.WaitForExit(ProgramTimeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    process.WaitForExit();
                    problems.Add($"{name} did not finish within {ProgramTimeoutMs / 1000} s, and was stopped");
                    continue;
                }
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

    /// <summary>
    /// The compiler written in Cufet builds the lexer written in Cufet — the first of the four to
    /// come through — and what it builds lexes every Cufet file in the repository as C# does.
    /// </summary>
    /// <remarks>
    /// ★ The first self-hosting step, 2026-10-11: Cufet compiling a program of its own toolchain.
    /// Held to the C# lexer, the same key `TheCompiledLexer_AgreesWithCSharpToo` holds the lexer
    /// `cufet build` makes to — so a difference here is this compiler's, not the lexer's.
    /// </remarks>
    [Fact]
    public void TheCompiledCompiler_BuildsTheLexer_WhichLexesAsCSharpDoes()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(TestScratch.Root, "cufet-selflexer-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string exe = BuiltByTheCufetCompiler("lexer", dir);
            var files = Corpus();
            Assert.True(files.Count >= 80, $"only {files.Count} Cufet files found under {RepoRoot}");
            var got = ByFile(Run(exe, files, RepoRoot));
            var problems = Differences(WithSources(files), got);
            Assert.True(problems.Count == 0,
                $"{problems.Count} of {files.Count} files lex differently:\n{string.Join("\n", problems)}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>
    /// The compiler written in Cufet builds the parser written in Cufet, and what it builds parses
    /// every Cufet file in the repository into the tree C#'s parser builds.
    /// </summary>
    /// <remarks>
    /// ★ The second of the four, 2026-10-12. Held to <c>Parser.cs</c> through the same printed tree
    /// `TheCompiledParser_BuildsTheSameTreeAsCSharp_ForEveryFile` holds `cufet build`'s parser to.
    /// </remarks>
    [Fact]
    public void TheCompiledCompiler_BuildsTheParser_WhichParsesAsCSharpDoes()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(TestScratch.Root, "cufet-selfparser-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string exe = BuiltByTheCufetCompiler("parser", dir);
            var files = Corpus();
            Assert.True(files.Count >= 80, $"only {files.Count} Cufet files found under {RepoRoot}");
            var (parsed, problems, unsupported) = ParserParityTests.Compare(ByFile(Run(exe, files, RepoRoot)), WithSources(files));
            Assert.True(problems.Count == 0,
                $"{problems.Count} of {files.Count} files parse differently:\n{string.Join("\n", problems.Take(20))}");
            Assert.True(parsed >= 100, $"only {parsed} files parse alike. Not yet:\n{string.Join("\n", unsupported)}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>
    /// The compiler written in Cufet builds the checker written in Cufet, and what it builds checks
    /// every example program as `TypeChecker` does — the same verdict, and for each it accepts, the
    /// same checked tree.
    /// </summary>
    /// <remarks>
    /// ★ 2026-10-13, the day regions began freeing: until then this checker ran out of memory over a
    /// long list of files (16 GB, MEASURED), because a `Pull a rabbit` around each file freed nothing.
    /// ⚠ The examples only, not the large `self-hosting/` folders — over all 141 files the run takes
    /// minutes. Measured by hand that day: identical to `cufet build`'s checker on all 141.
    /// </remarks>
    /// <summary>One large file for the memory ceiling: checking it loads its whole folder.</summary>
    private const string HeavyFile = "self-hosting/Cufet/compiler/emitting.cufe";

    /// <summary>The most private memory the checker the Cufet compiler builds may hold over <see cref="HeavyFile"/>.</summary>
    /// <remarks>
    /// ★ Measured 2026-10-24 on Windows: the checker `cufet build` builds peaked at 2,086 MB over this
    /// file, and the one the Cufet compiler builds at 2,288 MB. The ceiling is 1.5 times the first.
    /// Raise it, with a new measurement of both, when the file grows — never to make a red test green.
    /// ⚠ The test this exists for: the same checker once peaked at 4,799 MB here, from one lowering that
    /// made a throwaway chase on every insert at a chase's end. Every parity test stayed green, because
    /// they compare only what the checker prints.
    /// </remarks>
    private const long HeavyFileMemoryCeilingBytes = 3_100L * 1024 * 1024;

    [Fact]
    public void TheCompiledCompiler_BuildsTheChecker_WhichChecksAsCSharpDoes()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(TestScratch.Root, "cufet-selfchecker-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string exe = BuiltByTheCufetCompiler("checker", dir);
            var files = Corpus().Where(f => f.StartsWith("examples/", StringComparison.Ordinal)
                                         && !f.EndsWith("/blueprint.cufe", StringComparison.Ordinal)).ToList();
            Assert.True(files.Count >= 60, $"only {files.Count} example programs found under {RepoRoot}");
            var got = ByFile(Run(exe, ["--tree", .. PreludeArgs, .. files], RepoRoot));

            // And how much memory it takes over one large file — see HeavyFileMemoryCeilingBytes.
            Run(exe, ["--tree", .. PreludeArgs, HeavyFile], RepoRoot, out long peak);
            Assert.True(peak > 0, "the checker's memory was never measured, so this would pass on nothing.");
            Assert.True(peak <= HeavyFileMemoryCeilingBytes,
                $"the checker the Cufet compiler builds held {peak / (1024 * 1024):N0} MB at its peak over {HeavyFile}; "
              + $"the ceiling is {HeavyFileMemoryCeilingBytes / (1024 * 1024):N0} MB. See HeavyFileMemoryCeilingBytes.");
            var problems = new List<string>();
            int trees = 0;
            foreach (var name in files)
            {
                var at = Path.Combine(RepoRoot, name);
                string want = CheckerAnswer.ExpectedAt(at);
                string have = got.TryGetValue(name, out var lines) && lines.Count > 0 ? lines[0] : "<nothing printed>";
                if (have != want) { problems.Add($"{name}:\n    C#:    {want}\n    Cufet: {have}"); continue; }
                if (want != "ok") continue;
                string wantTree = CheckerAnswer.CheckedTreeAt(at)!;
                string haveTree = string.Join("\n", lines!.Skip(1).Where(l => !l.StartsWith("warning\t", StringComparison.Ordinal)));
                if (haveTree != wantTree) problems.Add($"{name}: the checked tree differs");
                else trees++;
            }
            Assert.True(problems.Count == 0,
                $"{problems.Count} of {files.Count} are checked differently:\n{string.Join("\n", problems.Take(10))}");
            Assert.True(trees >= 50, $"only {trees} checked trees matched");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>
    /// The compiler written in Cufet compiles ITSELF, and the compiler that makes compiles it again
    /// into the very same C — the fixed point a self-hosting compiler is held to.
    /// </summary>
    /// <remarks>
    /// ★★ 2026-10-13. Stage 1 is the compiler `CompiledCufetCompiler` builds with `cufet build`;
    /// stage 2 is what stage 1 makes of `compiler.cufe`; stage 2 then compiles `compiler.cufe`
    /// again. The C stage 2 writes must be byte for byte the C stage 1 wrote: a compiler that built
    /// itself subtly wrong would write different C, or none.
    /// </remarks>
    [Fact]
    public void TheCompiledCompiler_CompilesItself_ToAFixedPoint()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(TestScratch.Root, "cufet-selfcompiler-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string stageOneDir = Directory.CreateDirectory(Path.Combine(root, "one")).FullName;
            string stageTwo = BuiltByTheCufetCompiler("compiler", stageOneDir);
            string stageOneC = Path.Combine(stageOneDir, "Cufet", "compiler", "compiler.c");

            string stageTwoDir = Directory.CreateDirectory(Path.Combine(root, "two")).FullName;
            foreach (var f in Directory.GetFiles(SelfHostingDir, "*.cufe", SearchOption.AllDirectories))
            {
                var to = Path.Combine(stageTwoDir, Path.GetRelativePath(SelfHostingDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            var runtime = compiled.RuntimeFolder.Replace('\\', '/');
            string source = "Cufet/compiler/compiler.cufe";
            var said = ByFile(Run(stageTwo, ["--runtime", runtime, .. PreludeArgs, source], stageTwoDir));
            string verdict = said.TryGetValue(source, out var lines) && lines.Count > 0 ? lines[0] : "<nothing printed>";
            Assert.True(verdict == "built", $"the self-built compiler did not build itself: {verdict}");

            string stageTwoC = Path.Combine(stageTwoDir, "Cufet", "compiler", "compiler.c");
            string one = File.ReadAllText(stageOneC), two = File.ReadAllText(stageTwoC);
            if (one != two)
            {
                int at = 0;
                while (at < one.Length && at < two.Length && one[at] == two[at]) at++;
                int line = one[..at].Count(c => c == '\n') + 1;
                Assert.Fail($"the self-built compiler writes different C for itself, first at line {line}:\n"
                    + $"    stage 1: {Show(one[at..Math.Min(one.Length, at + 120)])}\n"
                    + $"    stage 2: {Show(two[at..Math.Min(two.Length, at + 120)])}");
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    /// <summary>
    /// A copy of `self-hosting/` with one of its programs — `lexer`, `parser`, … — built by the
    /// compiler written in Cufet; the program's path. Fails the test if it does not build.
    /// </summary>
    private string BuiltByTheCufetCompiler(string entry, string dir)
    {
        foreach (var f in Directory.GetFiles(SelfHostingDir, "*.cufe", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dir, Path.GetRelativePath(SelfHostingDir, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        var runtime = compiled.RuntimeFolder.Replace('\\', '/');
        string source = $"Cufet/{entry}/{entry}.cufe";
        var said = ByFile(Run(compiled.Exe, ["--runtime", runtime, .. PreludeArgs, source], dir));
        string verdict = said.TryGetValue(source, out var lines) && lines.Count > 0 ? lines[0] : "<nothing printed>";
        Assert.True(verdict == "built", $"the {entry} did not build: {verdict}");
        return Path.Combine(dir, "Cufet", entry, entry + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }

    /// <summary>Output with its line breaks and tabs shown, so a difference in either is visible.</summary>
    private static string Show(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
