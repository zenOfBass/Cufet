using Cufet.Lexer;

namespace Cufet.Interpreter;

// Style warnings — legal code that reads worse than it needs to.
//
// Deliberately a separate pass from the checker, and deliberately incapable of producing an error.
// The checker answers "will this run"; this answers "is this how you would want to have written
// it", and the second question has no right to stop the first. Everything here is advice, and
// advice that cannot be ignored is not advice.
//
// Each rule owes an explanation of what to do instead, not just what is wrong — a warning that
// only names a fault makes the reader do the work twice.
//
// Two kinds of rule live here. The first reads TOKENS, because what it judges is how a line looks
// before it means anything. The rest read the AST, because what they judge is shape — one loop
// inside another, a statement ordered after a statement. Both are handed in.
public static class Linter
{
    public static IReadOnlyList<Diagnostic> Lint(
        IReadOnlyList<Token> tokens,
        IReadOnlyList<(int Line, int Column, bool KeywordLed)> statementStarts,
        Program program)
    {
        var bag = new DiagnosticBag();
        CapitaliseTheStartOfALine(bag, tokens, statementStarts);
        NestedBareItLoops(bag, program);
        ChangeDirectoryBeforeStartingTasks(bag, program);
        SupersededTypeDefinitions(bag, program);
        return bag.Items;
    }

    // ── Start a line with a capital letter ────────────────────────────────────
    //
    // A Cufet statement reads as a sentence, and a sentence opens with a capital. Keywords are
    // case-insensitive, so `for each x in xs, repeat:` and `For each …` are the same program —
    // which is exactly why this is the linter's business and not the parser's.
    //
    // ★ Only the half that needs no judgement, and that is now settled rather than pending. A line
    // opening with a KEYWORD can always be capitalised, and the fix is to capitalise that word —
    // nothing else changes and no reading is at stake. A line opening with a variable's own name is
    // left alone forever: capitalising it would rename it, so the only way to satisfy the rule there
    // is to insert an article, and whether `The total becomes 5.` reads better than `total becomes
    // 5.` depends on whether the name is a noun. `The got becomes 5.` is not English, and the fix is
    // to rename the variable — which no pass over the source is entitled to suggest. That half is
    // advice for a human and is never flagged.
    private static void CapitaliseTheStartOfALine(
        DiagnosticBag bag, IReadOnlyList<Token> tokens,
        IReadOnlyList<(int Line, int Column, bool KeywordLed)> statementStarts)
    {
        // The leftmost token on each line. A statement that begins further right on a line someone
        // else already opened is not what the rule is about — `If x is 1, state "one".` opens with
        // `If`, and the inline `state` is mid-sentence.
        var lineOpener = new Dictionary<int, int>();
        foreach (var t in tokens)
        {
            if (t.Type == TokenType.Eof) continue;
            if (!lineOpener.TryGetValue(t.Line, out int col) || t.Column < col)
                lineOpener[t.Line] = t.Column;
        }

        var byPosition = new Dictionary<(int, int), Token>();
        var indexByPosition = new Dictionary<(int, int), int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            byPosition[(tokens[i].Line, tokens[i].Column)] = tokens[i];
            indexByPosition[(tokens[i].Line, tokens[i].Column)] = i;
        }

        // ★ Leftmost-on-its-line is not the same as first-in-its-sentence. A one-line `If` whose
        // body is wrapped onto the next line puts that body at the left margin of a line nobody
        // else opened — but it is still the tail of a sentence that began with `If`:
        //
        //     If name is "world",
        //         cast greet on (name).
        //
        // Capitalising `cast` there would put a capital in the middle of a sentence, which is the
        // opposite of what the rule is for. What actually starts a sentence is the token BEFORE it:
        // a '.' ended the previous statement, a ':' opened a block, or there is nothing before it
        // at all. A ',' means the sentence is already under way.
        bool StartsASentence(int line, int column)
        {
            if (!indexByPosition.TryGetValue((line, column), out int index)) return false;
            int previous = index - 1;
            while (previous >= 0 && tokens[previous].IsNoise) previous--;
            if (previous < 0) return true;
            return tokens[previous].Type is TokenType.Dot or TokenType.Colon;
        }

