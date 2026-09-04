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
public class PipelineBooksTests : PipelineTestBase
{

    [Fact]
    public void Book_Math_Constants_BakedExact()
    {
        // pi/e are baked from (decimal)Math.PI / (decimal)Math.E in the compiler (itself .NET), so
        // the CufetDec is bit-identical to the interpreter's stored constant.
        const string src = """
            Pull a book on math.
                State math's pi.
                State math's e.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Book_Math_AliasedPull()
    {
        // `Pull a book on math as the m.` — the alias resolves book-member dispatch just the same.
        const string src = """
            Pull a book on math as the m.
                State m's floor of 3.7.
                State m's pi.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Sort_Numbers_AscendingAndReverse()
    {
        const string src = """
            Define nums as a series with (3, 1, 4, 1, 5, 9, 2, 6).
            State nums sorted.
            State nums sorted in reverse.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Sort_Text_OrdinalOrder()
    {
        const string src = """
            Define words as a series of text with ("banana", "apple", "cherry", "apple").
            State words sorted.
            State words sorted in reverse.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Sort_ByField_Stable()
    {
        // Stability proof: Bob and Cy both have age 30; sorted by age they keep insertion order
        // (Bob before Cy). A stable sort (not qsort) is required to match the interpreter's OrderBy.
        const string src = """
            Define party as a series of records like (the text name, the number age).
            Insert a record with (the name "Bob", the age 30) into party.
            Insert a record with (the name "Ann", the age 25) into party.
            Insert a record with (the name "Cy", the age 30) into party.
            State party sorted by age.
            State party sorted by name in reverse.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── math's transcendentals — written in Cufet, computed on the decimal ──
    // ★★ THE libm CAVEAT IS RETIRED. These used to be double-backed, which meant `power` with a
    // fractional exponent was last-ULP platform-dependent: .NET's Math.Pow IS the platform libm,
    // so inputs like 2^2.65 differed by ±1 in the 15th significant digit between ucrt
    // (.NET-on-Windows) and glibc/mingw, and that family could be documented but never asserted.
    // square-root/log/exp/power are now Cufet in the math book's own layer, computed on CufetDec
    // itself — so both backends run the SAME algorithm on the SAME arithmetic and agree by
    // construction rather than by sharing a library. The formerly-divergent family is asserted
    // below, which is the whole proof.

    [Fact]
    public void Book_Math_Sqrt_BridgeOracleMatch()
    {
        // 130 sqrt values incl. fractions — every one exercises the 15-sig-digit bridge. sqrt is
        // IEEE-correctly-rounded, so any mismatch would be a BRIDGE bug, not libm.
        const string src = """
            Pull a book on math.
                For each n in the range 1 to 60, repeat:
                    State (math's square-root of n) but void is -1.
                    State (math's square-root of (n / 7)) but void is -1.
                Done.
                State (math's square-root of 2) but void is -1.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Book_Math_Log_BridgeOracleMatch()
    {
        const string src = """
            Pull a book on math.
                For each n in the range 1 to 60, repeat:
                    State (math's log of n) but void is -1.
                    State (math's log of (n / 7)) but void is -1.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Book_Math_Power_VerifiedFamilies()
    {
        // Integer/exact powers + the corpus-verified cube and square-root-via-pow families.
        // (2^fractional is the measured ±1-ULP libm-divergent family — documented, not asserted.)
        const string src = """
            Pull a book on math.
                State (math's power of (2, 10)) but void is -1.
                State (math's power of (10, 28)) but void is -1.
                For each n in the range 1 to 40, repeat:
                    State (math's power of (n / 10, 3)) but void is -1.
                    State (math's power of (n, 0.5)) but void is -1.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Book_Math_Power_FormerlyLibmDivergentFamily()
    {
        // ★ These are the inputs the double-backed implementation could not assert: a fractional
        // exponent went through the platform's own pow, so the answer's last digit belonged to
        // whichever libm was linked. Nothing here touches a double any more.
        const string src = """
            Pull a book on math.
                State (math's power of (2, 2.65)) but void is -1.
                For each n in the range 1 to 30, repeat:
                    State (math's power of (2, n / 10)) but void is -1.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Book_Math_Transcendental_VoidPaths()
    {
        // MathPartial semantics: NaN/±Inf → void (sqrt of negative, log of 0/negative, pow NaN),
        // and decimal-OVERFLOW → void (pow(10,1000) is double-inf; pow(10,30) is a FINITE double
        // that overflows decimal in the conversion — the exp>96 path). All flow as voidable number.
        const string src = """
            Pull a book on math.
                State (math's square-root of -1) but void is -999.
                State (math's log of 0) but void is -999.
                State (math's log of -1) but void is -999.
                State (math's power of (-1, 0.5)) but void is -999.
                State (math's power of (10, 1000)) but void is -999.
                State (math's power of (10, 30)) but void is -999.
                State (math's power of (10, 28)) but void is -999.
                Define r as math's square-root of 16.
                If r is not void, State "sixteen has a root".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Arc 1C: collections aggregates (mechanical — reductions on the compiled series model) ──
    // minimum/maximum/average → voidable number (void on empty, reuses 5C); min/max keep the first
    // of ties; average = sequential exact-decimal sum then ONE divide (LINQ Sum semantics — no
    // double bridge, so fractional averages are EXACT). unique = element-type-preserving first-
    // occurrence dedup via per-type value equality (the series-of-T payoff).

    [Fact]
    public void Collections_MinMaxAverage_ExactDecimal()
    {
        // average of (0.1, 0.2, 0.3) is EXACTLY 0.2 (software decimal — no float drift), and the
        // repeating division (100+3+3)/3 matches the interpreter's 28-digit decimal quotient.
        const string src = """
            Pull a book on collections.
                Define xs as a series with (3, 1, 4, 1, 5, 9, 2, 6).
                State (cast collections's minimum of (xs)) but void is -1.
                State (cast collections's maximum of (xs)) but void is -1.
                State (cast collections's average of (xs)) but void is -1.
                Define fr as a series with (0.1, 0.2, 0.3).
                State (cast collections's average of (fr)) but void is -1.
                Define rep as a series with (100, 3, 3).
                State (cast collections's average of (rep)) but void is -1.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Collections_Aggregates_VoidOnEmpty()
    {
        const string src = """
            Pull a book on collections.
                Define empty as a series of number with ().
                State (cast collections's minimum of (empty)) but void is -999.
                State (cast collections's maximum of (empty)) but void is -999.
                State (cast collections's average of (empty)) but void is -999.
                Define r as cast collections's average of (empty).
                If r is void, State "empty average is void".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Collections_Unique_FirstOccurrenceAcrossTypes()
    {
        // Numbers, text, and records (structural equality — the two Bob-30s dedup; Ann-25 and
        // Ann-26 stay distinct). First-occurrence order preserved in every case.
        const string src = """
            Pull a book on collections.
                Define nq as a series with (3, 1, 3, 2, 1).
                State cast collections's unique of (nq).
                Define tq as a series of text with ("b", "a", "b", "c", "a").
                State cast collections's unique of (tq).
                Define party as a series of records like (the text name, the number age).
                Insert a record with (the name "Bob", the age 30) into party.
                Insert a record with (the name "Ann", the age 25) into party.
                Insert a record with (the name "Bob", the age 30) into party.
                Insert a record with (the name "Ann", the age 26) into party.
                State cast collections's unique of (party).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Collections_Unique_CufetLayer_AliasAndHoistedBind()
    {
        // ★ `unique` lives in the book's Cufet layer (Prelude/collections.cufe) — the native copy
        // is deleted, so this compiles through ordinary method emission or not at all. The Bind
        // inside the pull body is the hoisted-function case: its body emits outside the pull's C
        // block, which is why the layer receiver is a compound literal rather than the binding.
        const string src = """
            Pull a book on collections as c.
                Define nq as a series with (3, 1, 3, 2, 1).
                State cast c's unique on (nq).
                Bind series of text to dedupe, given (the series of text names):
                    Return cast c's unique on (names).
                Done.
                State cast dedupe on (a series of text with ("b", "a", "b")).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void Collections_Unique_MemorySafety_ASan()
    {
        // unique builds a NEW arena series (like sorted) — must free cleanly at scope exit.
        const string src = """
            Pull a book on collections.
                Define xs as a series with (5, 3, 5, 1, 3, 5, 2).
                Define u as cast collections's unique of (xs).
                State u.
            Done.
            """;
        Assert.Equal(Interpret(src), CompileSanitized(src));
    }

    // ── Arc 1D: matrix (the new-type capstone of the collections book) ──
    // CufetMatrix = arena reference type (shared on assign, matching the interpreter — a write
    // through one name is visible through all of them). All arithmetic is EXACT CufetDec. Dimension
    // mismatch is a Cufet
    // FAILURE (category "dimension-mismatch") the typechecker requires handling for. Printing uses
    // the FormatMatrix format added to BOTH backends this slice: matrix((1, 2), (3, 4)).

    [Fact]
    public void Matrix_LiteralSizedAccess_OracleMatch()
    {
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                State m.
                State the rows of m.
                State the columns of m.
                State the item at (1, 2) of m.
                State the item at (2, 1) of m.
                Define g as a matrix with 2 by 3 filled with 7.
                State g.
                Define z as a matrix with 2 by 2.
                State z.
                Define fr as a matrix with ((0.1, 0.2), (1.5, -2.75)).
                State fr.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Matrix_Arithmetic_ExactDecimal_InclNonSquareMultiply()
    {
        // add/sub with fractional elements (exact decimal), square multiply, and the 2×3 · 3×2
        // real matrix product — accumulation order replicates the interpreter, so bit-identical.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                Define fr as a matrix with ((0.1, 0.2), (1.5, -2.75)).
                Try to:
                    Define s as m + fr.
                    State s.
                    Define d as m - fr.
                    State d.
                    Define p as m * m.
                    State p.
                    Define ns1 as a matrix with ((1, 2, 3), (4, 5, 6)).
                    Define ns2 as a matrix with ((7, 8), (9, 10), (11, 12)).
                    State ns1 * ns2.
                Done.
                In case of failure:
                    State "unexpected".
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Matrix_DimensionMismatch_IsCufetFailure()
    {
        // Mismatched add and non-conforming multiply → failures with the interpreter's exact
        // deterministic messages + the "dimension-mismatch" category, caught by Try.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                Define wide as a matrix with ((1, 2, 3), (4, 5, 6)).
                Try to:
                    Define oops as m + wide.
                    State "no failure".
                Done.
                In case of failure:
                    State the message of the failure.
                    State the category of the failure but void is "none".
                Done.
                Try to:
                    Define oops2 as wide * wide.
                    State "no failure".
                Done.
                In case of failure:
                    State the message of the failure.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Matrix_TransposeAndComposition()
    {
        // transpose (incl. non-square), matrix in a series (reference element), share-on-assign.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                State cast collections's transpose of (m).
                Define wide as a matrix with ((1, 2, 3), (4, 5, 6)).
                State cast collections's transpose of (wide).
                Define g as a matrix with 2 by 2 filled with 9.
                Define ms as a series with (m, g).
                State the item at (1, 1) of first of ms.
                Define m2 as m.
                State the item at (2, 2) of m2.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Matrix_FunctionParamsAndReturns()
    {
        // A matrix-typed function must live INSIDE the collections pull (the type isn't in scope
        // outside) — the compiler hoists Pull-body binds to free functions (books are compile-time).
        const string src = """
            Pull a book on collections.
                Bind the matrix to double-it, given (the matrix m):
                    Define d as (m + m) but on failure (m).
                    return d.
                Done.
                Define src as a matrix with ((1.5, 2), (3, 4.25)).
                Define doubled as cast double-it on (src).
                State doubled.
                State the item at (2, 2) of doubled.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void MatrixSet_ElementWrites_OracleMatch()
    {
        // The write half of `the item at (row, column) of m`. Includes computed indices and an
        // alias, so the two backends have to agree on reference semantics and not merely on values.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with 3 by 3 filled with 0.
                The item at (1, 1) of m becomes 5.
                The item at (3, 3) of m becomes 0 - 2.5.
                Define r as 1.
                The item at (r + 1, r + 1) of m becomes 7.
                State m.
                State the item at (2, 2) of m.
                Define alias as m.
                The item at (1, 3) of alias becomes 9.
                State m.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void MatrixSet_ThroughAParameter_OracleMatch()
    {
        // A matrix parameter is the caller's matrix on both backends — the compiler passes the
        // pointer, the interpreter shares the array. This is what makes an in-place board work.
        const string src = """
            Pull a book on collections.
                Bind void to light, given (the matrix board, the number r, the number c):
                    The item at (r, c) of board becomes 1.
                Done.
                Define b as a matrix with 2 by 2 filled with 0.
                Cast light on (b, 2, 1).
                Cast light on (b, 1, 2).
                State b.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void MatrixSet_OutOfRange_FaultsOnBothBackends()
    {
        // Bounds messages are shared text on purpose — cufet_mat_set repeats cufet_mat_get's. An
        // uncaught fault cannot be compared through stdout (the interpreter throws where the binary
        // exits), so this pins the shape both sides agree on: everything before the write runs, and
        // the write does not.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with 2 by 2 filled with 0.
                State "before".
                The item at (3, 1) of m becomes 1.
                State "after".
            Done.
            """;
        var ex = Assert.Throws<RuntimeException>(() => Interpret(src));
        Assert.Contains("Row index 3 is out of range", ex.Message);
        Assert.Equal("before", Compile(src));
    }

    [LinuxFact]
    public void Matrix_MemorySafety_ASan()
    {
        // Matrices + arithmetic results + transposes are all arena allocations — everything frees
        // at Done., zero leaks/UAF.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                Try to:
                    Define p as m * m.
                    State the item at (2, 2) of p.
                Done.
                In case of failure:
                    State "unexpected".
                Done.
                State cast collections's transpose of (m).
            Done.
            """;
        Assert.Equal(Interpret(src), CompileSanitized(src));
    }

    [Fact]
    public void Chance_Invariants_CompiledAllPass()
    {
        Assert.Equal(ChanceExpectedPass, Compile(ChanceInvariantBattery));
    }

    [Fact]
    public void Chance_Invariants_InterpretedAllPass()
    {
        // The same invariants hold in the oracle — each backend is checked against the PROPERTY,
        // not against the other's bit-stream (the CONC.5 discipline for nondeterministic features).
        Assert.Equal(ChanceExpectedPass, Interpret(ChanceInvariantBattery));
    }

    [LinuxFact]
    public void Chance_Shuffle_MemorySafety_ASan()
    {
        // randomly shuffled builds a NEW arena series (like sorted/unique) — must free cleanly.
        const string src = """
            Pull a book on chance.
                Define xs as a series with (1, 2, 3, 4, 5, 6, 7, 8).
                Define sh as randomly shuffled xs.
                If (the number of sh) is 8, State "ok". Otherwise, State "bad".
            Done.
            """;
        Assert.Equal("ok", CompileSanitized(src));
    }

    // ── Cleanup slice: the misc smalls (env vars, is-a-type, voidable maps, directory contents) ──

    [Fact]
    public void EnvVar_UnsetIsVoid_PresentMatches()
    {
        // The compiled binary is a child of the test process, so it inherits the same environment —
        // the PATH value oracle-matches exactly; an unset name is void (both backends).
        const string src = """
            Define unset as the environment variable "CUFET_DEFINITELY_UNSET_XYZ".
            If unset is void, State "unset is void". Otherwise, State "FAIL".
            State the environment variable "PATH" but void is "none".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void IsTypeCheck_StaticConstant_AndVoidableRuntime()
    {
        // Static targets are compile-time constants (the monomorphic model); a VOIDABLE target is
        // the one dynamic case — `v is a number` ⇔ present, and the positive arm NARROWS (v + 1
        // reads the inner). Kind-erasure matches the interpreter (series by kind, element-erased).
        const string src = """
            Define n as 5.
            If n is a number, State "number yes". Otherwise, State "FAIL".
            If n is a text, State "FAIL2". Otherwise, State "not text".
            If n is not a text, State "negated ok". Otherwise, State "FAIL3".
            Define words as a series of text with ("x").
            If words is a series of text, State "series kind ok". Otherwise, State "FAIL4".
            Define v as "42" converted to number.
            If v is a number:
                State v + 1.
            Done.
            Define w as "abc" converted to number.
            If w is a number, State "FAIL5". Otherwise, State "unparsed not a number".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void VoidableValuedMaps_LookupFlattens_EntryKeyDiverge()
    {
        // `map from text to voidable number`: lookup FLATTENS (never voidable-voidable — an absent
        // key and a stored void both read as void); `has a key` sees the explicit-void slot but
        // `has an entry` does NOT (the interpreter's is-not-VoidValue rule).
        const string src = """
            Define m as a map from text to voidable number with ().
            In m, the entry for "present" becomes 7.
            In m, the entry for "explicit-void" becomes void.
            If m has a key for "explicit-void", State "void slot has key". Otherwise, State "FAIL".
            If m has an entry for "explicit-void", State "FAIL2". Otherwise, State "void slot has no entry".
            If m has an entry for "present", State "entry present ok".
            State (the entry for "present" in m) but void is -1.
            State (the entry for "nowhere" in m) but void is -99.
            State (the entry for "explicit-void" in m) but void is -7.
            State the size of m.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void DirectoryContents_SortedListing_AndFailurePaths()
    {
        // Both backends SORT entries (ordinal) — the raw OS order is filesystem-dependent, so
        // sorting defines the undefined (normalize-the-unobservable, the FormatRecord move). The
        // full paths use the platform separator, identical same-platform. Failures: not-found
        // message + category, and but-on-failure composes.
        var dir = Path.Combine(Path.GetTempPath(), "cufet-dirtest-" + Guid.NewGuid().ToString("N")[..8])
                      .Replace('\\', '/');
        Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/zeta.txt", "z");
        File.WriteAllText(dir + "/alpha.txt", "a");
        File.WriteAllText(dir + "/mid.log", "m");
        try
        {
            string src = $"""
                Try to:
                    Define entries as the contents of the directory "{dir}".
                    State entries.
                    State the number of entries.
                Done.
                In case of failure:
                    State "unexpected".
                Done.
                Try to:
                    Define nope as the contents of the directory "{dir}-definitely-not-here".
                    State "no failure".
                Done.
                In case of failure:
                    State the message of the failure.
                    State the category of the failure but void is "none".
                Done.
                """;
            Assert.Equal(InterpretRaw(src), CompileRaw(src));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [LinuxFact]
    public void DirectoryContents_MemorySafety_ASan()
    {
        // The listing's arena strings + array free cleanly at scope exit.
        var dir = Path.Combine(Path.GetTempPath(), "cufet-dirasan-" + Guid.NewGuid().ToString("N")[..8])
                      .Replace('\\', '/');
        Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/one.txt", "1");
        File.WriteAllText(dir + "/two.txt", "2");
        try
        {
            string src = $"""
                Try to:
                    Define entries as the contents of the directory "{dir}".
                    State the number of entries.
                Done.
                In case of failure:
                    State "unexpected".
                Done.
                """;
            Assert.Equal(Interpret(src), CompileSanitized(src));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ── CONC.E-prime: the exception path (In case of exception / Suppress) ──
    // setjmp/longjmp over SOFTWARE faults (Cufet's numbers are software decimals — div/mod-by-zero
    // and OOB are detected checks, not hardware signals). Handlers are a per-thread jmp_buf stack
    // (nested Trys nest; innermost wins); the handler RE-RAISES by default unless it Suppresses.
    // Fault messages replicate the interpreter's RuntimeException text, line numbers included.

    [Fact]
    public void Exception_FaultSites_CaughtWithExactMessages()
    {
        const string src = """
            Try to:
                Define x as 1 / 0.
            Done.
            In case of exception (the exception):
                State the message of the exception.
                Suppress the exception.
            Done.
            Try to:
                Define y as 7 % 0.
            Done.
            In case of exception (the exception):
                State the message of the exception.
                Suppress the exception.
            Done.
            Try to:
                Define xs as a series with (1, 2, 3).
                State item 9 of xs.
            Done.
            In case of exception (the exception):
                State the message of the exception.
                Suppress the exception.
            Done.
            Try to:
                Define empty as a series of number with ().
                State the last of empty.
            Done.
            In case of exception (the exception):
                State the message of the exception.
                Suppress the exception.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// `Suppress` releases everything the handler opened, not just its arenas.
    /// </summary>
    /// <remarks>
    /// ⚠ REGRESSION, and a live divergence. `Suppress` is a nonlocal exit out of the handler block,
    /// and every other nonlocal exit in the generator emits the same FOUR releases in the same
    /// order — run unmakers, close files, pop exception pads, pop arenas. This site emitted only
    /// the arena pop, under a comment saying it worked "exactly like Stop out of a loop"; Stop does
    /// all four. So a destructor on an object made inside a suppressing handler never ran. The
    /// interpreter unwinds the handler block properly and printed `unmade: …`; the compiled program
    /// printed nothing.
    ///
    /// ★ The exact failure the one-ownership-story refactor exists to prevent: four things to
    /// release, nine open-coded sites, and nothing making a new one remember all four.
    /// </remarks>
    [Fact]
    public void Suppress_RunsUnmakersForObjectsMadeInTheHandler()
    {
        const string src = """
            Define object noisy with (the text tag):
            Done.

            Bind unmaking a noisy to hush:
                State "unmade: " joined to one's tag.
            Done.

            State "before".
            Try to:
                State 1 / 0.
            Done.
            In case of exception (the exception):
                Define inside as a new noisy { the tag "in-handler" }.
                State "handling".
                Suppress the exception.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
    [Fact]
    public void ABindInsideAPullInsideARabbit_Compiles()
    {
        // !! An interpreted-runs / compiled-refuses split, with a message that was simply untrue:
        // "'doubled' is declared further down this block" about a function declared four lines
        // ABOVE the call.
        //
        // ** The cause is the walk, not the ordering. Binds inside a pull body are HOISTED to free
        // functions at Generate time, and the pull emitter skips them because of that -- but the
        // collector that hoisted them matched `PullStatement` and recursed only into its body, so
        // a pull nested inside a rabbit was never reached. The Bind was neither hoisted nor
        // emitted in place, and the name resolved nowhere.
        //
        // * Either nesting ALONE compiled fine, which is what kept it hidden: `Pull a book` at top
        // level worked, and `Pull a rabbit` with a bare Bind worked. Only the two together failed.
        const string src = """
            Pull a rabbit.
                Pull a book on math.
                    Bind number to doubled, given (the number x):
                        Return x * 2.
                    Done.

                    State cast doubled on (21).
                Done.
            Done.
            """;
        Assert.Equal("42", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ABindInsideAPullInsideARabbit_StillSeesTheBook()
    {
        // The hoist has to carry the book aliases with it, or the hoisted body cannot resolve the
        // member that made it worth writing inside the pull.
        const string src = """
            Pull a rabbit.
                Pull a book on math.
                    Bind number to rooted, given (the number x):
                        Return (math's square-root of (x)) but void is 0.
                    Done.

                    State cast rooted on (144).
                Done.
            Done.
            """;
        Assert.Equal("12", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
