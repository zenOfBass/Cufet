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
public class PipelineRecordObjectTests : PipelineTestBase
{

    /// <summary>
    /// An object used as a series ELEMENT brings its own field types with it.
    /// </summary>
    /// <remarks>
    /// ★ A plain divergence, found sideways — it interprets and would not compile, with no generics
    /// involved at all. `RegisterNestedRecords` recursed into a record's fields but had no case for
    /// an object, so registering `series of holder` never registered the `series of number` inside
    /// holder, and the emitted struct referenced an undeclared `cser_1`.
    ///
    /// ⚠ What hid it: the DISCOVERY pass registers the inner series whenever the body touches it,
    /// which nearly every program does. It takes a field the program never READS — declared in the
    /// struct, reached by nothing else — before the gap is visible. It surfaced while probing
    /// generics, because a filled template happens to have exactly that shape.
    /// </remarks>
    [Fact]
    public void AnObjectWithAnUnreadSeriesField_AsASeriesElement_Compiles()
    {
        const string src = """
            Define object holder with (the series of number items).
            Define many as a series of holder.
            State the number of many.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>A generic METHOD on a module — the shape a book written in Cufet has.</summary>
    /// <remarks>
    /// ★ The compiler resolves a method by looking the member up on its type, and a filling is a
    /// member under its filled name (`unique of number`). Every place that does that lookup had to
    /// agree — the emitter AND the return-type inference, which was still reading the written name
    /// and failed with "'kit' has no method 'unique'" long after the interpreter was happy.
    /// </remarks>
    [Fact]
    public void GenericMethodOnAModule_MatchesInterpreter()
    {
        const string src = """
            Define object kit with () and module:
                Bind series of element to unique, given (the series of element xs):
                    Define out as a series of element.
                    For each x in xs, repeat:
                        Define seen as false.
                        For each y in out, repeat:
                            If y is x:
                                The seen becomes true.
                            Done.
                        Done.
                        If seen is false:
                            Insert x into out.
                        Done.
                    Done.
                    Return out.
                Done.
            Done.

            Pull kit.
                State cast kit's unique on (a series of number with (1, 2, 2, 3, 1)).
                State cast kit's unique on (a series of text with ("a", "b", "a")).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>A FUNCTION that leaves a blank — one body, two fillings, in the generated C.</summary>
    /// <remarks>
    /// ★ The filling is read off the argument, then the function is emitted once per filling under
    /// a name naming it (`first-two of number`) and the template is dropped. Only `Interpret ==
    /// Compile` shows whether the call site reached the right one of the two bodies — the checker
    /// alone would be happy either way.
    /// </remarks>
    [Fact]
    public void GenericFunction_TwoFillings_MatchInterpreter()
    {
        const string src = """
            Bind series of element to first-two, given (the series of element xs):
                Define out as a series of element.
                Insert the first of xs into out.
                Insert item 2 of xs into out.
                Return out.
            Done.

            Bind voidable element to first-or-none, given (the series of element xs):
                If the number of xs is 0:
                    Return void.
                Done.
                Return the first of xs.
            Done.

            Define nums as a series of number with (1, 2, 3).
            Define words as a series of text with ("a", "b", "c").
            State the number of (cast first-two on (nums)).
            State the first of (cast first-two on (words)).
            State (cast first-or-none on (nums)) but void is 0.
            State (cast first-or-none on (words)) but void is "none".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── A definition that leaves a blank ──
    //
    // ★★ Filling happens in the FRONT END: `a stack of number` becomes an ordinary definition named
    // `stack of number`, spliced into the program, and the template itself is DROPPED before either
    // backend runs — the same rule that lets no `StashType` survive. So the compiler never learns
    // what a template is, and this test is really asking whether that stayed true.
    //
    // ⚠ It caught two things a checker-only test could not. The filled-in name contains spaces
    // (`stack of number`), which is what makes it impossible for a writer to collide with and
    // equally impossible for C to accept — the struct, method, getter and setter names all had to
    // learn to flatten it. And the template leaking through emitted a struct for `stack` whose
    // field type was an undefined `element`.

    [Fact]
    public void GenericObject_TwoFillings_MatchInterpreter()
    {
        const string src = """
            Define object stack of element with (the series of element items):
                Bind void to push, given (the element value):
                    Insert value into one's items.
                Done.
                Bind number to how-many:
                    Return the number of one's items.
                Done.
            Done.

            Define counts as a new stack of number { the items a series of number }.
            Cast push on (counts, 5).
            Cast push on (counts, 7).

            Define names as a new stack of text { the items a series of text }.
            Cast push on (names, "alice").

            State cast how-many on (counts).
            State cast how-many on (names).
            State the first of names's items.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_PositionalAccess_MatchesInterpreter()
    {
        const string src = "Define point as a record with (3, 4). State the first of point. State the second of point.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_FieldsCanonicalPrintOrder_MatchesInterpreter()
    {
        // Fields written in non-sorted order still print sorted (canonical), matching the interpreter.
        const string src = "State a record with (the name \"Zed\", the age 9, the city \"Tulsa\").";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_NamedFieldSet_MatchesInterpreter()
    {
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            the age of alice becomes 31.
            State alice.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_PositionalFieldSet_MatchesInterpreter()
    {
        const string src = "Define point as a record with (3, 4). the first of point becomes 10. State point.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_ValueSemantics_DefineCopies_MatchesInterpreter()
    {
        // Define copies (value semantics): mutating the copy leaves the original untouched.
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            Define bob as alice.
            the name of bob becomes "Bob".
            the age of bob becomes 99.
            State alice.
            State bob.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_Nested_MatchesInterpreter()
    {
        // A record field that is itself a record — deep-copied inline (value struct).
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            Define row as a record with (the person alice, the score 95).
            State row.
            State the name of the person of row.
            State the score of row.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_WithSeriesField_MatchesInterpreter()
    {
        // A record holding a series (reference type) — the struct carries a CufetSeries*.
        const string src = """
            Pull a rabbit.
                Define team as a record with (the label "A", the scores a series of number with (10, 20, 30)).
                State team.
                State the first of the scores of team.
                Insert 40 into the scores of team.
                State team.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_TextFieldEquality_MatchesInterpreter()
    {
        // Text-as-stored-data: text field compared by value (strcmp), not pointer.
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            If the name of alice is "Alice", state "match". Otherwise, state "no".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_ReturnedFromFunction_MatchesInterpreter()
    {
        // A function that builds and returns a record (record return type, by value).
        const string src = """
            Bind the record result with (the text name, the number age) to make-person, given (the text n, the number years):
                return a record with (the name n, the age years).
            Done.
            Define p as cast make-person on ("Alice", 30).
            State p.
            State the age of p.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_ReadOnlyParam_MatchesInterpreter()
    {
        // A function reading (not mutating) a record param — by-value matches the oracle.
        const string src = """
            Bind number to get-age, given (the record p with (the text name, the number age)):
                return the age of p.
            Done.
            Define alice as a record with (the name "Alice", the age 42).
            State cast get-age on (alice).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Series_OfRecords_MatchesInterpreter()
    {
        // Series of records (slice 8): value-type elements copy on insert (binding is binding),
        // remove-by-value uses value equality (same as `is`), and series equality is element-wise.
        const string src = """
            Define people as a series with (a record with (the name "Alice", the age 30), a record with (the name "Bob", the age 25)).
            State people.
            Insert a record with (the name "Carol", the age 40) into people.
            State the number of people.
            For each p in people, repeat:
                State the name of p.
            Done.
            Remove a record with (the name "Bob", the age 25) from people.
            State the number of people.
            Define pa as a series with (a record with (the x 1)).
            Define pb as a series with (a record with (the x 1)).
            If pa is pb, state "eq". Otherwise, state "neq".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Series_OfRecords_InsertCopies_MatchesInterpreter()
    {
        // The element is COPIED into the series (value semantics) — mutating the original record
        // afterward does not change the stored element. Interpreter and compiler both copy now.
        const string src = """
            Define r as a record with (the x 1).
            Define s as a series with (r).
            The x of r becomes 99.
            State s.
            State r.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_MutatedParam_NoLeak_MatchesInterpreter()
    {
        // Binding is binding: a record arg is copied, so a function mutating its param
        // does NOT change the caller's record — and compiled matches interpreted exactly
        // (both copy). This was the pre-fix divergence; it's now locked shut.
        const string src = """
            Bind the record result with (the text name, the number age) to make-older, given (the record p with (the text name, the number age)):
                the age of p becomes the age of p + 1.
                return p.
            Done.
            Define alice as a record with (the name "Alice", the age 30).
            Define older as cast make-older on (alice).
            State alice.
            State older.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Series_MutatedParam_Shares_MatchesInterpreter()
    {
        // The region-model flip side: a series arg is shared, so mutating it inside a
        // function IS visible to the caller — compiled and interpreted agree on that too.
        const string src = """
            Bind void to grow, given (the series of number s):
                Insert 99 into s.
            Done.
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                Cast grow on (xs).
                State xs.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 5B: objects (nominal value structs + methods, direct dispatch) ──

    [Fact]
    public void Object_ConstructAccessAndPrint_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Define alice as a new person { the name "Alice", the age 30 }.
            State alice.
            State alice's name.
            State the age of alice.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_VoidMutatingMethod_MatchesInterpreter()
    {
        // `one's age becomes ...` mutates the receiver in place (receiver passed by pointer).
        const string src = """
            Define object person with (the text name, the number age):
                Bind void to birthday:
                    one's age becomes one's age + 1.
                Done.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            Cast birthday on alice.
            State the age of alice.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_ValueReturningMethod_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age):
                Bind number to doubled-age:
                    return one's age * 2.
                Done.
            Done.
            Define alice as a new person { the name "Alice", the age 21 }.
            State cast alice's doubled-age.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_MethodWithArgs_MatchesInterpreter()
    {
        // Method dispatch with extra args: receiver first, params follow.
        const string src = """
            Define object person with (the text name, the number age):
                Bind number to age-in, given (the number years):
                    return one's age + years.
                Done.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            State cast age-in on (alice, 5).
            State cast alice's age-in on (10).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_ValueSemantics_DefineCopies_MatchesInterpreter()
    {
        // Objects are value structs: a copy is fully independent (methods on one don't
        // touch the other), matching the interpreter's deep-copy on Define.
        const string src = """
            Define object person with (the text name, the number age):
                Bind void to birthday:
                    one's age becomes one's age + 1.
                Done.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            Define bob as alice.
            the name of bob becomes "Bob".
            Cast birthday on bob.
            State alice.
            State bob.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_MutatedParam_NoLeak_MatchesInterpreter()
    {
        // Binding is binding: an object arg is copied, so a function mutating its param
        // (via a method) does NOT change the caller's object. Compiled == interpreted.
        const string src = """
            Define object person with (the text name, the number age):
                Bind void to birthday:
                    one's age becomes one's age + 1.
                Done.
            Done.
            Bind void to age-it, given (the person p):
                Cast birthday on p.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            Cast age-it on (alice).
            State the age of alice.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_AsFunctionReturn_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Bind the person to make-alice:
                return a new person { the name "Alice", the age 30 }.
            Done.
            Define alice as cast make-alice.
            State alice.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_AsRecordField_MatchesInterpreter()
    {
        // A record whose field is an object (value struct nested in a value struct).
        const string src = """
            Define object person with (the text name, the number age).
            Define alice as a new person { the name "Alice", the age 30 }.
            Define row as a record with (the who alice, the score 95).
            State row.
            State the age of the who of row.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_Embedding_MatchesInterpreter()
    {
        // Composition-with-promotion: promoted field/method access, embed handle, promoted
        // set, and print (own fields then embedded object) — all bit-identical.
        const string src = """
            Define object animal with (the text name, the number legs):
                Bind text to describe:
                    return one's name.
                Done.
            Done.
            Define object dog with (the number age) and as an animal.
            Define rex as a new dog { the age 3, the name "Rex", the legs 4 }.
            State rex.
            State the name of rex.
            State rex's name.
            State the age of rex.
            State cast rex's describe.
            State the animal of rex.
            the name of rex becomes "Max".
            State rex.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 5B object core: equality, unto, constructors, getters/setters ──

    [Fact]
    public void RecordEquality_Structural_MatchesInterpreter()
    {
        // Structural: field order at construction doesn't matter; series fields element-wise.
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            Define alice2 as a record with (the age 30, the name "Alice").
            Define bob as a record with (the name "Bob", the age 30).
            If alice is alice2, state "eq". Otherwise, state "ne".
            If alice is not bob, state "ne2". Otherwise, state "eq2".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ObjectEquality_Nominal_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Define p1 as a new person { the name "Alice", the age 30 }.
            Define p2 as a new person { the name "Alice", the age 30 }.
            Define p3 as a new person { the name "Alice", the age 31 }.
            If p1 is p2, state "eq". Otherwise, state "ne".
            If p1 is p3, state "eq2". Otherwise, state "ne2".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void RecordEquality_SeriesField_MatchesInterpreter()
    {
        const string src = """
            Define t1 as a record with (the items a series of number with (1, 2, 3)).
            Define t2 as a record with (the items a series of number with (1, 2, 3)).
            Define t3 as a record with (the items a series of number with (1, 2, 4)).
            If t1 is t2, state "eq". Otherwise, state "ne".
            If t1 is t3, state "eq2". Otherwise, state "ne2".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_UntoMethods_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Bind void to birthday unto person:
                one's age becomes one's age + 1.
            Done.
            Bind number to age-plus unto person, given (the number d):
                return one's age + d.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            Cast birthday on alice.
            State the age of alice.
            State cast age-plus on (alice, 100).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_NamedConstructor_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Bind making a person to teen, given (the text n):
                return a new person { the name n, the age 13 }.
            Done.
            Define alice as cast teen on ("Alice").
            State alice.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_GettersSetters_MatchesInterpreter()
    {
        // Getter computes (no stored field); setter intercepts + clamps; self-write bypass.
        const string src = """
            Define object circle with (the number radius):
                Get area as number:
                    return one's radius * one's radius * 3.
                Done.
                Set radius given (the number r):
                    If r < 0, one's radius becomes 0.
                    Otherwise, one's radius becomes r.
                Done.
            Done.
            Define c as a new circle { the radius 2 }.
            State c's area.
            State the area of c.
            c's radius becomes 5.
            State c's radius.
            State c's area.
            c's radius becomes -3.
            State c's radius.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_PositionalAccessOnNamedFields_ThrowsCleanly()
    {
        // Named-field objects have no positional slots — the interpreter errors, and the
        // compiler must reject cleanly (not emit broken C).
        const string src = """
            Define object person with (the text name, the number age).
            Define alice as a new person { the name "Alice", the age 30 }.
            State the first of alice.
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        try { new TypeChecker().Check(program); } catch (TypeException) { return; } // TC may reject first
        Assert.Throws<CompilerException>(() => new CodeGenerator().Generate(program));
    }

    [Fact]
    public void Object_TransitiveEmbedding_MatchesInterpreter()
    {
        // Multi-level embedding (employee → person → address): promoted access + set reach
        // through two levels; equality recurses the whole chain.
        const string src = """
            Define object address with (the text city).
            Define object person with (the text name) and as an address.
            Define object employee with (the number salary) and as a person.
            Define e as a new employee { the salary 100, the name "Alice", the city "Tulsa" }.
            State e.
            State the city of e.
            the city of e becomes "Norman".
            State the city of e.
            Define e2 as a new employee { the salary 100, the name "Alice", the city "Norman" }.
            If e is e2, state "eq". Otherwise, state "ne".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_Interface_ConformanceWithoutDispatch_Compiles()
    {
        // Interface conformance needs no representation change — a conforming object is an ordinary
        // value struct. Declaring conformance (and never calling through the interface) compiles and
        // runs; the conformer's method is a normal direct-dispatch method. (Was: a deferred throw.)
        const string src = """
            Define greeter as an interface for the void function greet.
            Define object robot with (the text id) and greeter:
                Bind void to greet:
                    State one's id.
                Done.
            Done.
            Define r as a new robot { the id "R2" }.
            Cast r's greet on ().
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 5C: voidable (uniform tagged struct cvd_N { int has; T val; }) ──

    [Fact]
    public void Voidable_Number_MatchesInterpreter()
    {
        // Present → value, absent → "void"; is void / is not void; but void is (value / default).
        const string src = """
            Bind voidable number to half-if-even, given (the number n):
                If n % 2 is 0, return n / 2.
                return void.
            Done.
            Define x as cast half-if-even on (4).
            Define y as cast half-if-even on (3).
            State x.
            State y.
            If x is void, state "x-void". Otherwise, state "x-present".
            If y is void, state "y-void". Otherwise, state "y-present".
            If x is not void, state "x-notvoid". Otherwise, state "x-isvoid".
            State x but void is 0.
            State y but void is 99.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// A filled generic WRITTEN as a type — a `Define`'s annotation, a parameter, a return type —
    /// holds the oracle, not just the checker.
    /// </summary>
    /// <remarks>
    /// ★★ Every shape here was refused outright before this test existed, by the front end both
    /// backends share, so the oracle could not have caught any of it: neither side ran. That is the
    /// shape of blind spot worth naming — a program that does not reach either backend is not a
    /// program the two backends can be compared on.
    ///
    /// ⚠ Nothing in the corpus wrote a USER-DEFINED generic in an annotation. Every generic
    /// annotation anywhere in tests/ or examples/ is `series of number` — a built-in, which leads
    /// with its own keyword and so never met the fault.
    /// </remarks>
    [Fact]
    public void AFilledGeneric_WrittenAsAType_AgreesOnBothBackends()
    {
        const string src = """
            Define object stack of element with (the series of element items):
                Bind void to push, given (the element value):
                    Insert value into one's items.
                Done.
                Bind number to how-many:
                    Return the number of one's items.
                Done.
            Done.

            Bind stack of number to make-counts:
                Return a new stack of number { the items a series of number }.
            Done.

            Bind number to tally-up, given (the stack of number box):
                Return cast how-many on (box).
            Done.

            Define the stack of number counts as cast make-counts on ().
            Cast push on (counts, 5).
            Cast push on (counts, 7).
            State cast tally-up on (counts).

            Define the stack of text names as a new stack of text { the items a series of text }.
            Cast push on (names, "Ada").
            State cast how-many on (names).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// `unto` a template and `unto` one filling, held to the oracle.
    /// </summary>
    /// <remarks>
    /// ★ Neither reaches the backends as anything unusual: a template's `unto` member is merged
    /// into the template and substituted per filling, and a filling's is merged into that filling
    /// when it is made. By the time either backend sees the program, both are ordinary methods on
    /// ordinary objects — which is the same move monomorphization already makes, and the reason
    /// this needed no compiler change at all.
    /// </remarks>
    [Fact]
    public void UntoOnATemplateAndOnOneFilling_AgreeOnBothBackends()
    {
        const string src = """
            Define object stack of element with (the series of element items):
                Bind void to push, given (the element value):
                    Insert value into one's items.
                Done.
            Done.

            Bind number to counted unto stack:
                Return the number of one's items.
            Done.

            Bind number to total unto stack of number:
                Define the sum as 0.
                For each item in one's items, repeat:
                    The sum becomes the sum + item.
                Done.
                Return the sum.
            Done.

            Define counts as a new stack of number { the items a series of number }.
            Cast push on (counts, 5).
            Cast push on (counts, 7).

            Define names as a new stack of text { the items a series of text }.
            Cast push on (names, "Ada").

            State cast counted on (counts).
            State cast total on (counts).
            State cast counted on (names).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// A record and a bit pattern as map keys, held to the oracle.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ This is the test that matters most for this rule, because the two backends do NOT agree
    /// by construction. The interpreter's map is a Dictionary — it HASHES the key, then compares.
    /// The compiler's map is a linear scan calling its own `_eq`. Nothing whatsoever makes a hash
    /// and a scan answer alike; they agree only if the hash is kept in step with the equality it
    /// is paired with, and when it is not the interpreter finds NOTHING while the compiler finds
    /// the entry. No error either side — just two different answers.
    ///
    /// ★ Every lookup below deliberately uses a value that is EQUAL to the stored key without
    /// being the same one: a second record with the same contents, named fields written in the
    /// other order, and a bit pattern written in a different base.
    /// </remarks>
    [Fact]
    public void RecordAndBitPatternMapKeys_AgreeOnBothBackends()
    {
        const string src = """
            Define spot as a record with (1, 2).
            Define grid as a map with (spot : "here").
            Define other as a record with (1, 2).
            State the entry for other in grid.
            State grid has a key for other.

            Define cell as a record with (the row 3, the col 4).
            Define board as a map with (cell : "corner").
            Define flipped as a record with (the col 4, the row 3).
            State the entry for flipped in board.

            Define flags as a map with (0b1010 : "ten").
            State the entry for 0xA in flags.

            Define tag as a record with ("north", 3, true).
            Define tags as a map with (tag : "found").
            Define same as a record with ("north", 3, true).
            State the entry for same in tags.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// A closed union of empty objects, used as an enumeration, on both backends.
    /// </summary>
    /// <remarks>
    /// ★ Neither backend learns anything new here: an object with no fields was always legal, and
    /// `Judge` over a closed union already dispatched on a tag. What changed is only that the
    /// empty shape and the empty literal no longer have to be written out — so this test is really
    /// asking whether the SHORTHAND lowers to the same program the long form did.
    /// </remarks>
    [Fact]
    public void AnEnumerationOfEmptyObjects_AgreesOnBothBackends()
    {
        const string src = """
            Define object red.
            Define object green.
            Define object blue.

            Bind text to name-of, given (the (red or green or blue) light):
                Judge light, where it is:
                    A red, return "red".
                    A green, return "green".
                    A blue, return "blue".
                Done.
            Done.

            State cast name-of on (a new red).
            State cast name-of on (a new green).
            State cast name-of on (a new blue).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// A nested `Judge` whose inner arm uses `it` — hand-written, no dispatch involved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ A COMPILER bug this feature uncovered, and older than it. `Judge` binds its subject to
    /// `it`, so nesting two rebinds the name — but the compiler's narrowing table is keyed by name
    /// and COMPOSES accesses, which is right for one binding narrowed twice and wrong here. The
    /// outer arm's `.val.c0` was prefixed onto the inner arm's, emitting `(cv_it).val.c0.val.c0`
    /// and reaching for a member of a type that has none. gcc refused the program.
    /// </para>
    /// <para>
    /// ★★ Only ever visible as a DIVERGENCE. The interpreter shadows `it` properly, so it runs
    /// this correctly and no interpreter test could go red — which is what the oracle is for. The
    /// inner arm has to USE `it`: reading only a local bound from the outer arm compiles fine, and
    /// that is why the existing nested-Judge coverage never caught it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ANestedJudgeUsingIt_AgreesOnBothBackends()
    {
        const string src = """
            Define object num-node with (the number value).
            Define object add-node with (the number left, the number right).
            Define object int-type.
            Define object text-type.

            Bind text to want-int, given (the int-type w): Return "int". Done.
            Bind text to want-text, given (the text-type w): Return "text". Done.

            Bind text to check, given (the (num-node or add-node) node, the (int-type or text-type) want):
                Judge node, where it is:
                    A num-node:
                        Define held as it.
                        Judge want, where it is:
                            A int-type, return "num/{cast want-int on (it)} {held's value}".
                            A text-type, return "num/{cast want-text on (it)}".
                        Done.
                    Done.
                    A add-node:
                        Judge want, where it is:
                            A int-type, return "add/{cast want-int on (it)}".
                            A text-type, return "add/{cast want-text on (it)}".
                        Done.
                    Done.
                Done.
            Done.

            State cast check on (a new num-node { the value 7 }, a new int-type).
            State cast check on (a new num-node { the value 7 }, a new text-type).
            State cast check on (a new add-node { the left 1, the right 2 }, a new int-type).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// Dispatch on TWO arguments' types, on both backends.
    /// </summary>
    /// <remarks>
    /// ★ Neither argument's type is known at the call — both are catalogue elements — so the
    /// version comes from two tags read in turn, which the front end lowers to nested `Judge`s.
    /// ⚠ Each nested `Judge` rebinds `it`, so the outer argument's narrowed value is bound to a
    /// local before descending. This test is what would catch that binding being dropped: the
    /// versions declare narrow types, and only the bound local still carries one.
    /// </remarks>
    [Fact]
    public void DispatchOnTwoArguments_AgreesOnBothBackends()
    {
        const string src = """
            Define object num-lit with (the number value).
            Define object text-lit with (the text value).
            Define object int-type.
            Define object text-type.

            Bind text to check, given (the num-lit node, the int-type want):
                Return "num/int {node's value}".
            Done.

            Bind text to check, given (the num-lit node, the text-type want):
                Return "want text, got {node's value}".
            Done.

            Bind text to check, given (the text-lit node, the int-type want):
                Return "want number, got {node's value}".
            Done.

            Bind text to check, given (the text-lit node, the text-type want):
                Return "text/text {node's value}".
            Done.

            Define nodes as a catalogue of (num-lit or text-lit) with (
                a new num-lit { the value 7 }, a new text-lit { the value "x" }).
            Define wants as a catalogue of (int-type or text-type) with (
                a new int-type, a new text-type).

            For each n in nodes, repeat:
                For each w in wants, repeat:
                    State cast check on (n, w).
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// Dispatch by a `when` condition, composed with dispatch on type, on both backends.
    /// </summary>
    /// <remarks>
    /// ★ The conditions are shown pairwise disjoint at check time, so the `If` chain the front end
    /// generates gives the same answer in any order — which is what makes lowering order-independent
    /// dispatch to an ordinary ordered chain honest.
    /// ⚠ The condition is rewritten onto the narrowed subject before it reaches the chain: inside
    /// the generated `Judge` arm the parameter still holds the whole union.
    /// </remarks>
    [Fact]
    public void DispatchByCondition_AgreesOnBothBackends()
    {
        const string src = """
            Define object num-node with (the number value).
            Define object add-node with (the number left, the number right).

            Bind text to describe, given (the num-node node) when node's value is 0:
                Return "zero".
            Done.

            Bind text to describe, given (the num-node node):
                Return "number {node's value}".
            Done.

            Bind text to describe, given (the add-node node) when node's left is 0 xor node's right is 0:
                Return "one-identity".
            Done.

            Bind text to describe, given (the add-node node):
                Return "sum".
            Done.

            Define nodes as a catalogue of (num-node or add-node) with (
                a new num-node { the value 0 },
                a new num-node { the value 7 },
                a new add-node { the left 0, the right 3 },
                a new add-node { the left 0, the right 0 },
                a new add-node { the left 1, the right 3 }).

            For each n in nodes, repeat:
                State cast describe on (n).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// Dispatch on one argument's type, on both backends.
    /// </summary>
    /// <remarks>
    /// ★ Neither backend learns dispatch exists. The front end renames each version and builds a
    /// dispatcher whose body is an ordinary `Judge`, so what reaches the compiler is functions and
    /// a tag switch it has emitted since closed unions shipped.
    /// ⚠ The `For each` is the half that matters: every element is statically the union, so the
    /// version is chosen from the tag the value carries rather than from anything at the call site.
    /// </remarks>
    [Fact]
    public void DispatchOnArgumentType_AgreesOnBothBackends()
    {
        const string src = """
            Define object num-node with (the number value).
            Define object add-node with (the number left, the number right).
            Define object neg-node with (the number operand).

            Bind number to eval, given (the num-node node):
                Return node's value.
            Done.

            Bind number to eval, given (the add-node node):
                Return node's left + node's right.
            Done.

            Bind number to eval, given (the neg-node node):
                Return 0 - node's operand.
            Done.

            State cast eval on (a new add-node { the left 3, the right 4 }).

            Define nodes as a catalogue of (num-node or add-node or neg-node) with (
                a new num-node { the value 1 },
                a new add-node { the left 2, the right 3 },
                a new neg-node { the operand 4 }).
            For each n in nodes, repeat:
                State cast eval on (n).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// Named arguments at a call site, on both backends.
    /// </summary>
    /// <remarks>
    /// ★ Neither backend learns them. The checker puts the call in the order the callee declares
    /// and empties the named list, so what reaches the compiler is the ordinary positional call it
    /// has always emitted. What this asks is whether the reorder lands on the same program.
    /// ⚠ Division, not multiplication: swapping the arguments of a commutative operation prints
    /// the same thing either way, so it would agree even with the reorder broken on both sides.
    /// </remarks>
    [Fact]
    public void NamedArguments_AgreeOnBothBackends()
    {
        const string src = """
            Define object box with (the number w):
                Bind number to scaled, given (the number factor, the number bias):
                    Return one's w * factor + bias.
                Done.
            Done.

            Bind number to take-half, given (the number whole, the number divisor):
                Return whole / divisor.
            Done.

            State cast take-half on (the divisor 2, the whole 16).
            State cast take-half on (16, the divisor 2).

            Define crate as a new box { the w 2 }.
            State cast crate's scaled on (the bias 1, the factor 10).
            State cast scaled on (crate, the bias 1, the factor 10).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// Mixed-type operator overloads, in both orders, on both backends.
    /// </summary>
    /// <remarks>
    /// ⚠ The compiler's C symbol for an overload used to be built from ONE operand type
    /// (`cop_vec2_star`). With `vec2 * number` and `number * vec2` both declarable that emits the
    /// same symbol twice, so the name carries both sides now. This test is what would catch a
    /// regression there: `vec2 * number` and `vec2 * vec2` SHARE a left type and operator, which
    /// is exactly the pair a left-name-only symbol would collapse into one function.
    /// </remarks>
    [Fact]
    public void MixedTypeOperatorOverloads_AgreeOnBothBackends()
    {
        const string src = """
            Define object vec2 with (the number x, the number y).

            Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):
                Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.
            Done.

            Bind overloading *, given (the lhs is a vec2, the rhs is a number):
                Return a new vec2 { the x lhs's x * rhs, the y lhs's y * rhs }.
            Done.

            Bind overloading *, given (the lhs is a number, the rhs is a vec2):
                Return a new vec2 { the x lhs * rhs's x, the y lhs * rhs's y }.
            Done.

            Bind overloading *, given (the lhs is a vec2, the rhs is a vec2):
                Return lhs's x * rhs's x + lhs's y * rhs's y.
            Done.

            Define u as a new vec2 { the x 1, the y 2 }.
            Define w as a new vec2 { the x 3, the y 4 }.
            State u + w.
            State u * 3.
            State 10 * u.
            State u * w.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>Scaling a matrix by a number, on both backends.</summary>
    /// <remarks>
    /// ⚠ The compiler had to grow a SEPARATE path for this, not a variant of the existing one:
    /// `matrix + matrix` routes through a failable struct and a check-goto because a dimension
    /// mismatch is a failure, and scaling has none — so it is a plain expression returning a plain
    /// `CufetMatrix*`. Sending it through the fallible machinery would have compiled and then
    /// demanded a `Try` the front end never asked for.
    /// </remarks>
    [Fact]
    public void ScalingAMatrix_AgreesOnBothBackends()
    {
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                State m * 2.
                State 3 * m.
                Define doubled as m * 2.
                State doubled * 2.
                Try to:
                    State m * m.
                Done.
                In case of failure:
                    State the message of the failure.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
