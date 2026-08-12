using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
public class PipelineTextFailureTests : PipelineTestBase
{

    [Fact]
    public void Fallibility_Propagation_MatchesInterpreter()
    {
        // `or pass the failure off` propagates a failure out of the enclosing fallible function;
        // the outer Try catches it. Also: a failure with no category → `the category is void`.
        const string src = """
            Bind number or failure to safe-div, given (the number x, the number y):
                If y is 0, return a failure "div by zero".
                return x / y.
            Done.
            Bind number or failure to compute, given (the number n):
                Define h as cast safe-div on (100, n) or pass the failure off.
                return h + 1.
            Done.
            Try to:
                Define r as cast compute on (0).
                State r.
            Done.
            In case of failure:
                State the message of the failure.
                If the category of the failure is void, state "no-category". Otherwise, state "has-category".
            Done.
            State cast compute on (0) but on failure 0.
            State cast compute on (5) but on failure 0.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Fallibility_ReadmeStyleParse_MatchesInterpreter()
    {
        // The README's parse-age shape (a validating fallible fn + Try/In case of failure),
        // adapted to avoid the deferred text ops (converted to number / joined to).
        const string src = """
            Bind number or failure to parse-positive, given (the number n):
                If n < 0, return a failure "not positive" of category "validation".
                return n.
            Done.
            Try to:
                Define good as cast parse-positive on (42).
                State good.
                Define bad as cast parse-positive on (0 - 7).
                State bad.
            Done.
            In case of failure:
                State the message of the failure.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Fallibility_ExceptionHandler_CompilesAndReRaises()
    {
        // E-prime: `In case of exception` now COMPILES (was the deferral this test used to assert).
        // The handler runs, and WITHOUT Suppress the fault re-raises — the compiled binary exits
        // nonzero after printing the handler's output, exactly like the interpreter's re-throw.
        const string src = """
            Try to:
                State 1 / 0.
            Done.
            In case of exception (the exception):
                State "caught".
                Suppress the exception.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 7: text-as-full-type (immutable const char*, results arena-allocated) ──

    [Fact]
    public void Text_Operations_MatchesInterpreter()
    {
        const string src = """
            State "hello" joined to " " joined to "world".
            State 42 converted to text.
            State (0.1 + 0.2) converted to text.
            State true converted to text.
            State the length of "hello".
            If "hello world" contains "world", state "yes". Otherwise, state "no".
            State the characters from 2 to 4 of "hello".
            State the first 3 characters of "hello".
            State the last 2 characters of "hello".
            State replace "o" with "0" in "hello world".
            State "Hello World" in uppercase.
            State "Hello World" in lowercase.
            State "  spaces  " trimmed.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ★ The test data that was missing, not the method. Every text test above uses ASCII, where
    // a byte, a UTF-16 code unit and a character are the same thing — so the oracle compared two
    // backends that agreed only because nothing ever asked them to differ. They did not: the
    // interpreter counted UTF-16 code units and the compiler counted bytes, so `the length of
    // "👍"` was 2 interpreted and 4 compiled, and every slice of a non-ASCII string produced
    // different text on each side. A character is now a code point on both.
    //
    // The four classes below are the ones that break different assumptions: two bytes and one
    // code unit (é), three bytes and one code unit (中), four bytes and TWO code units (👍 —
    // the case that proves the interpreter was wrong too, not merely the compiler), and two code
    // points that a reader sees as one character (e + combining acute), which is where the
    // code-points-not-graphemes decision shows through.
    //
    // Casing and trimming are deliberately absent. Casing is the already-documented
    // ASCII-versus-locale exception; trimming disagrees on Unicode whitespace for a different
    // reason entirely — what counts as whitespace, not where a character starts — and is its own
    // undecided question rather than part of this fix.
    [Fact]
    public void Text_NonAscii_MatchesInterpreter()
    {
        const string src =
            "State the length of \"héllo\".\n" +
            "State the length of \"中文\".\n" +
            "State the length of \"👍👍\".\n" +
            "State the length of \"é\".\n" +
            "State the characters from 1 to 2 of \"héllo\".\n" +
            "State the characters from 2 to 2 of \"héllo\".\n" +
            "State the characters from 3 to 5 of \"héllo\".\n" +
            "State the characters from 2 to 9 of \"中文中文\".\n" +
            "State the first 2 characters of \"héllo\".\n" +
            "State the last 3 characters of \"héllo\".\n" +
            "State the first 1 characters of \"👍👍\".\n" +
            "State the last 1 characters of \"中文\".\n" +
            "State the position of \"llo\" in \"héllo\" but void is 0.\n" +
            "State the position of \"文\" in \"中文\" but void is 0.\n" +
            "State the position of \"zz\" in \"héllo\" but void is 0.\n" +
            "If \"héllo\" contains \"é\", state \"yes\". Otherwise, state \"no\".\n" +
            "State replace \"é\" with \"e\" in \"héllo\".\n" +
            "State \"héllo\" joined to \"中文\".\n";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Text_ConvertAndFind_MatchesInterpreter()
    {
        // converted to number → voidable number (reuses 5C); position of → voidable number.
        const string src = """
            State "  42.5  " converted to number but void is 0.
            State "not a number" converted to number but void is -1.
            State "-3.14" converted to number but void is 0.
            State the position of "world" in "hello world" but void is 0.
            State the position of "xyz" in "hello world" but void is 0.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Text_ConvertAtTheDecimalBoundary_MatchesInterpreter()
    {
        // Found by mutation testing on Linux: nothing exercised either side of the compiled
        // parser's overflow guard, so `coef > max96` could become `coef >= max96` and the whole
        // suite stayed green.
        //
        // max96 is 2^96-1 — the largest coefficient a decimal can hold, and a perfectly ordinary
        // number to write down. One more digit of magnitude is not representable and must come
        // back void on BOTH backends; the interpreter gets that from decimal.TryParse, the
        // compiler from this guard, and the two only agree if the boundary sits in the same place.
        const string src = """
            State "79228162514264337593543950335" converted to number but void is -1.
            State "79228162514264337593543950336" converted to number but void is -1.
            State "7922816251426433759354395033.5" converted to number but void is -1.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Text_ReadmeParseAge_MatchesInterpreter()
    {
        // The README parse-age integration verbatim (text→number + fallibility + joined to),
        // written in the natural void-guard idiom `If n is void, return failure. Return n.`
        // Both backends narrow n to non-void on the guard's fall-through (guard-return narrowing).
        const string src = """
            Bind number or failure to parse-age, given (the text raw):
                Define n as raw converted to number.
                If n is void, return a failure "not a number" of category "validation".
                Return n.
            Done.
            Try to:
                Define age as cast parse-age on ("thirty").
                State age.
            Done.
            In case of failure:
                State "bad input: " joined to the message of the failure.
            Done.
            Try to:
                Define age as cast parse-age on ("42").
                State age.
            Done.
            In case of failure:
                State "bad input: " joined to the message of the failure.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void GuardNarrowing_DisjunctiveVoidGuard_MatchesInterpreter()
    {
        // The REFERENCE from-pair shape: `If x is void or y is void, return failure` narrows
        // BOTH x and y to non-void on the fall-through (¬(A or B) = ¬A and ¬B). Constructing a
        // point whose fields are plain `number` proves both were unwrapped, not left voidable.
        const string src = """
            Define object point with (the number x, the number y).
            Bind making a point or failure to from-pair, given (the text sx, the text sy):
                Define x as sx converted to number.
                Define y as sy converted to number.
                If x is void or y is void, return a failure "non-numeric".
                Return a new point { the x x, the y y }.
            Done.
            Try to:
                State cast from-pair on ("3", "4").
                State cast from-pair on ("bad", "4").
            Done.
            In case of failure:
                State "failed: " joined to the message of the failure.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void GuardNarrowing_DoesNotLeakPastArm_MatchesInterpreter()
    {
        // A guard inside an if-arm narrows only within that arm; after the arm the variable is
        // voidable again. Handling it with `but void is` on both paths must agree with the oracle.
        const string src = """
            Bind number to classify, given (the number flag, the text raw):
                Define n as raw converted to number.
                If flag is 1:
                    If n is void, return 0.
                    Return n.
                Done.
                Return n but void is -1.
            Done.
            State cast classify on (1, "5").
            State cast classify on (0, "bad").
            State cast classify on (0, "9").
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Text_RuntimeStringsCompose_MatchesInterpreter()
    {
        // Runtime-built strings as map keys (compared by value) and record fields.
        const string src = """
            Pull a rabbit.
                Define uid as "user-" joined to (42 converted to text).
                Define m as a map from text to number with (uid: 100).
                In m, the entry for ("user-" joined to (99 converted to text)) becomes 7.
                State the entry for "user-42" in m but void is 0.
                State the entry for "user-99" in m but void is 0.
                Define r as a record with (the name ("Ms " joined to "Alice"), the age 30).
                State r.
                If the name of r is "Ms Alice", state "match". Otherwise, state "no-match".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Text_SplitBy_MatchesInterpreter()
    {
        // split by → series of text (slice 8). Matches C# string.Split(string): empties kept,
        // trailing/leading delimiter → empty parts, delimiter-not-found → single whole element.
        const string src = """
            State "a,b,c" split by ",".
            State "a,,c," split by ",".
            State ",lead" split by ",".
            State "no-delimiter" split by ",".
            State "" split by ",".
            State the number of ("x=1;y=2;z=3" split by ";").
            Define parts as "10,20,30,40" split by ",".
            Define total as 0.
            For each part in parts, repeat:
                total becomes total + (part converted to number but void is 0).
            Done.
            State total.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 8: series-of-T generalization (per-element-type cser_N; split by rides on it) ──

    [Fact]
    public void Series_OfText_MatchesInterpreter()
    {
        const string src = """
            Define words as a series with ("banana", "apple", "cherry").
            Insert "date" into words.
            Insert "acai" into the start of words.
            State words.
            State item 2 of words.
            State the number of words.
            Remove "apple" from words.
            State words.
            For each w in words, repeat:
                State w in uppercase.
            Done.
            Define w2 as a series with ("acai", "banana", "cherry", "date").
            If words is w2, state "eq". Otherwise, state "neq".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Series_OfObjects_MatchesInterpreter()
    {
        const string src = """
            Define object point with (the number x, the number y).
            Define pts as a series with (a new point { the x 1, the y 2 }, a new point { the x 3, the y 4 }).
            State pts.
            Insert a new point { the x 5, the y 6 } into pts.
            For each p in pts, repeat:
                State the x of p.
            Done.
            Remove a new point { the x 3, the y 4 } from pts.
            State the number of pts.
            Define pa as a series with (a new point { the x 1, the y 2 }).
            Define pb as a series with (a new point { the x 1, the y 2 }).
            If pa is pb, state "eq". Otherwise, state "neq".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Series_Nested_MatchesInterpreter()
    {
        // Series of series: elements are reference-type pointers (shared), equality is deep.
        const string src = """
            Define grid as a series with (a series with (1, 2, 3), a series with (4, 5, 6)).
            State grid.
            Insert a series with (7, 8, 9) into grid.
            State the number of grid.
            For each row in grid, repeat:
                State the number of row.
            Done.
            State item 1 of grid.
            Define ga as a series with (a series with (1, 2)).
            Define gb as a series with (a series with (1, 2)).
            If ga is gb, state "eq". Otherwise, state "neq".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Series_OfMaps_MatchesInterpreter()
    {
        const string src = """
            Define m1 as a map from text to number with ("a": 1, "b": 2).
            Define m2 as a map from text to number with ("c": 3).
            Define ms as a series with (m1, m2).
            State the number of ms.
            For each m in ms, repeat:
                State the size of m.
            Done.
            State the entry for "a" in item 1 of ms but void is 0.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Current directory ────────────────────────────────────────────────

    [Fact]
    public void CurrentDirectory_Read_IsPresentOnBothBackends()
    {
        Assert.Equal(InterpretRaw("State the current directory is not void."),
                     CompileRaw("State the current directory is not void."));
    }

    // The headline test, and it deliberately does NOT compare the printed path. Two runtimes can
    // canonicalise the same directory differently (casing, separators, symlink resolution), so
    // asserting on the string would test path spelling rather than the feature. Writing to a
    // RELATIVE filename after the change proves the change actually took effect, on both sides.
    [Fact]
    public void CurrentDirectory_Change_AffectsRelativePaths_MatchesInterpreter()
    {
        AssertCwdOracle("""
            The current directory becomes "{DIR}".
            Write "landed here" to the file "probe.txt".
            Try to:
                Define c as read all from the file "probe.txt".
                State c.
            Done.
            In case of failure:
                State "could not read it back".
            Done.
            """);
    }

    [Fact]
    public void CurrentDirectory_MissingPath_FailsIdentically()
    {
        AssertCwdOracle("""
            Try to:
                The current directory becomes "/cufet-no-such-directory-xyz-9876".
            Done.
            In case of failure:
                State the category of the failure but void is "(none)".
                State the message of the failure.
            Done.
            """);
    }

    [Fact]
    public void CurrentDirectory_PathIsAFile_FailsIdentically()
    {
        // The category that exists only because both backends stat before changing: .NET cannot
        // distinguish this from not-found by exception type, and Windows chdir reports ENOENT
        // rather than ENOTDIR. Getting "not-a-directory" from both is the whole point.
        AssertCwdOracle("""
            Write "x" to the file "{DIR}/afile.txt".
            Try to:
                The current directory becomes "{DIR}/afile.txt".
            Done.
            In case of failure:
                State the category of the failure but void is "(none)".
            Done.
            """);
    }

    // ★ Top-level mutual recursion compiles and runs — that is the documented promise, and it
    // holds. What follows only pins the NESTED case, where a Bind is a closure emitted where it
    // stands and a forward reference genuinely cannot resolve.
    // ★ Widening into a union at a CALL ARGUMENT. Every other slot — a variable, an object field,
    // a series element — already coerced; this one emitted the raw value, so C received a
    // `cd_box` where a union struct was declared. The checker passed it, `check --native` passed
    // it, and only gcc objected: the shape of bug the oracle cannot catch, because there is no
    // binary to compare against.
    [Fact]
    public void ObjectWidenedIntoAUnionParameter_MatchesInterpreter()
    {
        const string src = """
            Define object box with (the number weight).

            Bind text to describe, given (the (number or box) thing):
                If thing is a number:
                    Return "num".
                Done.
                Otherwise:
                    Return "box".
                Done.
            Done.

            Define parcel as a new box { the weight 1 }.
            State cast describe on (parcel).
            State cast describe on (7).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ARecursiveUnionValue_MatchesInterpreter()
    {
        // A union cannot be named, so it cannot refer to itself — but an OBJECT type can, and an
        // object type may appear in a union. That is the whole route to a recursive sum type in
        // Cufet today, and it is what a JSON value needs.
        const string src = """
            Define object jarray with (the series of (number or text or fact or jarray) items).

            Bind text to render, given (the (number or text or fact or jarray) value):
                If value is a number:
                    Return value converted to text.
                Done.
                Otherwise if value is a text:
                    Return "\"" joined to value joined to "\"".
                Done.
                Otherwise if value is a fact:
                    Return value converted to text.
                Done.
                Otherwise:
                    Define out as "[".
                    Define leading as true.
                    For each kid in the items of value, repeat:
                        If leading is false, the out becomes out joined to ",".
                        The out becomes out joined to cast render on (kid).
                        The leading becomes false.
                    Done.
                    Return out joined to "]".
                Done.
            Done.

            Define inner as a new jarray { the items a series of (number or text or fact or jarray) with (2, 3) }.
            Define outer as a new jarray { the items a series of (number or text or fact or jarray) with (1, "hi", true, inner) }.
            State cast render on (outer).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Judge ─────────────────────────────────────────────────────────────
    //
    // Lowers to a tag dispatch on the union, with the subject evaluated once into a C local named
    // `cv_it` inside a fresh block — so a nested Judge shadows an outer one through C's own
    // scoping rather than through generated names.

    [Fact]
    public void Judge_ExhaustiveUnion_MatchesInterpreter()
    {
        const string src = """
            Define the (number or text or fact) thing as 42.

            Judge thing, where it is:
                A number, state "a number".
                A text, state "some text".
                A fact, state "a fact".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Judge_OtherwiseAndGrouping_MatchesInterpreter()
    {
        const string src = """
            Define the (number or text or fact) thing as "hi".

            Judge thing, where it is:
                A number or a fact, state "not text".
                Otherwise, state "text".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Judge_NarrowsItInsideEachArm_MatchesInterpreter()
    {
        // `the length of` is text-only, so this compiles at all only because the arm narrowed —
        // and the C side has to emit `.val.c<k>` for the same reason.
        const string src = """
            Define the (number or text) thing as "hello".

            Judge thing, where it is:
                A text, state the length of it.
                Otherwise, state "not text".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Judge_OverAnObjectUnionTree_MatchesInterpreter()
    {
        // The self-hosting shape: an AST as a closed union of object types, walked recursively,
        // every arm returning. Exercises narrowing, coverage and return-path analysis at once.
        const string src = """
            Define object num-node with (the number value).
            Define object add-node with (the series of (num-node or add-node) kids).

            Bind number to eval, given (the (num-node or add-node) node):
                Judge node, where it is:
                    A num-node, return the value of it.
                    An add-node:
                        Define total as 0.
                        For each kid in the kids of it, repeat:
                            The total becomes total + cast eval on (kid).
                        Done.
                        Return total.
                    Done.
                Done.
            Done.

            Define two as a new num-node { the value 2 }.
            Define three as a new num-node { the value 3 }.
            Define sum as a new add-node { the kids a series of (num-node or add-node) with (two, three) }.
            State cast eval on (sum).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Judge_OverANonUnion_IsRefusedCleanly()
    {
        // Value arms are not built yet. The checker accepts this with an Otherwise, so the
        // compiler must refuse it rather than emit something that does not dispatch — a clean
        // refusal is the sanctioned escape, silently wrong output is not.
        var ex = Assert.Throws<CompilerException>(() => Compile("""
            Define thing as 42.

            Judge thing, where it is:
                A number, state "a number".
                Otherwise, state "other".
            Done.
            """));
        Assert.Contains("not a closed union", ex.Message);
    }

    [Fact]
    public void MutualRecursion_AtTopLevel_MatchesInterpreter()
    {
        const string src = """
            Bind fact to is-even, given (the number n):
                If n is 0, return true.
                Return Cast is-odd on (n - 1).
            Done.

            Bind fact to is-odd, given (the number n):
                If n is 0, return false.
                Return Cast is-even on (n - 1).
            Done.

            State Cast is-even on (10).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void MutualRecursion_InsideARabbit_IsRefusedWithTheRealReason()
    {
        // The refusal is legitimate — a nested Bind compiles where it stands, so neither of two
        // functions that call each other can come first. The MESSAGE was the bug: it said the
        // name was "not a known function or method" about a function declared six lines below,
        // which sends the reader looking for a typo instead of moving the pair to the top level.
        var ex = Assert.Throws<CompilerException>(() => Compile("""
            Pull a rabbit.
                Bind fact to is-even, given (the number n):
                    If n is 0, return true.
                    Return Cast is-odd on (n - 1).
                Done.

                Bind fact to is-odd, given (the number n):
                    If n is 0, return false.
                    Return Cast is-even on (n - 1).
                Done.

                State Cast is-even on (10).
            Done.
            """));
        Assert.Contains("declared further down this block", ex.Message);
        Assert.Contains("TOP LEVEL", ex.Message);
        Assert.DoesNotContain("not a known function", ex.Message);
    }

    [Fact]
    public void UnknownFunction_StillSaysItIsUnknown()
    {
        // The other half of the same branch: a name that really is bound nowhere must keep the
        // blunt message, or the fix would have traded one misleading sentence for another.
        var ex = Assert.Throws<CompilerException>(() => Compile("""
            Pull a rabbit.
                State Cast no-such-function on (1).
            Done.
            """));
        Assert.Contains("not a known function or method", ex.Message);
        Assert.DoesNotContain("declared further down", ex.Message);
    }

    [Fact]
    public void CurrentDirectory_ChangeInsideTask_IsRefusedCleanly()
    {
        // A process has one working directory, so changing it from a task races every other
        // thread's relative-path resolution. The compiler refuses rather than shipping a
        // construct the cooperative interpreter would run deterministically.
        var ex = Assert.Throws<CompilerException>(() => Compile("""
            Pull a rabbit.
                Have rabbit start a task as worker:
                    The current directory becomes "/tmp".
                    return 1.
                Done.
                Define r as the awaited result of worker.
                State r.
            Done.
            """));
        Assert.Contains("cannot change the current directory", ex.Message);
        Assert.DoesNotContain("slice", ex.Message);
    }

    [Fact]
    public void File_WriteReadRoundtrip_MatchesInterpreter()
    {
        AssertFileOracle("""
            Write "hello world" to the file "{PATH}".
            Try to:
                Define c as read all from the file "{PATH}".
                State c.
                State the length of c.
            Done.
            In case of failure:
                State "read failed".
            Done.
            """);
    }

    [Fact]
    public void File_AppendAndReadLines_MatchesInterpreter()
    {
        // ReadAllLines semantics: 3 lines, no trailing empty (append adds two more lines).
        AssertFileOracle("""
            Write "first" to the file "{PATH}".
            Append "\nsecond\nthird" to the file "{PATH}".
            Try to:
                Define lines as read all lines from the file "{PATH}".
                State the number of lines.
                For each ln in lines, repeat:
                    State "line: " joined to ln.
                Done.
            Done.
            In case of failure:
                State "fail".
            Done.
            """);
    }

    [Fact]
    public void File_PathChecks_MatchesInterpreter()
    {
        AssertFileOracle("""
            Write "x" to the file "{PATH}".
            If the path "{PATH}" exists, state "exists". Otherwise, state "gone".
            If the path "{PATH}" is a file, state "is-file". Otherwise, state "not-file".
            If the path "{PATH}" is a directory, state "is-dir". Otherwise, state "not-dir".
            If the path "no-such-path-zzz" exists, state "exists". Otherwise, state "gone".
            """);
    }

    [Fact]
    public void File_NotFound_FailureMatchesInterpreter()
    {
        // The OS-error bridge: a missing file → not-found failure with the templated message
        // (category + message reproduced bit-identically by the errno path).
        AssertFileOracle("""
            Define fallback as read all from the file "no-such-file-abc.txt" but on failure "DEFAULT".
            State fallback.
            Try to:
                Define x as read all from the file "no-such-file-abc.txt".
                State x.
            Done.
            In case of failure:
                State "cat: " joined to (the category of the failure but void is "none").
                State "msg: " joined to the message of the failure.
            Done.
            """);
    }

    // ── Slice 9B: streams + With…open + stdin (close-on-all-paths cleanup) ──

    [Fact]
    public void With_ReadWriteStreams_MatchesInterpreter()
    {
        AssertFileOracle("""
            With the file "{PATH}" open for writing as out:
                write "alpha\n" to out.
                write "beta\n" to out.
                write "gamma" to out.
            Done.
            With the file "{PATH}" open for reading as inp:
                Define first as read a line from inp.
                State first but void is "?".
                Define second as read a line from inp.
                State second but void is "?".
                State read all from inp.
            Done.
            With the file "{PATH}" open for reading as inp2:
                Define lines as read all lines from inp2.
                State the number of lines.
                For each ln in lines, repeat:
                    State "L: " joined to ln.
                Done.
            Done.
            """);
    }
}
