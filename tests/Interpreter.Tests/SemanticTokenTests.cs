using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// The semantic-token producer answers the one question a TextMate grammar cannot: what KIND of
// thing is this word. These tests pin the answer for each kind, and pin the positions too — a
// token whose kind is right and whose column is wrong paints the word next door.
public class SemanticTokenTests
{
    private static IReadOnlyList<SemanticToken> Classify(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        var checker = new TypeChecker();
        checker.Check(program);
        return SemanticTokenizer.Collect(program, tokens, checker);
    }

    private static (int, int, int, string, bool) Shape(SemanticToken t) =>
        (t.Line, t.Column, t.Length, SemanticTokenLegend.NameOf(t.Kind),
         t.Modifiers.HasFlag(SemanticTokenModifier.Declaration));

    // ★ The whole reason a `keyword` kind exists. `output` and `seed` open statements but lex as
    // identifiers, so a program may also name a variable either one. A TextMate grammar was tried
    // twice and cannot win: colouring the word always paints a variable as a keyword, and
    // colouring only the CAPITALISED spelling gives one statement two colours depending on whether
    // its line has been capitalised yet. These pin the answer the parse makes available.
    [Fact]
    public void OutputStatement_IsAKeyword_WhicheverWayItIsCapitalised()
    {
        var lower = Classify("Bind void to emit:\n    output 1.\nDone.");
        var upper = Classify("Bind void to emit:\n    Output 1.\nDone.");

        Assert.Contains((2, 5, 6, "keyword", false), lower.Select(Shape));
        Assert.Contains((2, 5, 6, "keyword", false), upper.Select(Shape));

        // Not merely both present — identical, so capitalising a line cannot change a colour.
        Assert.Equal(lower.Select(Shape), upper.Select(Shape));
    }

    [Fact]
    public void AVariableNamedOutput_IsAVariableEverywhere()
    {
        // The other half. If this ever comes back "keyword", the producer has started doing what
        // the grammar used to do wrong, and someone's variable is painted as language syntax.
        var shapes = Classify("Define output as 9.\nThe output becomes 10.\nState output.")
            .Select(Shape).ToList();

        Assert.Equal(
            [(1, 8, 6, "variable", true), (2, 5, 6, "variable", false), (3, 7, 6, "variable", false)],
            shapes);
    }

    [Fact]
    public void SeedStatement_IsAKeyword_WhicheverWayItIsCapitalised()
    {
        var lower = Classify("Pull a book on chance.\n    seed the chance with 42.\nDone.");
        var upper = Classify("Pull a book on chance.\n    Seed the chance with 42.\nDone.");

        Assert.Contains((2, 5, 4, "keyword", false), lower.Select(Shape));
        Assert.Equal(lower.Select(Shape), upper.Select(Shape));
    }

    [Fact]
    public void Legend_IndexesMatchTheKindEnum()
    {
        // B2's editor providers register this array and then send indices into it, so the ORDER
        // is the wire format — a reordering here silently recolours every file.
        Assert.Equal(
            ["namespace", "type", "parameter", "variable", "property", "function", "keyword"],
            SemanticTokenLegend.Kinds);

        foreach (SemanticTokenKind kind in Enum.GetValues<SemanticTokenKind>())
            Assert.Equal(SemanticTokenLegend.Kinds[(int)kind], SemanticTokenLegend.NameOf(kind));

        Assert.Equal(["declaration"], SemanticTokenLegend.Modifiers);
        Assert.Equal(["declaration"], SemanticTokenLegend.NamesOf(SemanticTokenModifier.Declaration));
        Assert.Empty(SemanticTokenLegend.NamesOf(SemanticTokenModifier.None));
    }

