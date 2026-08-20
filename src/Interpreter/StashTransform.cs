using Cufet.Lexer;

namespace Cufet.Interpreter;

/// <summary>
/// The types StashTransform cannot work out on its own, gathered by the type checker while it
/// checks a burying body.
/// </summary>
/// <remarks>
/// Only two things are needed, and both are needed for the same reason: a hoisted local lives in a
/// one-element series, and a series has to be told what it holds.
/// </remarks>
public sealed class StashFacts
{
    /// <summary>(burying function, local name) → the local's declared or inferred type.</summary>
    public Dictionary<(string Function, string Local), CufetType> Locals { get; } = new();

    /// <summary>A for-each's source type, keyed by the loop's position — unique by construction.</summary>
    public Dictionary<(int Line, int Column), CufetType> ForEachSources { get; } = new();
}

/// <summary>
/// Rewrites every stash-producing function into a factory that hands back a CLOSURE — so neither
/// backend needs suspension machinery, because after this pass there is nothing left to suspend.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Why a closure and not a generated object type. The obvious lowering gives each burying
/// function its own nominal type — `stash_walk`, `stash_ticker` — which works but is NOT first
/// class: those types have different fields and different sizes, so a `series of stash of number`
/// would need one representation for elements that are not the same shape, i.e. a vtable. Cufet
/// declines vtables, which is what makes interfaces free.
/// </para>
/// <para>
/// A closure already has a uniform representation the language ships and trusts —
/// <c>cfn_N { fn ptr; void* env; }</c>, two pointers, copied by value — and closures are already
/// storable in a series. So EVERY `stash of T` lowers to the same type, and holding one in a local,
/// passing it to a function, or putting it in a collection all work with no union, no narrowing and
/// no dispatch table. Verified on both backends before this was written.
/// </para>
/// <para>
/// ★ Every piece of state lives in a ONE-ELEMENT SERIES, and that is not arbitrary. A closure
/// captures value types by SNAPSHOT and region types by SHARE, so state that must survive between
/// resumptions has to live in a region. A series is the smallest one. The step counter gets one
/// (the frame); so does every local that crosses a bury.
/// </para>
/// <para>
/// ⚠ The lambda is returned INLINE, never bound to a local first: a closure that escapes indirectly
/// is refused by the compiler because its environment is opaque once built.
/// </para>
/// </remarks>
public static class StashTransform
{
    /// <summary>
    /// Rewrites the program's burying functions. <paramref name="buryingFunctions"/> and
    /// <paramref name="facts"/> both come from the type checker.
    /// </summary>
    public static IReadOnlyList<IStatement> Expand(
        IReadOnlyList<IStatement> statements,
        IReadOnlySet<string> buryingFunctions,
        StashFacts facts)
    {
        // Nothing buries ⇒ nothing can ever PRODUCE a stash, so any `stash of T` written in this
        // program is a type no value can inhabit. Leaving it alone keeps the walk below off the
        // path of every ordinary program, which is all of them.
        if (buryingFunctions.Count == 0) return statements;

        // ★ Two halves of one job. The rewrite turns burying BODIES into state machines; the
        // substitution turns written `stash of T` ANNOTATIONS into the closure type those machines
        // hand back. Both are needed: rewriting bodies alone leaves a `stash of number` parameter
        // spelled as a type the back end has never heard of.
        return StashTypeSubstitution.Apply(Rewrite(statements, buryingFunctions, facts));
    }