        var reported = new HashSet<(int, int)>();
        foreach (var (line, column, keywordLed) in statementStarts)
        {
            // A name is not capitalisable — an identifier must start lowercase, so the capital
            // could only come from an article, and that is the judgement half of the rule. The
            // parser decides this, because it is contextual: `output 7.` opens with a keyword and
            // `output becomes 10.` opens with a variable that happens to share the spelling.
            // Suggesting a capital on the second would not improve it, it would break it.
            if (!keywordLed) continue;
            if (!lineOpener.TryGetValue(line, out int opener) || opener != column) continue;
            if (!StartsASentence(line, column)) continue;
            if (!byPosition.TryGetValue((line, column), out var tok)) continue;
            if (!reported.Add((line, column))) continue;
            if (tok.Lexeme.Length == 0 || !char.IsLower(tok.Lexeme[0])) continue;

            string capitalised = char.ToUpperInvariant(tok.Lexeme[0]) + tok.Lexeme[1..];
            bag.Warn(
                $"this line opens with '{tok.Lexeme}' — write '{capitalised}'. A statement reads as a " +
                $"sentence, and a sentence starts with a capital. Keywords are case-insensitive, so " +
                $"this changes nothing but how the line reads.",
                line, column);
        }
    }

    // ── A type definition replaced by a later one ──────────────────────
    //
    // Declaring a type twice is legal and the last one wins — the same rule shadowing follows
    // everywhere else here. What a reader cannot see is that the FIRST one is dead: nothing
    // dispatches to it, its methods are never emitted, and its body is not even checked.
    //
    // ★ Allowed and reported, exactly as a nested bare `it` is. Both are well defined, both are
    // invisible in the text, and the answer in this language is to put the fact back in front of
    // the writer rather than to refuse the program.
    //
    // Reported at the SUPERSEDED definition, because that is the one to remove — naming the winner
    // would point at code that is doing its job.
    private static void SupersededTypeDefinitions(DiagnosticBag bag, Program program)
    {
        // Every definition, in the order the checker registers them — the same walk, so "last" here
        // means what "last" means there.
        var byName = new Dictionary<string, List<ObjectDefinition>>(StringComparer.Ordinal);
        foreach (var statement in AstSearch.EveryStatement(program.Statements))
            if (statement is ObjectDefinition od)
            {
                if (!byName.TryGetValue(od.Name, out var seen)) byName[od.Name] = seen = [];
                seen.Add(od);
            }

        foreach (var (name, definitions) in byName)
        {
            if (definitions.Count < 2) continue;
            var winner = definitions[^1];
            foreach (var superseded in definitions)
            {
                if (ReferenceEquals(superseded, winner)) continue;
                bag.Warn(
                    $"this definition of '{name}' is replaced by the one on line {winner.Line}, so "
                  + $"nothing reaches it. That is well defined — the last definition wins, the same "
                  + $"as any other shadowing — but a reader has to notice the second one to know "
                  + $"this is dead. Remove it, or rename one of them.",
                    superseded.Line, superseded.Column);
            }
        }
    }

    // ── Nested bare-`it` loops ────────────────────────────────────────────────
    //
    // `For each in xs, repeat:` binds the element to `it`. Two of them nested is legal and its
    // meaning is not in doubt — the innermost binding wins, the same as any other shadowing — but
    // the reader has to hold which `it` is which, and the source stopped saying it. Naming either
    // loop puts the answer back in the text.
    //
    // Reported at the INNER loop, because that is the one to change: naming it leaves the outer
    // loop's `it` reading exactly as it did.
    private static void NestedBareItLoops(DiagnosticBag bag, Program program)
        => WalkBareIt(bag, program.Statements, null);

    private static void WalkBareIt(
        DiagnosticBag bag, IReadOnlyList<IStatement> block, ForEachStatement? enclosingBareIt)
    {
        foreach (var statement in block)
        {
            // What encloses this statement's OWN children. A bare-`it` loop becomes the enclosing
            // one for its body; anything else passes along whatever it inherited, so a bare loop
            // buried inside an `If` inside a bare loop is still nested for this rule's purposes.
            var enclosing = enclosingBareIt;

            if (statement is ForEachStatement { IteratorName: null } bare)
            {
                if (enclosingBareIt is not null)
                    bag.Warn(
                        $"this loop and the one on line {enclosingBareIt.Line} both bind 'it', so this " +
                        $"one shadows the outer. That is well defined — the innermost wins — but a " +
                        $"reader has to track which 'it' is which. Name one of them: " +
                        $"'For each <name> in …' binds the element to <name> instead.",
                        bare.Line, bare.Column);
                enclosing = bare;
            }

            foreach (var (child, opensNewScope) in ChildBlocks(statement))
                WalkBareIt(bag, child, opensNewScope ? null : enclosing);
        }
    }

    // ── Change the current directory before starting tasks ────────────────────
    //
    // Tasks resolve relative paths against the process's current directory, and there is one of
    // those for the whole process. A rabbit that changes it while its own tasks are already running
    // is a race: which directory a given task sees depends on when it happens to run.
    //
    // ★ A warning and not a refusal, and only for the ORDERING that is actually wrong. The compiler
    // already refuses this *inside* a task, where copy-versus-share has two defensible answers, and
    // the refusal message tells you to change the directory before spawning instead. Flagging that
    // recommended ordering would contradict the advice the compiler just gave, so a change made
    // before the first task starts is silent — it is the fix, not the fault.
    private static void ChangeDirectoryBeforeStartingTasks(DiagnosticBag bag, Program program)
    {
        var reported = new HashSet<(int, int)>();
        foreach (var rabbit in AllRabbits(program.Statements))
        {
            bool started = false;
            ScanRabbitBody(bag, rabbit, rabbit.Body, ref started, reported);
        }
    }

    private static IEnumerable<PullRabbitStatement> AllRabbits(IReadOnlyList<IStatement> block)
    {
        foreach (var statement in block)
        {
            if (statement is PullRabbitStatement rabbit) yield return rabbit;
            foreach (var (child, _) in ChildBlocks(statement))
                foreach (var nested in AllRabbits(child))
                    yield return nested;
        }
    }

    // Source order matters here and pre-order DFS gives it: a statement is visited before anything
    // nested inside it, and siblings in the order written. So "have we passed a task launch yet"
    // is just a flag carried along the walk.
    private static void ScanRabbitBody(
        DiagnosticBag bag, PullRabbitStatement rabbit, IReadOnlyList<IStatement> block,
        ref bool started, HashSet<(int, int)> reported)
    {
        foreach (var statement in block)
        {
            switch (statement)
            {
                case LaunchTaskStatement:
                    // The body is not walked. Changing the directory in there is already a hard
                    // compiler error, and repeating it as advice would only add noise to a refusal.
                    started = true;
                    continue;

                case CurrentDirectorySetStatement cd when started:
                    if (reported.Add((cd.Line, cd.Column)))
                        bag.Warn(
                            $"this changes the current directory after the rabbit on line {rabbit.Line} " +
                            $"has already started a task. Tasks resolve relative paths against it, and " +
                            $"there is one for the whole process, so which directory a running task sees " +
                            $"is a race. Change it before starting any task.",
                            cd.Line, cd.Column);
                    continue;
            }

            foreach (var (child, opensNewScope) in ChildBlocks(statement))
            {
                // A function declared inside the rabbit is not part of its ordering — its body runs
                // wherever it is called from, which this pass cannot see.
                if (opensNewScope) continue;
                ScanRabbitBody(bag, rabbit, child, ref started, reported);
            }
        }
    }

    // ── The shared walk ───────────────────────────────────────────────────────
    //
    // The statement blocks nested directly inside a statement, each paired with whether entering it
    // opens a NEW SCOPE — a function, a method, an accessor, an unmaker. Names visible outside do
    // not reach inside those, so a rule tracking "am I inside an X" has to forget on the way in.
    // Getting that backwards costs a false positive, and for advice a false positive is far more
    // expensive than a miss: it teaches the reader to stop reading the warnings.
    //
    // That flag is defensive rather than load-bearing today, and worth keeping anyway. The parser
    // refuses a function declared inside a block ("only at the top level or inside another
    // function"), so a body cannot currently sit lexically inside a loop at all. If local functions
    // ever land, the rules here stay correct instead of quietly inventing warnings.
    //
    // ★ Statements only. A lambda's body hangs off an EXPRESSION, so reaching it would mean walking
    // every expression in the program to serve an edge case. A rule here that misses something
    // inside a lambda gives no advice where it might have given some, which costs nothing.
    //
    // A new statement type that carries a body must be added here, or it becomes invisible to every
    // rule at once.
    private static IEnumerable<(IReadOnlyList<IStatement> Block, bool OpensNewScope)> ChildBlocks(
        IStatement statement)
    {
        switch (statement)
        {
            case IfStatement s:
                foreach (var arm in s.Arms) yield return (arm.Body, false);
                if (s.ElseBody is not null) yield return (s.ElseBody, false);
                break;

            case JudgeStatement s:
                foreach (var arm in s.Arms) yield return (arm.Body, false);
                if (s.OtherwiseBody is not null) yield return (s.OtherwiseBody, false);
                break;

            case TryStatement s:
                yield return (s.Body, false);
                if (s.FailureHandler is not null) yield return (s.FailureHandler, false);
                if (s.ExceptionHandler is not null) yield return (s.ExceptionHandler, false);
                break;

            case WhileStatement s: yield return (s.Body, false); break;
            case RepeatUntilStatement s: yield return (s.Body, false); break;
            case ForEachStatement s: yield return (s.Body, false); break;
            case ForEachFromInputStatement s: yield return (s.Body, false); break;
            case WithOpenStatement s: yield return (s.Body, false); break;
            case PullStatement s: yield return (s.Body, false); break;
            case PullRabbitStatement s: yield return (s.Body, false); break;
            case LaunchTaskStatement s: yield return (s.Body, false); break;

            case BindStatement s: yield return (s.Body, true); break;

            // ★ A cufet block IS looked into, and here rather than where it is cited — the linter
            // runs on the program as written, where the cited copies do not exist yet. Advice about
            // how a line reads is only actionable at the line, and the line is in the block.
            //
            // A new scope, like a `Bind` body: a bare `it` inside a block cannot mean a loop
            // outside it, because what the block holds is placed somewhere else entirely.
            case CufetAxiomDefinition s: yield return (s.Body, true); break;
            case OperatorOverloadDeclaration s: yield return (s.Body, true); break;
            case UnmakerDeclaration s: yield return (s.Body, true); break;
            case GetterDeclaration s: yield return (s.Body, true); break;
            case SetterDeclaration s: yield return (s.Body, true); break;

            // Methods and accessors hang off the type rather than standing as statements of their
            // own, so without this branch every body inside an object is invisible to every rule.
            case ObjectDefinition s:
                foreach (var method in s.Methods) yield return (method.Body, true);
                foreach (var getter in s.Getters) yield return (getter.Body, true);
                foreach (var setter in s.Setters) yield return (setter.Body, true);
                break;
        }
    }
}