    [Fact]
    public void EveryKind_IsClassifiedAndPlaced()
    {
        //             1         2         3         4         5
        //    1234567890123456789012345678901234567890123456789012345
        var source = """
            Define object point with (the number x, the number y).

            Bind number to widen, given (the number span):
                return span * 2.
            Done.

            Define here as a new point { the x 3, the y 4 }.
            Define reach as Cast widen on (the x of here).
            State reach.
            State here's y.

            Pull a book on math as m.
                State m's square-root of (144).
            Done.
            """;

        Assert.Equal(
            [
                (1,  15, 5,  "type",      true),   // object point
                (1,  38, 1,  "property",  true),   // field x
                (1,  52, 1,  "property",  true),   // field y
                (3,  16, 5,  "function",  true),   // Bind widen
                (3,  41, 4,  "parameter", true),   // given (the number span)
                (4,  12, 4,  "parameter", false),  // return span * 2
                (7,   8, 4,  "variable",  true),   // Define here
                (7,  22, 5,  "type",      false),  // a new point
                (7,  34, 1,  "property",  false),  // the x 3
                (7,  43, 1,  "property",  false),  // the y 4
                (8,   8, 5,  "variable",  true),   // Define reach
                (8,  22, 5,  "function",  false),  // Cast widen — the callee, not a value
                (8,  36, 1,  "property",  false),  // the x of here
                (8,  41, 4,  "variable",  false),  // ... of here
                (9,   7, 5,  "variable",  false),  // State reach
                (10,  7, 6,  "variable",  false),  // State here's y — the owner spans its 's
                (10, 14, 1,  "property",  false),  // here's y
                (12, 16, 4,  "namespace", true),   // Pull a book on math — no marker, no widening
                (12, 24, 1,  "namespace", true),   // ... as m
                (13, 11, 3,  "namespace", false),  // m's square-root — owner + marker
                (13, 15, 11, "function",  false),  // the book member, spanning both its words
            ],
            Classify(source).Select(Shape));
    }

    [Fact]
    public void MethodsAndFieldsOfAnObject_AreClassifiedThroughThePossessive()
    {
        var source = """
            Define object dog with (the text name):
                Bind void to speak:
                    State one's name.
                Done.
            Done.

            Define rex as a new dog { the name "Rex" }.
            Cast rex's speak.
            """;

        Assert.Equal(
            [
                (1, 15, 3, "type",     true),   // object dog
                (1, 34, 4, "property", true),   // field name
                (2, 18, 5, "function", true),   // method speak
                (3, 21, 4, "property", false),  // one's name — the field, through the receiver
                (7,  8, 3, "variable", true),   // Define rex
                (7, 21, 3, "type",     false),  // a new dog
                (7, 31, 4, "property", false),  // the name "Rex"
                (8,  6, 5, "variable", false),  // Cast rex's speak — the receiver is a value
                (8, 12, 5, "function", false),  // ... and the member is the method being called
            ],
            Classify(source).Select(Shape));
    }

    // ★ The grammar scopes `rex's` as ONE word on purpose — see the `possessive` rule's comment in
    // cufet.tmLanguage.json. A semantic token that stopped at `rex` would repaint the name and
    // leave the marker in the grammar's colour, so the word would visibly change colour halfway
    // through. The producer is what has to match the grammar's span, so it widens the owner.
    [Fact]
    public void APossessiveOwner_IsWidenedToCoverItsMarker()
    {
        var source = """
            Define object dog with (the text name).
            Define rex as a new dog { the name "Rex" }.
            State rex's name.
            State rex.
            """;

        var shapes = Classify(source).Select(Shape).ToList();

        Assert.Contains((3, 7, 5, "variable", false), shapes);   // rex's — 3 letters plus the marker
        Assert.Contains((4, 7, 3, "variable", false), shapes);   // the same name, bare, stays 3

        // Chained: every owner in the chain owns a marker, and each is widened in turn.
        var chained = Classify("""
            Define object engine with (the number power).
            Define object car with (the engine motor).
            Define mine as a new car { the motor a new engine { the power 300 } }.
            State mine's motor's power.
            """).Select(Shape).ToList();

        Assert.Contains((4, 7, 6, "variable", false), chained);   // mine's
        Assert.Contains((4, 14, 7, "property", false), chained);  // motor's
        Assert.Contains((4, 22, 5, "property", false), chained);  // power — nothing follows it
    }