    private static List<IStatement> Rewrite(
        IReadOnlyList<IStatement> statements,
        IReadOnlySet<string> buryingFunctions,
        StashFacts facts)
    {
        var output = new List<IStatement>(statements.Count);
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case BindStatement bind when buryingFunctions.Contains(bind.Name) && bind.UntoType == null:
                    output.AddRange(new Machine(bind, facts).Build());
                    break;

                // A burying function can be declared inside any of these, so the walk follows them.
                // An ordinary Bind is included because a nested generator is a generator too.
                case BindStatement bind:
                    output.Add(bind with { Body = Rewrite(bind.Body, buryingFunctions, facts) });
                    break;
                case PullStatement ps:
                    output.Add(ps with { Body = Rewrite(ps.Body, buryingFunctions, facts) });
                    break;
                case PullRabbitStatement prs:
                    output.Add(prs with { Body = Rewrite(prs.Body, buryingFunctions, facts) });
                    break;

                default:
                    output.Add(stmt);
                    break;
            }
        }
        return output;
    }

    // ── The machine ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns one burying body into a state machine: basic blocks, a step counter, and a slot for
    /// every local that has to outlive a resumption.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape is a dispatch loop. The step IS the program counter, a branch is a step
    /// assignment, and a loop's back-edge is a step assignment too — so `While`, `If`, `For each`
    /// and `Repeat until` all lower to the same one thing and none of them needs a `goto` the
    /// language does not have.
    /// </para>
    /// <para>
    /// ★ Blocks are scopes. Each one is emitted as an arm of the dispatch `If`, so a local declared
    /// in two sibling blocks is declared in two sibling scopes and nothing collides. That is what
    /// makes flattening safe without renaming anything the writer wrote.
    /// </para>
    /// </remarks>
    private sealed class Machine
    {
        private sealed class Block
        {
            public readonly List<IStatement> Body = [];
            // The step assignment (and, at a bury, the return) that ends the block. Kept apart from
            // the body because the write-back of every hoisted local has to slot in between them.
            public List<IStatement>? Exit;

            /// <summary>
            /// The conditions that were true on the way in, outermost first.
            /// </summary>
            /// <remarks>
            /// ★★ This is what lets a bury live inside a type test. Splitting an `If` arm into its
            /// own block leaves the NARROWING behind — the block is re-entered with the subject back
            /// at its declared type, and the compiler then cannot see which case of a union it is
            /// holding. Recording the arm's condition here, and re-testing it when the block is
            /// assembled, hands that narrowing back.
            ///
            /// ⚠ The re-test is not a real branch. Every hoisted local is restored from its slot
            /// first, so the subject holds exactly what it held when the arm was chosen, and the
            /// condition gives exactly the answer it gave then. It is a restatement for the type
            /// checker and the code generator, not a decision.
            /// </remarks>
            public IReadOnlyList<IExpression> Guards = [];
        }

        private readonly record struct LoopContext(int Continue, int Break);

        private readonly BindStatement _bind;
        private readonly StashFacts    _facts;
        private readonly List<Block>   _blocks = [];
        // Locals this pass invented (a for-each's index and source) — the checker never saw them,
        // so their types are recorded here instead.
        private readonly Dictionary<string, CufetType> _invented = new(StringComparer.Ordinal);
        private readonly int _line, _col;
        private int _fresh;
        // Conditions in force at the point a block is created — see Block.Guards.
        private readonly List<IExpression> _guards = [];

        public Machine(BindStatement bind, StashFacts facts)
        {
            _bind  = bind;
            _facts = facts;
            _line  = bind.Line;
            _col   = bind.Column;
        }

        public List<IStatement> Build()
        {
            // ★ A burying function has no `Return`: it finishes by reaching its end, and the stash
            // reports that by handing back void. Allowing one would need a second way to say
            // "spent", which is a surface decision, not a lowering detail.
            if (ContainsReturn(_bind.Body))
                throw Refuse($"'{_bind.Name}' buries, so it can't also return a value",
                    "return from a burying function",
                    "A burying function finishes by reaching its end, and whoever holds the stash "
                  + "sees void once it is spent. Take the 'Return' out.", _line, _col);

            int entry = NewBlock();
            int tail  = EmitStatements(_bind.Body, entry, null);
            // No arm dispatches to this step, so the dispatch's Otherwise catches it: spent forever.
            SetExit(tail, [SetStep(_blocks.Count)]);

            var hoisted   = CollectHoisted();
            var slotNames = hoisted.Select(h => SlotPrefix + h.Name).ToList();
            var passed    = _bind.Parameters.Where(p => !hoisted.Any(h => h.Name == p.Name)).ToList();

            var element = _bind.ReturnType!;                  // what it buries
            var yields  = new VoidableType(element);
            string resumeName = ResumePrefix + _bind.Name;

            // ★ The dispatch lives in a NAMED function rather than in the lambda, and the reason is a
            // real front-end limit: a lambda's return type is INFERRED, and `return T` does not unify
            // with `return void`. The first `Return <buried value>` would fix the type as T, and the
            // terminal `Return void.` that reports a spent stash would then be an error. A named
            // function DECLARES `voidable T`, so both are fine.
            List<(CufetType, string)> resumeParams =
            [
                (new SeriesType(CufetType.Number), FrameName),
                .. hoisted.Select(h => ((CufetType)new SeriesType(h.Type), SlotPrefix + h.Name)),
                .. passed,
            ];

            var resume = new BindStatement(
                resumeName, yields, resumeParams, BuildDispatch(hoisted),
                UntoType: null, ConstructsTypeName: null, _line, _col);

            // The factory: make the state, hand back a closure over it.
            var factoryBody = new List<IStatement>
            {
                // Region-typed, so the closure SHARES it rather than snapshotting it — which is the
                // whole reason the step survives from one resumption to the next.
                Define(FrameName, new SeriesLiteral([Num(0)], CufetType.Number, _line, _col)),
            };
            foreach (var h in hoisted)
                factoryBody.Add(Define(SlotPrefix + h.Name, new SeriesLiteral(
                    // A hoisted PARAMETER starts life holding the argument. A hoisted local starts
                    // empty, and its first write fills it — which is why a store has two arms.
                    _bind.Parameters.Any(p => p.Name == h.Name) ? [Var(h.Name)] : [],
                    h.Type, _line, _col)));

            // ⚠ Returned INLINE. A closure bound to a local and then returned is refused by the
            // compiler — its environment is opaque once built.
            factoryBody.Add(new ReturnStatement(
                new LambdaLiteral([],
                    [new ReturnStatement(
                        new CastExpression(Var(resumeName),
                            [Var(FrameName),
                            .. slotNames.Select(Var),
                            .. passed.Select(p => (IExpression)Var(p.Name))],
                            _line, _col),
                        _line, _col)],
                    _line, _col),
                _line, _col));

            var factory = new BindStatement(
                _bind.Name, new FunctionType([], yields), _bind.Parameters, factoryBody,
                UntoType: null, ConstructsTypeName: null, _line, _col);

            return [resume, factory];
        }

        // ── Linearisation ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Emits <paramref name="stmts"/> starting in block <paramref name="cur"/>, and answers
        /// which block execution carries on in.
        /// </summary>
        private int EmitStatements(IReadOnlyList<IStatement> stmts, int cur, LoopContext? loop)
        {
            foreach (var stmt in stmts)
            {
                // ⚠ A `Stop` left verbatim inside a block would break the DISPATCH loop, not the
                // loop the writer meant — so anything holding one that belongs to a flattened loop
                // has to be flattened too, bury or no bury.
                bool mustSplit = ContainsBury(stmt)
                            || stmt is StopStatement or SkipStatement
                            || (loop != null && HasFreeLoopExit(stmt));
                if (mustSplit) cur = EmitControl(stmt, cur, loop);
                else           _blocks[cur].Body.Add(stmt);
            }
            return cur;
        }

        /// <summary>
        /// Emits an arm's body with <paramref name="guard"/> in force for every block it creates.
        /// </summary>
        /// <remarks>
        /// ⚠ The guard is pushed BEFORE the entry block is handed on, but that block was created by
        /// the caller and already snapshotted its guards — so it is applied here, to the block the
        /// arm actually starts in, as well as to everything created inside.
        /// </remarks>
        private int EmitGuarded(IReadOnlyList<IStatement> body, int entry, LoopContext? loop, IExpression? guard)
        {
            if (guard == null) return EmitStatements(body, entry, loop);

            _blocks[entry].Guards = [.. _blocks[entry].Guards, guard];
            _guards.Add(guard);
            try     { return EmitStatements(body, entry, loop); }
            finally { _guards.RemoveAt(_guards.Count - 1); }
        }

        private int EmitControl(IStatement stmt, int cur, LoopContext? loop)
        {
            switch (stmt)
            {
                case BuryStatement bury:
                {
                    int next = NewBlock();
                    SetExit(cur, [SetStep(next), new ReturnStatement(bury.Value, bury.Line, bury.Column)]);
                    return next;
                }

                case IfStatement ifs:
                {
                    // ★ An arm that TESTS A TYPE is carried into its own block as a guard and
                    // re-tested there, so the narrowing survives the split. Only narrowing
                    // conditions are worth carrying — an ordinary comparison tells the compiler
                    // nothing it did not already know, and re-testing it would be noise in the
                    // generated code.
                    int after = NewBlock();
                    var entries = ifs.Arms.Select(_ => NewBlock()).ToList();
                    int otherwise = NewBlock();

                    SetExit(cur, [new IfStatement(
                        [.. ifs.Arms.Select((arm, i) => new ConditionArm(arm.Condition, [SetStep(entries[i])]))],
                        [SetStep(otherwise)])]);

                    for (int i = 0; i < ifs.Arms.Count; i++)
                    {
                        var armGuard = NarrowsAType(ifs.Arms[i].Condition) ? ifs.Arms[i].Condition : null;
                        SetExit(EmitGuarded(ifs.Arms[i].Body, entries[i], loop, armGuard), [SetStep(after)]);
                    }

                    // ★ The else arm narrows BY ELIMINATION — after `If x is a text`, the
                    // `Otherwise` is where x is known NOT to be a text — and the negated test states
                    // exactly that, so it guards the block like any other condition. This only works
                    // because the compiler now narrows on `is not a <type>`; until it did, the guard
                    // was a condition that restored nothing and the case had to be refused.
                    //
                    // Expressible only with exactly one arm: with several, the surviving type is a
                    // residue that no single condition states.
                    var elseGuard = ifs.Arms.Count == 1 && ifs.Arms[0].Condition is IsTypeCheck only
                        ? new IsTypeCheck(only.Target, only.Type, !only.Negated, only.Line, only.Column)
                        : null;
                    SetExit(EmitGuarded(ifs.ElseBody ?? [], otherwise, loop, elseGuard), [SetStep(after)]);
                    return after;
                }

                case JudgeStatement judge:
                {
                    // ★ A judgement arm carries TWO things across a split, where an `If` arm carries
                    // one. The NARROWING is a guard, exactly as it is for an `If`. The BINDING is
                    // handled by making `it` an ordinary local: it earns a hoisting slot like any
                    // other name, so the subject is evaluated ONCE here and every later block
                    // restores `it` from its slot instead of re-evaluating a subject that may have
                    // moved on — which is what "a binding is not a condition" really costs.
                    //
                    // ★ A GROUPED arm states itself as a disjunction — `it is a A or it is a B` —
                    // which both front ends narrow to the sub-union. That is the only way to say
                    // "one of these, not the others": a single test names one case and elimination
                    // names all but one, and a group is neither.
                    _blocks[cur].Body.Add(Define(ItName, judge.Subject));

                    int after = NewBlock();
                    var entries = judge.Arms.Select(_ => NewBlock()).ToList();
                    int otherwise = NewBlock();

                    IExpression AnyOf(IReadOnlyList<CufetType> cases, JudgeArm? at)
                    {
                        int line = at?.Line ?? judge.Line, col = at?.Column ?? judge.Column;
                        IExpression test = new IsTypeCheck(Var(ItName), cases[0], false, line, col);
                        for (int i = 1; i < cases.Count; i++)
                            test = new BinaryExpression(test, TokenType.Or,
                                new IsTypeCheck(Var(ItName), cases[i], false, line, col), line, col);
                        return test;
                    }

                    // The `Otherwise` is reached for whatever the arms did not take, and that
                    // leftover is statable the same way — as a disjunction of the cases still
                    // standing. It needs the subject's own case list, which is why the checker
                    // records `it` at its WIDEST type.
                    IExpression? elseGuard = null;
                    if (judge.OtherwiseBody != null)
                    {
                        _facts.Locals.TryGetValue((_bind.Name, ItName), out var subjectType);
                        if (subjectType is not UnionType { Cases: { } subjectCases })
                            throw Refuse(
                                $"'{_bind.Name}' buries inside the 'Otherwise' of a judgement on something "
                              + "that is not a closed union",
                                "bury from inside that 'Otherwise'",
                                "The leftover cases have to be named to resume into them, and only a closed "
                              + "union lists what they are. Judge a closed union, or move the bury out of "
                              + "the judgement.", Where(judge));

                        var taken = judge.Arms.SelectMany(a => a.Cases).ToList();
                        var residue = subjectCases.Where(c => !taken.Any(t => t.Equals(c))).ToList();
                        // Nothing left ⇒ the arms already cover it and the block is unreachable, so
                        // there is no narrowing to restore and no guard to write.
                        if (residue.Count > 0) elseGuard = AnyOf(residue, null);
                    }

                    SetExit(cur, [new IfStatement(
                        [.. judge.Arms.Select((arm, i) =>
                            new ConditionArm(AnyOf(arm.Cases, arm), [SetStep(entries[i])]))],
                        [SetStep(otherwise)])]);

                    for (int i = 0; i < judge.Arms.Count; i++)
                        SetExit(EmitGuarded(judge.Arms[i].Body, entries[i], loop,
                                            AnyOf(judge.Arms[i].Cases, judge.Arms[i])),
                                [SetStep(after)]);

                    SetExit(EmitGuarded(judge.OtherwiseBody ?? [], otherwise, loop, elseGuard), [SetStep(after)]);
                    return after;
                }

                case WhileStatement w:
                {
                    int header = NewBlock(), body = NewBlock(), after = NewBlock();
                    SetExit(cur, [SetStep(header)]);
                    SetExit(header, [Branch(w.Condition, body, after)]);
                    SetExit(EmitStatements(w.Body, body, new LoopContext(header, after)), [SetStep(header)]);
                    return after;
                }

                case RepeatUntilStatement r:
                {
                    // The test gets a block of its own so `Skip` has somewhere to land — jumping
                    // straight back to the body would skip the until-check, which is not what
                    // skipping an iteration means.
                    int body = NewBlock(), test = NewBlock(), after = NewBlock();
                    SetExit(cur, [SetStep(body)]);
                    SetExit(EmitStatements(r.Body, body, new LoopContext(test, after)), [SetStep(test)]);
                    SetExit(test, [Branch(r.Condition, after, body)]);
                    return after;
                }

                case ForEachStatement fe:
                    return EmitForEach(fe, cur, loop);

                case StopStatement when loop is { } l:
                    SetExit(cur, [SetStep(l.Break)]);
                    return NewBlock();   // unreachable, and cheaper than proving so

                case SkipStatement when loop is { } l2:
                    SetExit(cur, [SetStep(l2.Continue)]);
                    return NewBlock();

                case StopStatement or SkipStatement:
                    throw Refuse($"'{_bind.Name}' has a 'Stop' or a 'Skip' with no loop to leave",
                        "stop or skip outside a loop",
                        "Put it inside the loop it belongs to.", Where(stmt));

                default:
                    throw Refuse(
                        $"'{_bind.Name}' buries inside a {Describe(stmt)}, which can't be split into steps",
                        $"bury from inside a {Describe(stmt)}",
                        "A Try block, a rabbit block, a task and a file block each carry something — a "
                      + "handler, a region, a thread, an open file — that a resumption cannot restore. "
                      + "Move the bury out of it, or use an If, a While or a judgement.", Where(stmt));
            }
        }

        /// <summary>
        /// `For each x in xs: … Done.` becomes an indexed `While`, so the loop's position is a
        /// number the frame can hold. A hidden iterator would have to be saved and restored
        /// somehow; an index is already the smallest thing that survives a suspension.
        /// </summary>
        private int EmitForEach(ForEachStatement fe, int cur, LoopContext? loop)
        {
            if (!_facts.ForEachSources.TryGetValue((fe.Line, fe.Column), out var sourceType))
                throw Refuse(
                    $"'{_bind.Name}' buries inside a for-each over something that is not a series",
                    "bury while looping over a map",
                    "Resuming a loop means counting back to where it was, and a map's entries have "
                    + "no position to count to. Loop over a series, or use a While.", (fe.Line, fe.Column));

            string source = $"stash_source_{_fresh}", index = $"stash_index_{_fresh}";
            _fresh++;
            _invented[source] = sourceType;
            _invented[index]  = CufetType.Number;
            string iterator = fe.IteratorName ?? ItName;

            _blocks[cur].Body.Add(Define(source, fe.Series));
            _blocks[cur].Body.Add(Define(index, Num(1)));

            int header = NewBlock(), body = NewBlock(), step = NewBlock(), after = NewBlock();
            SetExit(cur, [SetStep(header)]);
            SetExit(header, [Branch(
                new BinaryExpression(Var(index), TokenType.Lte,
                    new SeriesLength(Var(source), fe.Line, fe.Column), fe.Line, fe.Column),
                body, after)]);

            _blocks[body].Body.Add(Define(iterator, new SeriesAccess(Var(source), Var(index), fe.Line, fe.Column)));
            SetExit(EmitStatements(fe.Body, body, new LoopContext(step, after)), [SetStep(step)]);

            _blocks[step].Body.Add(new BecomesStatement(index,
                new BinaryExpression(Var(index), TokenType.Plus, Num(1), fe.Line, fe.Column), fe.Line, fe.Column));
            SetExit(step, [SetStep(header)]);
            return after;
        }

        // ── Hoisting ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every name that has to survive a resumption, in slot order.
        /// </summary>
        /// <remarks>
        /// Two sources, and the rule for each is a consequence of the shape above. A PARAMETER is
        /// handed to the resume function afresh on every call, so it only needs a slot if the body
        /// REASSIGNS it. A LOCAL that ended up at the top level of a block did so because
        /// linearisation put it there, which means the block it belongs to is one the machine can
        /// re-enter — so it gets a slot whether or not it strictly needs one. Hoisting a local that
        /// never crosses a bury costs one series and changes nothing.
        /// </remarks>
        private List<(string Name, CufetType Type)> CollectHoisted()
        {
            var ordered = new List<(string, CufetType)>();
            var seen    = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (type, name) in _bind.Parameters)
                if (WritesTo(_bind.Body, name) && seen.Add(name))
                    ordered.Add((name, type));

            foreach (var block in _blocks)
                foreach (var stmt in block.Body)
                    if (stmt is DefineStatement define && seen.Add(define.Name))
                        ordered.Add((define.Name, TypeOfLocal(define)));

            return ordered;
        }

        private CufetType TypeOfLocal(DefineStatement define)
        {
            if (_invented.TryGetValue(define.Name, out var invented)) return invented;
            if (_facts.Locals.TryGetValue((_bind.Name, define.Name), out var known)) return known;
            throw Refuse(
                $"'{_bind.Name}' buries, and the type of '{define.Name}' could not be worked out",
                $"keep '{define.Name}' across a bury",
                "Everything that survives a resumption is stored, and storing it needs its type. "
                + "Give it a starting value whose type is clear.", (define.Line, define.Column));
        }

        /// <summary>The offending statement's own position, or the function's if it has none.</summary>
        /// <remarks>
        /// ★ Ten AST nodes carry no position at all — `Stop` and `Skip` among them, which is exactly
        /// the pair most likely to be refused here. Pointing at the function is a worse answer than
        /// pointing at the line, and a much better one than pointing at line 0.
        /// </remarks>
        private (int Line, int Column) Where(object node)
        {
            var type = node.GetType();
            if (type.GetProperty("Line")?.GetValue(node) is int line and > 0)
                return (line, type.GetProperty("Column")?.GetValue(node) is int column ? column : 1);
            return (_line, _col);
        }

        private static string Describe(IStatement stmt) => stmt switch
        {
            TryStatement         => "Try block",
            PullRabbitStatement  => "rabbit block",
            LaunchTaskStatement  => "task",
            WithOpenStatement    => "file block",
            _                    => stmt.GetType().Name,
        };

        /// <summary>
        /// Assembles one block: load what it reads, run it, write back what it changed, then jump.
        /// </summary>
        /// <remarks>
        /// ⚠ The write-back goes BEFORE the exit, not after — a block's exit is a step assignment,
        /// a branch, or a `Return` at a bury, and all three only READ the locals. Putting the
        /// write-back after the `Return` would put it after the block has already left.
        /// </remarks>
        private List<IStatement> Assemble(Block block, List<(string Name, CufetType Type)> hoisted)
        {
            var result = new List<IStatement>();
            foreach (var (name, _) in hoisted)
                if (NeedsLoad(block, name))
                    result.Add(Define(name, new SeriesAccess(Slot(name), Num(1), _line, _col)));

            var inner = new List<IStatement>();
            inner.AddRange(block.Body);

            foreach (var (name, _) in hoisted)
                if (Changes(block, name))
                    inner.Add(Store(name));

            inner.AddRange(block.Exit ?? []);

            // ★ Guards wrap everything AFTER the loads and INCLUDING the exit. After the loads,
            // because the guard reads a local that has to be restored first; including the exit,
            // because a narrowed value may well be what the block buries.
            //
            // ⚠ Each guard gets an `Otherwise` that leaves. The guard cannot be false — the value
            // was just restored from the slot that made it true — but a fall-through would skip the
            // step assignment and leave the dispatch loop spinning on the same block forever.
            // Returning void turns an impossibility into a spent stash rather than a hang.
            for (int i = block.Guards.Count - 1; i >= 0; i--)
                inner = [new IfStatement(
                    [new ConditionArm(block.Guards[i], inner)],
                    [new ReturnStatement(new VoidLiteral(_line, _col), _line, _col)])];

            result.AddRange(inner);
            return result;
        }

        /// <summary>
        /// Does this block read a slot's value, or only overwrite it?
        /// </summary>
        /// <remarks>
        /// The test is the block's FIRST mention of the name. If that mention is the declaration
        /// itself, the block establishes the value and there is nothing to load — which is exactly
        /// the case for a local declared inside a loop body, re-declared on every pass. Anything
        /// else — a read, or a `becomes` that needs the name to already exist — needs the load.
        /// </remarks>
        private static bool NeedsLoad(Block block, string name)
        {
            // ⚠ A guard is re-tested before anything else runs, so a name it mentions must already
            // be loaded — checked FIRST, ahead of the body, because the guard comes first in time.
            if (Mentions(block.Guards, name)) return true;

            foreach (var stmt in block.Body)
            {
                if (!Mentions(stmt, name)) continue;
                return stmt is not DefineStatement define || define.Name != name;
            }
            return Mentions(block.Exit, name);   // a branch condition still has to read it
        }

        private static bool Changes(Block block, string name) =>
            block.Body.Any(s => s is DefineStatement d && d.Name == name) || WritesTo(block.Body, name);

        /// <summary>
        /// Writes a local back to its slot — filling it if this is the first write.
        /// </summary>
        /// <remarks>
        /// ★ Why the slot starts empty rather than holding a placeholder: a placeholder needs a
        /// default value for an ARBITRARY type, and there is no such thing for an object. An empty
        /// series needs nothing but its element type, and two arms here cost less than a rule about
        /// which types may cross a bury.
        /// </remarks>
        private IStatement Store(string name) => new IfStatement(
            [new ConditionArm(
                new BinaryExpression(new SeriesLength(Slot(name), _line, _col), TokenType.Equal, Num(0), _line, _col),
                [new SeriesInsertStatement(Var(name), Slot(name), null, false, _line, _col)])],
            [new SeriesSetStatement(Slot(name), Num(1), Var(name), _line, _col)]);

        // ── The dispatch loop ──────────────────────────────────────────────────────────────────

        private List<IStatement> BuildDispatch(List<(string Name, CufetType Type)> hoisted)
        {
            var arms = _blocks
                .Select((block, i) => new ConditionArm(
                    new BinaryExpression(Var(StepName), TokenType.Equal, Num(i), _line, _col),
                    Assemble(block, hoisted)))
                .ToList();

            var spent = new ReturnStatement(new VoidLiteral(_line, _col), _line, _col);

            return
            [
                new WhileStatement(new BooleanLiteral(true, _line, _col),
                [
                    Define(StepName, new SeriesAccess(Var(FrameName), Num(1), _line, _col)),
                    new IfStatement(arms, [spent]),
                ]),
                // The checker cannot see that `While true` never falls out, so it needs a terminator
                // it can point at. Unreachable, and cheaper than teaching it otherwise.
                spent,
            ];
        }

        // ── Block plumbing ─────────────────────────────────────────────────────────────────────

        private int NewBlock()
        {
            // Snapshotted, not shared: blocks created deeper inside an arm inherit the guards in
            // force at their creation, and are unaffected by what is pushed or popped afterwards.
            _blocks.Add(new Block { Guards = [.. _guards] });
            return _blocks.Count - 1;
        }

        // First exit wins: a block that already left (a `Stop`, say) is not re-terminated by the
        // construct that contained it.
        private void SetExit(int block, List<IStatement> exit) => _blocks[block].Exit ??= exit;

        private IStatement SetStep(int to) =>
            new SeriesSetStatement(Var(FrameName), Num(1), Num(to), _line, _col);

        private IStatement Branch(IExpression condition, int whenTrue, int whenFalse) =>
            new IfStatement([new ConditionArm(condition, [SetStep(whenTrue)])], [SetStep(whenFalse)]);

        private IStatement Define(string name, IExpression value) =>
            new DefineStatement(name, value, false, false, _line, _col);

        private IExpression Slot(string name) => Var(SlotPrefix + name);
        private VariableReference Var(string name) => new(name, _line, _col);
    }

    // ★ Names a user cannot write. An identifier is letters, digits and INTERNAL DASHES — never an
    // underscore — so these can never collide with a parameter or a local. (A capital would not
    // work: it lexes fine and is then refused, because an uppercase-initial identifier is illegal
    // everywhere. And `stash-frame` would NOT be safe, since the keyword check matches the whole
    // lexeme: `stash-frame` is still a legal user identifier despite `stash` being reserved.)
    private const string FrameName    = "stash_frame";
    private const string StepName     = "stash_step";
    private const string SlotPrefix   = "stash_slot_";
    private const string ResumePrefix = "stash_resume_";

    // ⚠ The one name here a user CAN write, and deliberately so: `it` is the language's own name
    // for a judgement's subject and a for-each's iterator, so the machine has to use exactly it.
    private const string ItName = "it";

    // NumberLiteral is one of the position-less nodes — it carries no line or column.
    private static NumberLiteral Num(int n) => new(n);

    private static StashUnsupportedException Refuse(
        string context, string action, string fix, (int Line, int Column) at) =>
        new(context, action, fix, at.Line, at.Column);

    private static StashUnsupportedException Refuse(
        string context, string action, string fix, int line, int column) =>
        new(context, action, fix, line, column);

    // ── AST search ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Does this function's own body bury? The boundary the type checker uses too.</summary>
    internal static bool ContainsBury(object? node) =>
        Search(node, n => n is BuryStatement, Nested);

    private static bool ContainsReturn(object? node) =>
        Search(node, n => n is ReturnStatement, Nested);

    /// <summary>A `Stop` or `Skip` that would leave this statement and land on an enclosing loop.</summary>
    private static bool HasFreeLoopExit(object? node) =>
        Search(node, n => n is StopStatement or SkipStatement,
                    n => Nested(n) || n is WhileStatement or RepeatUntilStatement or ForEachStatement);

    /// <summary>Does this condition narrow the type of what it tests?</summary>
    /// <remarks>
    /// ⚠ `x is not void` is the SECOND narrowing form and was missing here, which cost the arm its
    /// guard: the block ran with `x` back at the `voidable T` its slot holds, and any use of the
    /// value the arm had just proved present — `If x is greater than 12`, `bury x + 1` — was
    /// refused by the compiler for operating on a voidable. The interpreter narrows by value and
    /// never noticed, so it ran the same program happily; a divergence, and one with NO `For each`
    /// or anything new in it. Both front ends already treat this shape as narrowing
    /// (`TryGetNotVoidNarrowing`, `NotVoidNarrow`) — only the linearisation did not.
    ///
    /// ★ The one-armed shape only. `x is void` narrows nothing on the way IN (x is void there, and
    /// void is what the slot already says), so its guard would restore nothing.
    /// </remarks>
    private static bool NarrowsAType(object? node) =>
        Search(node, n => n is IsTypeCheck
                       || n is BinaryExpression { Op: TokenType.NotEqual, Left: VoidLiteral }
                       || n is BinaryExpression { Op: TokenType.NotEqual, Right: VoidLiteral },
               Nested);

    /// <summary>Any appearance of the name — read, reassigned, or declared.</summary>
    private static bool Mentions(object? node, string name) =>
        Search(node, n => n switch
        {
            VariableReference reference => reference.Name == name,
            BecomesStatement  becomes   => becomes.Name   == name,
            DefineStatement   define    => define.Name    == name,
            _ => false,
        }, _ => false);

    private static bool WritesTo(object? node, string name) =>
        Search(node, n => n is BecomesStatement becomes && becomes.Name == name, _ => false);

    // A nested function's statements belong to IT, not to the body being rewritten.
    private static bool Nested(object node) => node is BindStatement or LambdaLiteral;

    /// <summary>
    /// Walks the tree until <paramref name="hit"/> answers yes, refusing to descend into anything
    /// <paramref name="opaque"/> claims.
    /// </summary>
    /// <remarks>
    /// ★ Keyed on the NAMESPACE, not on IExpression/IStatement — ConditionArm and JudgeArm
    /// implement neither, so matching the interfaces walks straight past the body of every `If` and
    /// every judgement. Same rule as AstSearch; see its note.
    /// </remarks>
    private static bool Search(object? node, Func<object, bool> hit, Func<object, bool> opaque)
    {
        switch (node)
        {
            case null or string or CufetType:
                return false;
            // Parameter lists are (type, name) tuples, and a tuple is not in our namespace.
            case System.Runtime.CompilerServices.ITuple tuple:
                for (int i = 0; i < tuple.Length; i++)
                    if (Search(tuple[i], hit, opaque)) return true;
                return false;
            case System.Collections.IEnumerable items:
                foreach (var item in items)
                    if (Search(item, hit, opaque)) return true;
                return false;
            default:
                if (node.GetType().Namespace != typeof(Program).Namespace) return false;
                if (hit(node))    return true;
                if (opaque(node)) return false;
                foreach (var property in node.GetType().GetProperties())
                    if (Search(property.GetValue(node), hit, opaque)) return true;
                return false;
        }
    }
}

/// <summary>
/// A shape inside a burying body that this pass cannot linearise.
/// </summary>
/// <remarks>
/// <para>
/// ★ A clean refusal, not a crash. Straight-line code, `If`, `While`, `Repeat until`, `For each`
/// over a series, `Stop` and `Skip` all lower; a judgement, a `Try`, or a rabbit block holding a
/// bury does not, because each of them carries context — a narrowing, a handler, a region — that a
/// step number cannot restore. A program that would be mis-lowered is told so, rather than
/// compiling into something subtly wrong.
/// </para>
/// <para>
/// It is a TYPE ERROR, in the same shape and reported the same way, because it is one: the writer
/// asked for something the language does not do yet, and finding that out should not look different
/// from finding out that a text will not go into a number.
/// </para>
/// </remarks>
public sealed class StashUnsupportedException : TypeException
{
    public StashUnsupportedException(string context, string action, string fix, int line, int column)
        : base($"That doesn't work: {context}.\nHere on line {line}, you're trying to {action}.\n\n{fix}",
                line, column)
    { }
}
