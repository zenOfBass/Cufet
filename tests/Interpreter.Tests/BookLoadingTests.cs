using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A book that lives in another file, brought in by the `Pull` that already existed.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Not a new mechanism. `Pull a book on ‹name›.` already resolved a bundled book and
/// `Pull ‹name›.` already resolved a module defined in the same file — and a module is an object
/// claiming the `module` interface, while a book is a module the language ships with. This adds a
/// third place to look for the same object. The namespace, the member access and the scope ending
/// at `Done.` were decided when modules were.
/// </para>
/// <para>
/// ⚠ Resolved and compiled TOGETHER. Whole-program visibility is what lets dispatch prove coverage
/// over every version of a name, bounds the open-union tag set, and monomorphizes a generic from
/// every filling — compiling files independently would reopen all three, and buys only build speed.
/// </para>
/// </remarks>
[Collection("SourceMap")]
public class BookLoadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cufet-books-" + Guid.NewGuid().ToString("n"));

    public BookLoadingTests() => Directory.CreateDirectory(_dir);

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

    private const string Kit = """
        Define object greeting-kit with () and book:
            Bind text to greet, given (the text who):
                Return "hello, " joined to who.
            Done.
        Done.
        """;

    [Fact]
    public void ABookInAnotherFileIsPulledLikeAnyOther()
    {
        Write("greeting-kit", Kit);
        Assert.Equal("hello, world", Run("""
            Pull a book on greeting-kit.
                State cast greeting-kit's greet on ("world").
            Done.
            """));
    }

    [Fact]
    public void ItsMembersAreReachedThroughItsName()
    {
        // ★ The namespace claim, and it needed nothing new — a module is an object, so its members
        // live on it. A bare `greet` is not in scope, which is the whole point of pulling.
        //
        // ★★ Refused at CHECK time. It was not when this test was written — an unknown name in a
        // free cast handed back a null type and was left to run time, so `cufet check` reported
        // "No problems found" for a program that died. Closing that hole is what moved this test
        // from a RuntimeException to a TypeException, and the comment is kept because the test
        // going red is how the change announced itself.
        Write("greeting-kit", Kit);
        var e = Refused("""
            Pull a book on greeting-kit.
                State cast greet on ("world").
            Done.
            """);
        Assert.Contains("'greet' isn't defined", e.Message);
    }

    [Fact]
    public void OneBookMayPullAnother()
    {
        Write("inner", """
            Define object inner with () and book:
                Bind number to double, given (the number n): Return n * 2. Done.
            Done.
            """);
        Write("outer", """
            Pull a book on inner.
                Define object outer with () and book:
                    Bind number to quadruple, given (the number n): Return n * 4. Done.
                Done.
            Done.
            """);
        // ⚠⚠ THIS A★ERTION USED TO READ `"outer loaded\n40"`, because `outer.cufe` carried a
        // top-level `State` and a pull RAN it. That is exactly the behaviour now refused — a
        // pulled file is a library, so its top level declares and does not act. The test's subject
        // is unchanged and is the whole of its name: one book may pull another.
        Assert.Equal("40", Run("""
            Pull a book on outer.
                State cast outer's quadruple on (10).
            Done.
            """));
    }

    [Fact]
    public void ABookThatPullsItselfRoundARingIsRefused()
    {
        // ★ Named rather than silently stopped. A ring is a mistake with a shape, and printing the
        // ring is what makes it fixable.
        // ⚠ The bodies DECLARE rather than act: a pulled file that does something is now
        // refused before the ring is reached, and this test is about the ring.
        Write("left", "Pull a book on right.\n    Define left-side as 1.\nDone.\n");
        Write("right", "Pull a book on left.\n    Define right-side as 2.\nDone.\n");
        var e = Refused("""
            Pull a book on left.
                State "host".
            Done.
            """);
        Assert.Contains("round a ring", e.Message);
    }

    [Fact]
    public void AMissingBookKeepsTheErrorItAlreadyHad()
    {
        // ⚠ The loader says nothing when the file is absent. The checker already refuses a name
        // that is neither bundled nor defined, and says what IS available — one message about
        // pulling, not a second one about files.
        var e = Refused("""
            Pull a book on nowhere-at-all.
                State "never".
            Done.
            """);
        Assert.Contains("nothing named 'nowhere-at-all' to pull", e.Message);
    }

    [Fact]
    public void AnErrorInsideALoadedBookReportsThatBooksOwnLine()
    {
        // ⚠⚠ The reason a loaded file is lexed at an OFFSET. Tokens and exceptions carry a line
        // and no file, so without the map an error from a book names the file that was RUN, at a
        // line out of that file's range — which is the one thing a multi-file error must not do.
        //
        // ★★ The number appears TWICE in a message: once in the reporter's header, and once in the
        // prose. 163 places in the front end put a line into prose, so the resolution happens to
        // the composed message rather than at each of them.
        Write("broken", """
            Define object broken with () and book:
                Bind number to bad, given (the number n):
                    Return n joined to "oops".
                Done.
            Done.
            """);
        var e = Refused("""
            Pull a book on broken.
                State "never".
            Done.
            """);
        Assert.Contains("line 3", e.Message);
        Assert.DoesNotContain("100", e.Message);
    }

    // ── A file’s top level is its own ───────────────────────────────────────────

    private const string KitWithHelpers = """
        Bind number to helper, given (the number n): Return n + 1. Done.

        Define kit-secret as 42 permanently.

        Define object node with (the number value).

        Define object kit with () and book:
            Bind number to use, given (the number n): Return cast helper on (n). Done.
        Done.
        """;

    [Fact]
    public void AModuleStillReachesItsOwnHelpers()
    {
        // ★ The half that must not break. Hiding is done by RENAMING to something unwritable, and
        // the file’s own references are renamed with it — so the module’s method still calls the
        // helper it was written against.
        Write("kit", KitWithHelpers);
        Assert.Equal("2", Run("""
            Pull a book on kit.
                State cast kit's use on (1).
            Done.
            """));
    }

    [Fact]
    public void WhatTheFileDeclaresBesideItsModuleIsOutOfReach()
    {
        // ★★ The decision this slice settled: a file is what does the hiding, and no marker does.
        // Pulling someone’s book hands you their module, not their working material — which is the
        // case that already bit once, when an overflow-guarded multiply wanted twice inside `math`
        // was inlined twice rather than become a permanent public member.
        Write("kit", KitWithHelpers);

        var constant = Refused("""
            Pull a book on kit.
                State "in".
            Done.
            State kit-secret.
            """);
        Assert.Contains("'kit-secret' isn't defined", constant.Message);

        var type = Refused("""
            Pull a book on kit.
                State "in".
            Done.
            Define spot as a new node { the value 7 }.
            """);
        Assert.Contains("'node' is not a defined object type", type.Message);
    }

    /// <remarks>
    /// ★★★ THE WRAPPER USED TO DECIDE PRIVACY, which nobody chose. `MakePrivate` walked the FLAT
    /// statement list, so a declaration written inside a top-level `Pull … Done.` was never
    /// renamed — and a `Bind` there is hoisted to a free function by both backends, so whoever
    /// pulled the book could call it by name.
    ///
    /// ⚠⚠ MEASURED 2026-09-22 on two books differing ONLY in that wrapper: the flat one's helper
    /// was refused, and the wrapped one answered 42 to the host.
    ///
    /// ★ It is not a rare shape. A file keeps its declarations inside a pull precisely so a
    /// SIGNATURE can name a type that pull introduces — `tools/snake/screen.cufe` was written that
    /// way for exactly that reason, and every book written like it leaked everything beside its
    /// module.
    /// </remarks>
    [Fact]
    public void AHelperInsideATopLevelPull_IsHiddenToo()
    {
        Write("wrapped", """
            Pull a book on math.
                Bind number to wrapped-secret: Return 42. Done.

                Define object wrapped with () and book:
                    Bind number to answer: Return cast wrapped-secret. Done.
                Done.
            Done.
            """);

        // The module still works — the file's own reference was renamed with the declaration.
        Assert.Equal("42", Run("""
            Pull a book on wrapped.
                State cast wrapped's answer.
            Done.
            """));

        var refused = Refused("""
            Pull a book on wrapped.
                State cast wrapped-secret.
            Done.
            """);
        Assert.Contains("'wrapped-secret' isn't defined", refused.Message);
    }

    /// <remarks>
    /// ★ A TYPE declared inside the pull is hidden by the same walk, and the helper that hands one
    /// back still works — which is the pair that proves the rename reached the type positions as
    /// well as the value ones.
    /// </remarks>
    [Fact]
    public void ATypeInsideATopLevelPull_IsHiddenAndStillUsable()
    {
        Write("boxes", """
            Pull a book on math.
                Define object box with (the number side):
                    Bind number to area: Return one's side * one's side. Done.
                Done.

                Bind box to boxed, given (the number n): Return a new box { the side n }. Done.

                Define object boxes with () and book:
                    Bind number to squared, given (the number n):
                        Return cast (cast boxed on (n))'s area.
                    Done.
                Done.
            Done.
            """);

        Assert.Equal("25", Run("""
            Pull a book on boxes.
                State cast boxes's squared on (5).
            Done.
            """));

        var refused = Refused("""
            Pull a book on boxes.
                Define mine as a new box { the side 2 }.
            Done.
            """);
        Assert.Contains("'box' is not a defined object type", refused.Message);
    }

    [Fact]
    public void TwoBooksMayEachHaveAHelperOfTheSameName()
    {
        // ★★ The payoff. Before this, both helpers hoisted to program scope and the duplicate-name
        // refusal fired on two declarations in two files a reader never saw together. Now neither
        // is in the host’s scope at all, and the name each author chose is their own business.
        Write("first", """
            Bind text to helper: Return "first". Done.
            Define object first with () and book:
                Bind text to speak: Return cast helper on (). Done.
            Done.
            """);
        Write("second", """
            Bind text to helper: Return "second". Done.
            Define object second with () and book:
                Bind text to speak: Return cast helper on (). Done.
            Done.
            """);
        Assert.Equal("first" + LF + "second", Run("""
            Pull books on first, and second.
                State cast first's speak on ().
                State cast second's speak on ().
            Done.
            """));
    }

    private const string LF = "\n";

    [Fact]
    public void ASingleFileProgramIsUntouched()
    {
        // The control. With no book to load the statements come back as the very same list, and a
        // message with a large number in it is left alone because no block was ever allocated.
        Assert.Equal("100003", Run("State 100003."));
    }

    // ── A bundled book's own file scope ──────────────────────────────────
    //
    // ★ Two changes that only work together. A bundled book's top level is now PRIVATISED the same
    // way an external book's is — `log-two` in `math.cufe` becomes `log-two in math` — and because
    // that name has a space in it and can never be written, a book's bodies may now import their
    // own file scope. Before, they imported NOTHING from the top level, because the prelude is
    // prepended to the writer's program and a book's local would otherwise collide with any name
    // the writer used.

    [Fact]
    public void ABundledBooksFileScope_IsReachableFromItsOwnMethods()
    {
        // `math`'s `log` and `exp` both read `log-two`, declared once at the file's top level. If a
        // book could not see its own file scope, neither would compute at all.
        Assert.Equal("2.0794415416798359282516963645", Run("""
            Pull a book on math.
                State cast math's log on (8) but void is 0.
            Done.
            """));
    }

    [Fact]
    public void ABundledBooksFileScope_IsNotReachableFromAProgram()
    {
        // ⚠ Private means private. The constant is renamed, so the name a writer would have to type
        // is not a name they can type.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Pull a book on math.
                State log-two.
            Done.
            """));

        Assert.Contains("log-two", ex.Message);
    }

    [Fact]
    public void AProgramsOwnNameCannotReachIntoABundledBook()
    {
        // ★★ THE BUG THE BLANKET REFUSAL WAS WRITTEN FOR, kept fixed by the narrowing rather than
        // by the refusal. `math`'s `log` keeps its running sum in a local called `total`; a program
        // declaring a FUNCTION of that name used to break it, because the book's body could see the
        // writer's top level. Now it imports only its own, so the two cannot meet.
        Assert.Equal("2.0794415416798359282516963645\n200", Run("""
            Bind number to total, given (the number x):
                Return x * 100.
            Done.

            Pull a book on math.
                State cast math's log on (8) but void is 0.
            Done.
            State cast total on (2).
            """));
    }

    [Fact]
    public void AnExternalBooksFileScope_WorksTheSameWay()
    {
        // The two kinds of book agree, which is the whole point of the change. An external book
        // could always do this; a bundled one could not.
        Assert.Equal("42", Run("""
            Define secret-number as 42 permanently.

            Define object helperkit with () and module:
                Bind number to answer:
                    Return secret-number.
                Done.
            Done.

            Pull helperkit.
                State cast helperkit's answer on ().
            Done.
            """));
    }

    [Fact]
    public void EveryBundledBook_ChecksCleanOnItsOwn()
    {
        // ⚠⚠ THE GAP THAT LET A BROKEN PRELUDE SHIP. Every other test checks a PROGRAM, which gets
        // the prelude spliced and privatised. Nobody checked a bundled book the way the editor does
        // — `cufet check src/Interpreter/Prelude/math.cufe`, where the book IS the program and
        // nothing privatises it. Narrowing a book's top-level import broke exactly that path, and
        // the whole suite stayed green while the extension put a red squiggle on `math.cufe`
        // four lines below the constant it said was undefined.
        //
        // ★ This is also what makes the language's own source lintable, which is why
        // TreatProgramAsPrelude exists at all.
        foreach (var (book, source) in TypeChecker.PreludeSources)
        {
            var tokens  = new CufetLexer(source).Tokenize();
            var program = new Parser(tokens).Parse();

            var failure = Record.Exception(
                () => new TypeChecker { TreatProgramAsPrelude = true }.Check(program));

            Assert.True(failure is null,
                $"the bundled book '{book}' does not check on its own: {failure?.Message}");
        }
    }
    // —— A file is a PROGRAM or a LIBRARY ————————————————————————
    //
    // ⚠⚠ A PULL RUNS THE LOADED FILE'S TOP LEVEL, and used to do it in SILENCE. MEASURED: a book
    // file with one `State` at the bottom printed it when another program pulled it — before that
    // program's own first line, exit 0, not a word. Pulling somebody's library ran their program
    // inside your block.
    //
    // ★★ The running was never the un-Cufet part; the SILENCE was. So a file is one or the other,
    // which is what Rust and Go both answer, and needs no marker for "only when I am the one being
    // run". The witness is `tools/shell.cufe`: its machinery is worth pulling and its last line
    // starts the shell, so nothing can borrow it.

    [Fact]
    public void AFileThatDoesSomethingAtItsTopLevel_CannotBePulled()
    {
        Write("greeter", """
            Define object greeter with () and book:
                Bind text to hello: Return "hi". Done.
            Done.

            State "this runs when you pull me".
            """);

        var ex = Assert.Throws<TypeException>(() => Run("""
            Pull a book on greeter.
                State cast greeter's hello.
            Done.
            """));

        Assert.Contains("does something at its top level", ex.Message);
        // ★ And it says what to DO, which is the whole of the fix: split the file.
        Assert.Contains("Split it in two", ex.Message);
    }

    /// <remarks>
    /// ★ THE JUDG—ENT CALL, pinned so it is a decision rather than an accident. A `Define` stays
    /// allowed — loose or `permanently` — because a constant is how a library is written, and the
    /// witness only asked that ACTIONS be refused. ⚠ The hole this leaves is known and deliberate:
    /// a `Define` whose VALUE does something still runs at pull time, and closing that needs an
    /// effect system the language does not have.
    /// </remarks>
    [Fact]
    public void ALibraryMayStillHoldDefines()
    {
        Write("sizes", """
            Define object sizes with () and book:
                Bind number to double, given (the number n): Return n * 2. Done.
            Done.

            Define loose as 5.
            Define fixed as 7 permanently.
            """);

        Assert.Equal("8", Run("""
            Pull a book on sizes.
                State (cast sizes's double on (4)) converted to text.
            Done.
            """));
    }

    /// <remarks>
    /// ⚠⚠ THE CASE THAT MAKES THE CHECK DESCEND. `examples/language/pennies.cufe` keeps its whole
    /// body inside `Pull a book on math. ... Done.`, so a check that looked only at the outermost
    /// statement would refuse a file the corpus already relies on. The walk asks
    /// `TypeChecker.FlattenHoistable` rather than repeating its descent.
    /// </remarks>
    [Fact]
    public void ALibraryWrittenEntirelyInsideAPull_IsStillALibrary()
    {
        Write("pennies", """
            Pull a book on math.
                Define object pennies with () and book:
                    Bind number to to-the-penny, given (the number amount):
                        Return (cast math's round on (amount * 100)) / 100.
                    Done.
                Done.
            Done.
            """);

        Assert.Equal("36.76", Run("""
            Pull a book on pennies.
                State (cast pennies's to-the-penny on (36.756)) converted to text.
            Done.
            """));
    }

    /// <remarks>⚠ And an action nested inside that pull is still caught — the descent finds it
    /// rather than the outer `Pull` hiding it.</remarks>
    [Fact]
    public void AnActionInsideThePull_IsCaughtToo()
    {
        Write("noisy", """
            Pull a book on math.
                Define object noisy with () and book:
                    Bind number to one-thing: Return 1. Done.
                Done.
                State "still runs".
            Done.
            """);

        var ex = Assert.Throws<TypeException>(() => Run("""
            Pull a book on noisy.
                State (cast noisy's one-thing) converted to text.
            Done.
            """));

        Assert.Contains("does something at its top level", ex.Message);
    }
}