    [Fact]
    public void FieldsThatReachTheAstOutOfOrder_StillLandOnTheirOwnWords()
    {
        // An object's named fields arrive from the parser sorted by name, not as written — 'suit'
        // is spelled first and reaches the AST second. A placer that only ever swept forward would
        // find 'rank', then run off the end of the header looking for 'suit'.
        var source = """
            Define object card with (the text suit, the text rank).
            Define ace as a new card { the suit "spades", the rank "A" }.
            State ace's rank.
            """;

        Assert.Equal(
            [
                (1, 15, 4, "type",     true),
                (1, 35, 4, "property", true),   // suit, declared first
                (1, 50, 4, "property", true),   // rank, declared second
                (2,  8, 3, "variable", true),
                (2, 21, 4, "type",     false),
                (2, 32, 4, "property", false),
                (2, 51, 4, "property", false),
                (3,  7, 5, "variable", false),  // ace's — the marker rides with its owner
                (3, 13, 4, "property", false),
            ],
            Classify(source).Select(Shape));
    }

    [Fact]
    public void InterpolatedNames_AreClassifiedInsideTheirString()
    {
        // A grammar sees one string literal here. The names inside the braces are ordinary
        // references, and they are exactly the words a grammar cannot classify.
        var source = """
            Define who as "world".
            State "hello {who}".
            """;

        Assert.Equal(
            [
                (1, 8,  3, "variable", true),
                (2, 15, 3, "variable", false),
            ],
            Classify(source).Select(Shape));
    }

    [Fact]
    public void TypeNamesInAnnotations_AreClassified()
    {
        // A type name in an annotation is the one place a word is a type without being spelled any
        // differently from a variable — the gap semantic tokens exist to close. A CufetType carries
        // no position (it is shared and cached), so each name is found in the annotation's own
        // token span instead.
        var source = """
            Define object card with (the text rank).
            Define object deck with (the series of card cards).

            Bind card to top, given (the deck d):
                Return item 1 of d's cards.
            Done.

            Define pile as a series of card.
            Insert a new card { the rank "A" } into pile.
            Define box as a new deck { the cards pile }.
            Define got as Cast top on (box).
            State got's rank.

            Define rows as a series of records like (the text word, the card owner).
            """;

        var tokens = Classify(source).Select(Shape).ToList();

        // Field annotation: 'the series of card cards' — the element type, then the field name.
        Assert.Contains((2, 40, 4, "type",     false), tokens);
        Assert.Contains((2, 45, 5, "property", true),  tokens);
        // Return type, then the function name, then the parameter's type and its name.
        Assert.Contains((4,  6, 4, "type",      false), tokens);
        Assert.Contains((4, 14, 3, "function",  true),  tokens);
        Assert.Contains((4, 30, 4, "type",      false), tokens);
        Assert.Contains((4, 35, 1, "parameter", true),  tokens);
        // 'a series of card' in a value position.
        Assert.Contains((8, 28, 4, "type", false), tokens);
        // A record shape spells its field names between their types; all three are placed.
        Assert.Contains((14, 51, 4, "property", false), tokens);
        Assert.Contains((14, 61, 4, "type",     false), tokens);
        Assert.Contains((14, 66, 5, "property", false), tokens);

        // Built-in scalars stay the grammar's job: the 'text' on line 1 (column 30) and the one on
        // line 14 (column 46) are keyword tokens a TextMate grammar already colours.
        Assert.DoesNotContain(tokens, t => t.Item1 == 1  && t.Item2 == 30);
        Assert.DoesNotContain(tokens, t => t.Item1 == 14 && t.Item2 == 46);
    }

