using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// The two first mistakes with an object's members, both found by writing the tutorial's lesson on
// objects.
public class ObjectMemberErrorTests
{
    private static TypeException CheckFails(string source)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        return Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
    }

    private const string Bed =
        "Define object bed with (the number width, the number depth):\n"
      + "    Bind number to area:\n"
      + "        Return one's width * one's depth.\n"
      + "    Done.\n"
      + "Done.\n"
      + "Define front as a new bed { the width 4, the depth 5 }.\n";

    [Fact]
    public void ABareFieldInsideAMethod_SaysToGoThroughOne()
    {
        // ⚠ It said "'width' isn't defined … declare it: 'Define object width with (...) and module:'"
        // — advice to make a module of a field name.
        var ex = CheckFails(
            "Define object bed with (the number width, the number depth):\n"
          + "    Bind number to area:\n"
          + "        Return width * depth.\n"
          + "    Done.\n"
          + "Done.");
        Assert.Contains("'width' is a field of 'bed'", ex.Message);
        Assert.Contains("Write 'one's width'", ex.Message);
        Assert.DoesNotContain("and module", ex.Message);
    }

    [Theory]
    [InlineData("State front's area.")]
    [InlineData("Define measure as front's area.")]
    public void AMethodNamedWithoutACall_IsRefusedBeforeItRuns(string line)
    {
        // ⚠⚠ This CHECKED CLEAN. The interpreter then died looking for a field named `area`, and the
        // compiler refused with "'bed' has no member 'area'". A method is reached only by calling it.
        var ex = CheckFails(Bed + line);
        Assert.Contains("'area' is a method of 'bed', and a method is used by calling it", ex.Message);
        Assert.Contains("'cast front's area'", ex.Message);
    }

    [Fact]
    public void CallingAMethod_StillWorks()
    {
        var program = new Parser(new CufetLexer(Bed + "State cast front's area.").Tokenize()).Parse();
        program = new TypeChecker().Check(program);
        var output = new StringWriter();
        new Interpreter(output).Execute(program);
        Assert.Equal("20", output.ToString().Trim());
    }

    private const string Charge =
        "Bind number to halve, given (the number price): Return price / 2. Done.
"
      + "Define object charge with (the text label, the number function apply given (the number)).
"
      + "Define c as a new charge { the label \"half\", the apply halve }.
";

    [Fact]
    public void AFieldHoldingAFunction_CalledAsAMethod_IsRefusedBeforeItRuns()
    {
        // ⚠⚠ `cast c's apply on (10)` CHECKED CLEAN. `'s` in a cast names a method, so the interpreter
        // then died looking for one and `cufet build` refused it — the checker was the only one of
        // the three to let it through.
        var ex = CheckFails(Charge + "State cast c's apply on (10).");
        Assert.Contains("'apply' is a field of 'charge' that holds a function, not a method", ex.Message);
        Assert.Contains("'cast the apply of c on (…)'", ex.Message);
    }

    [Fact]
    public void AFieldHoldingAFunction_ReadThenCalled_StillWorks()
    {
        var program = new Parser(new CufetLexer(Charge + "State cast the apply of c on (10).").Tokenize()).Parse();
        program = new TypeChecker().Check(program);
        var output = new StringWriter();
        new Interpreter(output).Execute(program);
        Assert.Equal("5", output.ToString().Trim());
    }
}
