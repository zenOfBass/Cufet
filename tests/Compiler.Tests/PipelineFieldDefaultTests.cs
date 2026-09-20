using Cufet.Interpreter;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `the number age with default 0` — a field a construction site may leave out.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE INVARIANT IS KEPT, NOT LOOSENED. Every field is still set on every object — an object
/// has no unset state. What changed is only who wrote the value down. The checker fills the
/// default onto the LITERAL (`ObjectLiteral.FilledDefaults`), so neither backend learns that
/// defaults exist: each appends that list and constructs exactly as it did before.
/// </para>
/// <para>
/// ⚠ The default is an EXPRESSION filled in at each construction site, never a value computed
/// once at the definition. `EachObjectGetsItsOwnDefault` is the guard on that — it is the
/// mutable-default-argument bug every language with this feature has had to answer for.
/// </para>
/// </remarks>
public class PipelineFieldDefaultTests : PipelineTestBase
{
    [Fact]
    public void AnOmittedFieldTakesItsDefault_AndAGivenOneWins()
    {
        const string src = """
            Define object person with (
                the text name,
                the number age with default 0,
                the text city with default "nowhere").

            Define ada as a new person { the name "Ada", the age 36, the city "London" }.
            Define bo  as a new person { the name "Bo" }.
            State "{ada's name}, {ada's age}, {ada's city}".
            State "{bo's name}, {bo's age}, {bo's city}".
            """;
        Assert.Equal("Ada, 36, London\nBo, 0, nowhere\n".ReplaceLineEndings(),
                     InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void EachObjectGetsItsOwnDefault()
    {
        // ⚠⚠ THE TRAP. If the default were evaluated once at the definition, both baskets would
        // share one series and this would print 1 / 1. Filling the EXPRESSION at each
        // construction site is what makes the answer 1 / 0 — by construction, not by a rule.
        const string src = """
            Define object basket with (
                the text label,
                the series of number items with default a series of number).

            Define one-basket as a new basket { the label "a" }.
            Define two-basket as a new basket { the label "b" }.
            Insert 7 into one-basket's items.
            State "{the number of one-basket's items} / {the number of two-basket's items}".
            """;
        Assert.Equal("1 / 0\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AFieldWithNoDefault_IsStillRequired()
    {
        // ★ The invariant the ROADMAP entry wanted kept. Defaults are opt-in per field; a field
        // without one is exactly as mandatory as it was.
        var ex = Assert.Throws<TypeException>(() => InterpretRaw("""
            Define object person with (the text name, the number age with default 0).
            Define p as a new person { the age 1 }.
            State p's name.
            """));
        Assert.Contains("field 'name' of 'person' is missing", ex.Message);
    }

    [Fact]
    public void AWronglyTypedDefault_IsRefusedAtTheDefinition()
    {
        // ⚠ MEASURED as a live hole before the check existed: this passed `check` outright,
        // because filling the value in only INFERRED its type and never compared it to the
        // field's. Refused at the DEFINITION — that is the sentence that is wrong.
        var ex = Assert.Throws<TypeException>(() => InterpretRaw("""
            Define object person with (the text name, the number age with default "old").
            Define p as a new person { the name "Ada" }.
            State p's age.
            """));
        Assert.Contains("the default for 'age' is a text, but the field holds a number", ex.Message);
    }

    [Fact]
    public void ADefaultOnAVoidableField_Works()
    {
        // The entry's own motivating case: a voidable field had to be supplied anyway, so
        // "may be absent" and "need not be written" were two different things with one spelling.
        const string src = """
            Define object person with (the text name, the voidable number age with default void).
            Define bo as a new person { the name "Bo" }.
            State bo's age but void is -1.
            """;
        Assert.Equal("-1\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ADefaultCanBeAnExpression_NotJustALiteral()
    {
        const string src = """
            Define object box with (the number width, the number area with default 6 * 7).
            Define b as a new box { the width 1 }.
            State b's area.
            """;
        Assert.Equal("42\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