    [Fact]
    public void JudgeArmCases_AreClassifiedAsTypes()
    {
        // A judgement's arms are the one place a bare type name opens a statement, so the grammar
        // reads them as ordinary references. Each arm's cases are spelled in its own header, and a
        // grouped arm spells two.
        var source = """
            Define object circle with (the number radius).
            Define object square with (the number side).
            Define the (circle or square) shape as a new circle { the radius 2 }.
            Judge shape, where it is:
                A circle, state "round".
                A square, state "cornered".
            Done.
            """;

        var tokens = Classify(source).Select(Shape).ToList();

        Assert.Contains((5,  7, 6, "type", false), tokens);   // A circle,
        Assert.Contains((6,  7, 6, "type", false), tokens);   // A square,

        // A grouped arm, and bodies that are their own scopes rather than the judgement's.
        var grouped = Classify("""
            Define object circle with (the number radius).
            Define object square with (the number side).
            Define the (circle or square) shape as a new circle { the radius 2 }.
            Judge shape, where it is:
                A circle or a square, state "a shape".
            Done.
            """).Select(Shape).ToList();

        Assert.Contains((5,  7, 6, "type", false), grouped);  // A circle ...
        Assert.Contains((5, 19, 6, "type", false), grouped);  // ... or a square
    }

    [Fact]
    public void CompoundAnnotations_ClassifyEveryTypeTheySpell()
    {
        var source = """
            Define object card with (the text rank).
            Define object deck with (the series of card cards).
            Define ace as a new card { the rank "A" }.

            Define lookup as a map from text to card with ("a" : ace).
            Define mixed as a catalogue of (card or deck) with (ace).
            Define index as an atlas from text to card with ("a" : ace).
            Define wire as a channel of card.

            If ace is a card:
                State "a card is a card".
            Done.
            """;

        var types = Classify(source)
            .Where(t => t.Kind == SemanticTokenKind.Type)
            .Select(t => (t.Line, t.Column, t.Length))
            .ToList();

        Assert.Contains((5, 37, 4), types);   // map from text to card
        Assert.Contains((6, 33, 4), types);   // catalogue of (card or deck)
        Assert.Contains((6, 41, 4), types);   // ... the second case
        Assert.Contains((7, 39, 4), types);   // atlas from text to card
        Assert.Contains((8, 29, 4), types);   // a channel of card
        Assert.Contains((10, 13, 4), types);  // ace is a card

        // The string on line 11 says 'card' twice and is not an annotation.
        Assert.DoesNotContain(types, t => t.Line == 11);
    }

    [Fact]
    public void AVariableSharingATypesName_IsNotRecolouredAsAType()
    {
        // The annotation path only ever emits words the annotation itself names, so a value that
        // happens to be spelled like a type keeps its own kind.
        var source = """
            Define object thing with (the text name).
            Define thing as 5.
            State thing.
            Define pick as a function given (the thing t): Return t's name. Done.
            """;

        var tokens = Classify(source).Select(Shape).ToList();

        Assert.Contains((1, 15, 5, "type",     true),  tokens);   // the declaration
        Assert.Contains((2,  8, 5, "variable", true),  tokens);   // Define thing as 5
        Assert.Contains((3,  7, 5, "variable", false), tokens);   // State thing
        Assert.Contains((4, 38, 5, "type",     false), tokens);   // given (the thing t)
        Assert.DoesNotContain(tokens, t => t.Item1 is 2 or 3 && t.Item4 == "type");
    }

    [Fact]
    public void KeywordBoundNames_AreLeftToTheGrammar()
    {
        // 'one' and 'it' are spelled with keyword tokens, so a TextMate grammar already colours
        // them. Emitting over them would only fight the theme.
        var source = """
            Define xs as a series of number with (1, 2, 3).
            For each in xs, repeat:
                State it.
            Done.
            """;

        var kinds = Classify(source);
        Assert.All(kinds, t => Assert.NotEqual(3, t.Line));   // nothing emitted for 'it'
        Assert.Equal(
            [
                (1, 8,  2, "variable", true),   // Define xs
                (2, 13, 2, "variable", false),  // For each in xs
            ],
            kinds.Select(Shape));
    }

