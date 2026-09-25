using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// A voidable value used where only one that is THERE will do.
//
// ⚠⚠ Five places refused this, each in its own words, and none said "void". Arithmetic told a
// writer who believed `carrots` WAS a number that "arithmetic requires numbers on both sides";
// joining advised `converted to text`, which then refused as well — following the fix led to a
// second refusal. Found by trying to write lesson 2 of the tutorial, which is about exactly this.
//
// ★ Each case pins the new words AND that the advice it gives runs — a refusal whose fix does not
// work is the defect this file exists for.
public class VoidableMisuseTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        program = new TypeChecker().Check(program);
        var output  = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private const string Carrots =
        "Define typed as \"12\".\nDefine carrots as typed converted to number.\n";

    [Theory]
    [InlineData("State carrots + 1.",
                "'carrots' might be void, and + needs a number that is there",
                "'(carrots but void is 0)' in its place.\nKeep the brackets")]
    [InlineData("If carrots is greater than 3, state \"lots\".",
                "'carrots' might be void, and ordering needs a number that is there",
                "'(carrots but void is 0)' in its place.\nKeep the brackets")]
    [InlineData("Define total as 0.\nThe total becomes carrots.",
                "'carrots' might be void, and 'total' holds numbers that are there",
                "'The total becomes carrots but void is 0'")]
    [InlineData("State \"Hopper has {carrots} carrots.\".",
                "'carrots' might be void, and void has no text to convert to",
                "'{carrots but void is 0}'")]
    [InlineData("State carrots converted to text.",
                "'carrots' might be void, and void has no text to convert to",
                "'(carrots but void is 0) converted to text'")]
    [InlineData("State \"Hopper has \" joined to carrots.",
                "'carrots' might be void, and only text that is there can be joined",
                "'(carrots but void is 0) converted to text'")]
    public void AVoidableWhereOnlyAPresentValueWillDo_SaysVoid(string line, string rule, string fix)
    {
        var ex = Assert.Throws<TypeException>(() => Run(Carrots + line));
        Assert.Contains(rule, ex.Message);
        Assert.Contains(fix, ex.Message);
        Assert.Contains("\nOr check first, with 'If carrots is not void:'", ex.Message);
    }

    [Fact]
    public void EveryFixTheRefusalsGive_Runs()
    {
        Assert.Equal("13\n1\n12\nHopper has 12 carrots.\nHopper has 12\n13", Run(Carrots + """
            State (carrots but void is 0) + 1.
            If (carrots but void is 0) is greater than 3, state 1.
            Define total as 0.
            The total becomes carrots but void is 0.
            State total.
            State "Hopper has {carrots but void is 0} carrots.".
            State "Hopper has " joined to (carrots but void is 0) converted to text.
            If carrots is not void, state carrots + 1.
            """));
    }

    [Fact]
    public void WithoutTheBrackets_TheDefaultTakesTheRestOfTheLine()
    {
        // ★ Why the arithmetic advice insists on brackets — pinned, so that if the precedence ever
        // changes, the advice gets revisited rather than left over-cautious.
        Assert.Equal("12\n13", Run(Carrots + """
            State carrots but void is 0 + 1.
            State (carrots but void is 0) + 1.
            """));
    }

    [Fact]
    public void AnExpression_IsNotOfferedTheIfCheck()
    {
        // Narrowing reaches variables only, so `If <expression> is not void:` would not help.
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define typed as \"12\".\nState (typed converted to number) + 1."));
        Assert.Contains("but void is 0", ex.Message);
        Assert.DoesNotContain("If ", ex.Message);
    }

    [Fact]
    public void ARealMismatch_KeepsItsOwnWords()
    {
        // Only a voidable whose PRESENT value would fit gets the void refusal.
        var ex = Assert.Throws<TypeException>(() => Run("State \"a\" + 1."));
        Assert.Contains("arithmetic requires numbers on both sides", ex.Message);
    }
}
