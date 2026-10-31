using Xunit;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// `is alphabetic`, `is numeric`, `is whitespace` and `the code point of`. Like casing, the classes
/// are one table both backends read (CharacterClassData, from tools/gen-character-classes.cs), so
/// the backends agree by construction; what these hold is that each class answers as Unicode says,
/// including above the Basic Multilingual Plane, where the two backends' encodings differ in shape.
public class PipelineCharacterClassTests : PipelineTestBase
{
    [Fact]
    public void EachClass_AnswersForEveryCharacter()
    {
        const string src = """
            For each word in a series with ("abc", "héllo", "中文", "abc1", "", "123", "٣٤", "½", " ", "a b"), repeat:
                State "{word}: {word is alphabetic} {word is numeric} {word is whitespace}".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("abc: true false false\nhéllo: true false false\n中文: true false false\n"
                   + "abc1: false false false\n: false false false\n123: false true false\n"
                   + "٣٤: false true false\n½: false false false\n : false false true\na b: false false false",
            Interpret(src));
    }

    [Fact]
    public void IsNot_AndAsACondition()
    {
        const string src = """
            Define digit as "7".
            If digit is numeric and digit is not alphabetic, state "a digit".
            Define gap as "\t\n" is not whitespace.
            State gap.
            While digit is not whitespace, repeat:
                The digit becomes " ".
            Done.
            State "[{digit}]".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("a digit\nfalse\n[ ]", Interpret(src));
    }

    [Fact]
    public void TheCodePoint_IsTheCharactersNumber()
    {
        // 😀 is a surrogate pair interpreted and four bytes compiled — one character either way.
        const string src = """
            State the code point of "A".
            State the code point of "é".
            State the code point of "中".
            State the code point of "😀".
            State (the code point of "7") - (the code point of "0").
            Define code as 1.
            Define point as 2.
            State code + point.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("65\n233\n20013\n128512\n7\n3", Interpret(src));
    }

    [Fact]
    public void TheCodePoint_OfMoreThanOneCharacter_StopsTheProgram()
    {
        AssertFaultOracle("State \"before\".\nState the code point of \"ab\".\nState \"after\".");
        AssertFaultOracle("State \"before\".\nState the code point of \"\".\nState \"after\".");
    }
}