    [Fact]
    public void AParameterNamedLikeItsFunction_StaysOnItsOwnWord()
    {
        // The cursor that places a construct's names walks forward once per name. Without that,
        // the parameter here would be reported at the function name's column.
        var source = """
            Bind number to span, given (the number span):
                return span.
            Done.
            State Cast span on (2).
            """;

        Assert.Equal(
            [
                (1,  16, 4, "function",  true),
                (1,  40, 4, "parameter", true),
                (2,  12, 4, "parameter", false),
                (4,  12, 4, "function",  false),
            ],
            Classify(source).Select(Shape));
    }

    // ★ A DESUGARED statement must carry the position of the real token it stands for, never
    // the verb's. `Increment total-bits by w.` becomes `total-bits becomes total-bits + w`, and
    // the walker paints Name.Length characters starting at the statement's own Line/Column — so a
    // node built at the verb's position painted the first ten characters of the line, which is
    // "Increment ", as a variable. The damage looked different on every line because the count
    // was the length of the TARGET's name, and it beat the TextMate grammar because semantic
    // tokens win over it.
    [Fact]
    public void Increment_PaintsItsTarget_NotTheVerb()
    {
        const string source =
            "Define total-bits as 0.\n"
          + "Define w as 5.\n"
          + "Increment total-bits by w.";
        var tokens = Classify(source);

        // `Increment` is columns 1-9 on line 3; `total-bits` starts at 11, `w` at 25.
        Assert.Contains((3, 11, 10, "variable", false), tokens.Select(Shape));
        Assert.Contains((3, 25, 1, "variable", false), tokens.Select(Shape));
        Assert.DoesNotContain(tokens, t => t.Line == 3 && t.Column <= 9);
    }

    [Fact]
    public void IncrementOnAField_PaintsTheField_NotTheVerb()
    {
        const string source =
            "Define object counter with (the number tally):\n"
          + "    Bind void to bump, Increment one's tally by 3.\n"
          + "Done.";
        var tokens = Classify(source);

        // `Increment` is columns 24-32 on line 2; `tally` starts at 40.
        Assert.Contains((2, 40, 5, "property", false), tokens.Select(Shape));
        Assert.DoesNotContain(tokens, t => t.Line == 2 && t.Column >= 24 && t.Column <= 32);
    }
    // -- Nothing inside an axiom is painted, whichever language it holds -------
    //
    // ⭐⭐ The rule is about the SURFACE, not about Cufet. `[ … ]` means the text inside is not
    // the program around it, and it has to mean that whichever tag it carries. Foreign source
    // cannot be highlighted — the brackets do not say which language, and a grammar injection per
    // language is not on offer — so Cufet source inside the same brackets is not highlighted
    // either. Axioms look alike because they ARE alike from the outside.
    //
    // ⚠ It stopped being true by accident. A runnable cufet axiom is lowered to a `Bind`, so the
    // walk descended into its body; the lexer offset gave those statements real positions, so the
    // tokens landed correctly and painted it. Both of those are right on their own — what was
    // wrong was letting them decide how a block looks.
    //
    // ⚠ And it was never uniform even within one axiom: a `Define` inside a block emits through
    // the token stream, where the whole block is a single `Axiom` token and nothing is found, while
    // the next line emits from its AST position and painted. Half a block lit, which is why this
    // reads as arbitrary rather than as a decision.

