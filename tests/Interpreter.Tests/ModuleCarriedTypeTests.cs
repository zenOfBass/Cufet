using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A module may hold TYPE declarations as well as members, and that is the only way a type crosses
/// a file boundary.
/// </summary>
/// <remarks>
/// <para>
/// ★★ **Members are what an object HAS; declarations are what a module CARRIES.** A type is not
/// reached with <c>'s</c> and no interface can require one, so it is not a member — which is why
/// only a module may hold one, and why an object body otherwise takes nothing but methods.
/// </para>
/// <para>
/// ★ **The surface came from the language, not from a design session.** <c>matrix</c> already
/// arrived bare inside <c>Pull a book on collections.</c> and was refused outside it, so a carried
/// type reads the same way and needed no new spelling. <c>RegisterScopedType</c> had exactly one
/// entry until now.
/// </para>
/// <para>
/// ⚠ A carried type is lifted to the top level under a name with a space in it, so the tests below
/// that check OUTPUT are also checking that the lift never shows — a reader must see the name they
/// wrote. Both backends leaked the lifted name identically before this shipped, which is precisely
/// the kind of fault the oracle cannot see.
/// </para>
/// </remarks>
public class ModuleCarriedTypeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cufet-carried-" + Guid.NewGuid().ToString("n"));

    public ModuleCarriedTypeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SourceMap.Current = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Write(string name, string body) =>
        File.WriteAllText(Path.Combine(_dir, name + ".cufe"), body);

    private string Run(string source)
    {
        var checker = new TypeChecker { SourceDirectory = _dir };
        SourceMap.Current = checker.Sources;
        var program = checker.Check(new Parser(new CufetLexer(source).Tokenize()).Parse());
        var output = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => Run(source));

    private const string Shapes = """
        Define object shapes with () and module:
            Define object point with (the number across, the number up):
                Bind number to sum:
                    Return one's across + one's up.
                Done.
            Done.

            Bind point to origin:
                Return a new point { the across 0, the up 0 }.
            Done.

            Bind number to total, given (the point which):
                Return cast which's sum.
            Done.
        Done.

        """;

    // ── What carrying a type buys ──────────────────────────────────────────

    [Fact]
    public void APulledModulesTypeIsInScopeByItsShortName()
    {
        Assert.Equal("7", Run(Shapes + """
            Pull a shapes.
                Define here as a new point { the across 3, the up 4 }.
                State cast here's sum.
            Done.
            """));
    }

    [Fact]
    public void AValueOfACarriedTypeGoesBackIntoTheModule()
    {
        // The caller HOLDS one and hands it back — which is the whole point. A module that could
        // only make them and never take one would need no visible type at all.
        Assert.Equal("7", Run(Shapes + """
            Pull a shapes.
                Define here as a new point { the across 3, the up 4 }.
                State cast shapes's total on (here).
            Done.
            """));
    }

    [Fact]
    public void TheCarriedTypeIsGoneAfterDone()
    {
        // A pull is a scope, and the type it introduces leaves with it — the same rule `matrix`
        // has followed since collections shipped.
        var ex = Refused(Shapes + """
            Pull a shapes.
                State cast shapes's origin.
            Done.
            Define there as a new point { the across 1, the up 1 }.
            """);
        Assert.Contains("point", ex.Message);
    }

    [Fact]
    public void ACarriedTypeAnnotatesASeries()
    {
        // ⚠ REGRESSION. The annotation was taken as WRITTEN while the elements resolved to the
        // lifted name, so a series of exactly the right thing was refused: "you said this is a
        // series of points … you're trying to put a point in shapes item in it."
        Assert.Equal("2", Run(Shapes + """
            Pull a shapes.
                Define corners as a series of point with (
                    a new point { the across 1, the up 2 },
                    a new point { the across 3, the up 4 }).
                State the number of corners.
            Done.
            """));
    }

    [Fact]
    public void TheLiftedNameNeverShows()
    {
        // ⚠⚠ A carried type is lifted to `point in shapes`, and printing one used to say so. Both
        // backends leaked the same name and therefore AGREED, so no oracle comparison could fail —
        // the reader was the only thing that could catch it.
        Assert.Equal("point(across: 0, up: 0)", Run(Shapes + """
            Pull a shapes.
                State cast shapes's origin.
            Done.
            """));
    }

    [Fact]
    public void TwoModulesMayEachCarryTheSameName()
    {
        // Falls out of the lift rather than being arranged: the module's own name is in the lifted
        // one, so there is nothing to collide.
        Assert.Equal("flat\nround", Run("""
            Define object plane with () and module:
                Define object shape with ():
                    Bind text to name: Return "flat". Done.
                Done.
                Bind text to describe: Return cast (a new shape { })'s name. Done.
            Done.

            Define object globe with () and module:
                Define object shape with ():
                    Bind text to name: Return "round". Done.
                Done.
                Bind text to describe: Return cast (a new shape { })'s name. Done.
            Done.

            Pull a plane.
                State cast plane's describe.
            Done.
            Pull a globe.
                State cast globe's describe.
            Done.
            """));
    }

    // ── Only a module may carry one ────────────────────────────────────────

    [Fact]
    public void AnOrdinaryObjectCannotCarryAType()
    {
        // The claim is what earns it. An object body takes members; carrying declarations is the
        // thing `module` now means, rather than being a marker that asks for nothing.
        var ex = Assert.Throws<ParseException>(() => Run("""
            Define object plain-thing with ():
                Define object inner with ():
                    Bind text to name: Return "no". Done.
                Done.
            Done.
            """));
        Assert.Contains("only a module may declare a type inside its body", ex.Message);
        Assert.Contains("and module", ex.Message);   // the message names the fix
    }

    // ── Across a file boundary, which is the reason it exists ──────────────

    [Fact]
    public void ATypeCrossesAFileBoundary()
    {
        // ★ The case nothing could do before: a file's top level is private, and an object body
        // took only methods, so a type declared in one file was unreachable from any other.
        Write("shapes", Shapes);
        Assert.Equal("7", Run("""
            Pull a shapes.
                Define here as a new point { the across 3, the up 4 }.
                State cast here's sum.
            Done.
            """));
    }

    [Fact]
    public void AFilesOwnTypesStillDoNotCross()
    {
        // The privacy rule is unchanged: what a loaded file declares BESIDE its module is its own.
        // Carrying is an opt-in, not a hole.
        Write("kit", """
            Define object tally with (the number count):
                Bind number to doubled: Return one's count * 2. Done.
            Done.

            Define object kit with () and module:
                Bind number to five: Return 5. Done.
            Done.
            """);
        var ex = Refused("""
            Pull a kit.
                Define counted as a new tally { the count 2 }.
                State cast counted's doubled.
            Done.
            """);
        Assert.Contains("tally", ex.Message);
    }
}