    [Fact]
    public void ARunnableCufetAxiom_PaintsItsDeclarationButNotItsBody()
    {
        var tokens = Classify("""
            Pull a book on cufet.
                Define cufet number doubled, given (the number value), as [
                    Define the scratch as 2.
                    Return the value * the scratch.
                ].
                State cast doubled on (21).
            Done.
            """);

        // The declaration line is ordinary Cufet and is painted — it sits outside the brackets.
        Assert.Contains(tokens, t => t.Line == 2
            && SemanticTokenLegend.NameOf(t.Kind) == "function"
            && t.Modifiers.HasFlag(SemanticTokenModifier.Declaration));
        Assert.Contains(tokens, t => t.Line == 2
            && SemanticTokenLegend.NameOf(t.Kind) == "parameter");

        // Lines 3 and 4 are inside the brackets.
        Assert.DoesNotContain(tokens, t => t.Line is 3 or 4);
    }

    [Fact]
    public void ACufetSourceBlock_PaintsItsNameButNotItsBody()
    {
        // The other half of the pair. This one never painted its body — it is here so that the two
        // kinds of block are held to one appearance by one test file, and widening either has to
        // answer for both.
        var tokens = Classify("""
            Pull a book on cufet.
                Define cufet shapes as [
                    Define object vec2 with (the number x): Done.
                ].
                Cite shapes.
            Done.
            """);

        Assert.Contains(tokens, t => t.Line == 2 && t.Column == 18);   // `shapes`, outside
        Assert.DoesNotContain(tokens, t => t.Line == 3);               // inside the brackets
    }

    // ── Documentation on a token ─────────────────────────────────────────
    //
    // What hover reads. The doc travels on USAGES as well as declarations, because hovering a call
    // is the case that matters — and it is what keeps name resolution out of the editor.

    [Fact]
    public void ADocumentedFunction_CarriesItsDocAtTheDeclaration()
    {
        var docs = Classify("""
            /// Adds two numbers together.
            Bind number to summed, given (the number left, the number right):
                Return left + right.
            Done.
            State cast summed on (1, 2).
            """).Where(t => t.Doc is not null).ToList();

        Assert.Equal(2, docs.Count);   // the declaration and the call
        Assert.All(docs, d => Assert.Equal("Adds two numbers together.", d.Doc));
    }

    [Fact]
    public void ADocumentedFunction_CarriesItsDocAtEveryUse()
    {
        // ★ The point of the whole slice. An editor asking "what is under my cursor" gets an answer
        // without resolving the name itself, so hover can never disagree with the colours.
        var call = Assert.Single(Classify("""
            /// Adds two numbers together.
            Bind number to summed, given (the number left, the number right):
                Return left + right.
            Done.
            State cast summed on (1, 2).
            """).Where(t => t.Doc is not null && !t.Modifiers.HasFlag(SemanticTokenModifier.Declaration)));

        Assert.Equal(5, call.Line);
        Assert.Equal("Adds two numbers together.", call.Doc);
    }

    [Fact]
    public void ConsecutiveDocLines_ReadAsOneBlock()
    {
        var first = Classify("""
            /// Adds two numbers together.
            /// Neither is widened.
            Bind number to summed, given (the number left, the number right):
                Return left + right.
            Done.
            """).First(t => t.Doc is not null);

        Assert.Equal("Adds two numbers together.\nNeither is widened.", first.Doc);
    }

    [Fact]
    public void ADocumentedType_CarriesItsDoc()
    {
        var docs = Classify("""
            /** Holds a point on a grid. */
            Define object point with (the number across, the number down).
            Define here as a new point { the across 1, the down 2 }.
            """).Where(t => t.Doc is not null).ToList();

        Assert.Equal(2, docs.Count);
        Assert.All(docs, d => Assert.Equal("type", SemanticTokenLegend.NameOf(d.Kind)));
        Assert.All(docs, d => Assert.Equal("Holds a point on a grid.", d.Doc));
    }

    [Fact]
    public void AnOrdinaryComment_IsNotDocumentation()
    {
        // ⚠ The carve-out that makes the markers worth having. If this leaked, every note anyone
        // ever left themselves would appear in a hover card.
        Assert.Empty(Classify("""
            // Adds two numbers together.
            /* And a block one too. */
            Bind number to summed, given (the number left, the number right):
                Return left + right.
            Done.
            State cast summed on (1, 2).
            """).Where(t => t.Doc is not null));
    }

    [Fact]
    public void AVariableSharingADocumentedFunctionsName_DoesNotBorrowItsDoc()
    {
        // ⚠ The table is keyed by NAME, so this is the collision it could have. Restricting the
        // lookup to functions and types is what stops a local answering for one — a wrong hover is
        // worse than a missing one.
        var docs = Classify("""
            /// Adds two numbers together.
            Bind number to summed, given (the number left, the number right):
                Return left + right.
            Done.
            Bind number to other, given (the number summed):
                Return summed.
            Done.
            """).Where(t => t.Doc is not null).ToList();

        // The parameter is called `summed` too. Only the function may answer for the name.
        Assert.Single(docs);
        Assert.Equal("function", SemanticTokenLegend.NameOf(docs[0].Kind));
        Assert.True(docs[0].Modifiers.HasFlag(SemanticTokenModifier.Declaration));
    }

    [Fact]
    public void AnUndocumentedProgram_CarriesNoDocAtAll()
    {
        Assert.Empty(Classify("""
            Bind number to summed, given (the number left, the number right):
                Return left + right.
            Done.
            State cast summed on (1, 2).
            """).Where(t => t.Doc is not null));
    }

    // ── Documentation on a book ──────────────────────────────────────────
    //
    // ★ A module needed no new collection at all: it IS an object, so its declaration is an
    // ObjectDefinition and its methods are ordinary BindStatements inside it. Only the emit-side
    // kind test stood between a documented book and an answer.

    private const string DocumentedBook = """
        /// Adds up money, exactly.
        Define object takings with () and module:
            /// One charge on a bill.
            Define object charge with (the text note, the number amount).

            /// Adds every charge together.
            Bind number to total, given (the series of charge charges):
                Define sum as 0.
                For each item in charges, repeat:
                    Increment sum by item's amount.
                Done.
                Return sum.
            Done.
        Done.

        Pull takings.
            Define bill as a series of charge with (a new charge { the note "tea", the amount 2.5 }).
            State cast takings's total on (bill).
        Done.
        """;

    [Fact]
    public void ADocumentedBook_AnswersWhereItIsPulled()
    {
        // `Pull takings.` and `takings's total` — the two places the book's own name appears, and
        // the first thing a reader meets. Selected by KIND rather than by line: a line number here
        // pins the shape of the test fixture, not the behaviour being tested.
        var books = Classify(DocumentedBook)
            .Where(t => t.Kind == SemanticTokenKind.Namespace).ToList();

        Assert.Equal(2, books.Count);
        Assert.All(books, b => Assert.Equal("Adds up money, exactly.", b.Doc));
    }

    [Fact]
    public void ADocumentedBook_AnswersAtAPossessiveUse()
    {
        // `takings's total` — the book half of the possessive carries the BOOK's doc, and the
        // member half carries the METHOD's. Two different cards on one expression, which is the
        // whole reason this is worth a test of its own.
        var all = Classify(DocumentedBook).ToList();
        int callLine = all.Where(t => t.Kind == SemanticTokenKind.Namespace).Max(t => t.Line);
        var line = all.Where(t => t.Line == callLine).ToList();

        var book   = Assert.Single(line.Where(t => t.Kind == SemanticTokenKind.Namespace));
        var method = Assert.Single(line.Where(t => t.Kind == SemanticTokenKind.Function));

        Assert.Equal("Adds up money, exactly.", book.Doc);
        Assert.Equal("Adds every charge together.", method.Doc);
    }

    [Fact]
    public void ADocumentedMemberOfABook_AnswersAtItsDeclaration()
    {
        var docs = Classify(DocumentedBook).Where(t => t.Doc is not null).ToList();

        Assert.Contains(docs, d => d.Doc == "One charge on a bill.");
        Assert.Contains(docs, d => d.Doc == "Adds every charge together.");
    }

    [Fact]
    public void ABookPulledUnderAnAlias_DoesNotAnswerAtTheAlias()
    {
        // ⚠ A KNOWN MISS, pinned so it cannot change in silence. The doc table is keyed by the name
        // as WRITTEN, and an alias is a different word — so hovering it answers nothing rather than
        // answering wrongly, which is the right way round to fail. Closing it means keying on the
        // resolved declaration, which is what the whole name-keyed approximation eventually wants.
        //
        // ★ If this test starts failing, the alias began answering: good news, and this test should
        // become the assertion that it does.
        var aliased = DocumentedBook
            .Replace("Pull takings.", "Pull takings as till.")
            .Replace("cast takings's total", "cast till's total");

        var namespaces = Classify(aliased)
            .Where(t => t.Kind == SemanticTokenKind.Namespace)
            .ToList();

        // The declared name still answers where it is written; the alias does not.
        Assert.Contains(namespaces, t => t.Doc == "Adds up money, exactly.");
        Assert.Equal(2, namespaces.Count(t => t.Doc is null));
    }

    // ── Documentation on a BUNDLED book ──────────────────────────────────
    //
    // ⚠⚠ The bundled books are parsed SEPARATELY by the checker and their statements carry the
    // prelude's own line numbers. Reading a doc position out of the program's token list therefore
    // finds another file's word, or nothing — which is exactly what a documented bundled book used
    // to answer. These read the prelude against its own tokens.

    [Fact]
    public void ABundledBook_AnswersWhereItIsPulled()
    {
        var book = Classify("""
            Pull a book on chance.
                State a random number from 1 to 6.
            Done.
            """).Single(t => t.Kind == SemanticTokenKind.Namespace);

        // ⚠ Asserts THAT it answers, not WHAT it says. Pinning the prelude's prose here would make
        // every reword of a bundled book's documentation a test failure, which is the opposite of
        // what documentation should cost to improve.
        Assert.NotNull(book.Doc);
    }

    [Fact]
    public void ABundledBooksMember_AnswersThroughItsOwner()
    {
        var call = Classify("""
            Pull a book on math.
                State cast math's round on (1.6).
            Done.
            """).Single(t => t.Kind == SemanticTokenKind.Function && t.Doc is not null);

        Assert.NotNull(call.Doc);
    }

    [Fact]
    public void AProgramsOwnNameBeatsABooksAtTheBareSpelling()
    {
        // ★★ THE CASE OWNER-QUALIFIED KEYS EXIST FOR, and it is a WRONG answer rather than a missing
        // one when they are absent. `math` has a member called `round`; so does this program. With a
        // name-keyed table, `math's round` hands back the program's documentation — measured, by
        // removing the owner lookup and watching line 3 below claim to be the local one.
        var docs = Classify("""
            /// MY round, which does nothing at all.
            Bind number to round, given (the number value):
                Return value.
            Done.
            Pull a book on math.
                State cast round on (1).
                State cast math's round on (1.6).
            Done.
            """).Where(t => t.Doc is not null).ToList();

        // The two bare uses are the program's own; the possessive one is the book's. Stated as a
        // DIFFERENCE rather than as the book's text, so rewording `math` cannot break this.
        Assert.Equal(2, docs.Count(d => d.Doc == "MY round, which does nothing at all."));
        Assert.Single(docs.Where(d => d.Doc != "MY round, which does nothing at all."));
    }

    [Fact]
    public void AProgramsOwnDocumentationWinsTheBareName()
    {
        // The prelude is merged AFTER the program and never overwrites it: the file you can edit is
        // the one whose documentation you should see for a name you wrote.
        var mine = Classify("""
            /// MY round, which does nothing at all.
            Bind number to round, given (the number value):
                Return value.
            Done.
            State cast round on (1).
            """).Where(t => t.Doc is not null).ToList();

        Assert.NotEmpty(mine);
        Assert.All(mine, d => Assert.Equal("MY round, which does nothing at all.", d.Doc));
    }
}
