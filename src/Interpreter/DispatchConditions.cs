using Cufet.Lexer;

namespace Cufet.Interpreter;

/// <summary>
/// Reads a version's <c>when</c> clause, and decides whether two of them can both hold.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Overlap between two versions is REFUSED, never resolved. That is what the rest of the
/// language already does with ambiguity — two overloads on one ordered pair, a name that is both a
/// method and a free function, a <c>Judge</c> that misses a case — and it is why there is no
/// specificity rule here to learn. The question "which of these two wins" is never asked.
/// </para>
/// <para>
/// ★★ Which turns an undecidable problem into a bounded one. Deciding whether two arbitrary
/// boolean expressions can both hold is not possible; deciding it for the fragment below is a
/// comparison of atoms. So the fragment is the design, not a limitation of it.
/// </para>
/// <para>
/// ⚠ The check is SOUND and deliberately INCOMPLETE. Over a <c>(red or green)</c>, the clauses
/// `is not a red` and `is not a green` are disjoint by exhaustion and no atom pair says so, so
/// they are refused. That errs toward refusal, which is the safe direction: a refused program is a
/// message, an accepted ambiguous one is a silent wrong answer.
/// </para>
/// </remarks>
public static class DispatchConditions
{
    /// <summary>One indivisible test: a target compared against a literal, or against a type.</summary>
    /// <param name="Target">
    /// The thing being tested, canonicalised — `node`, `node's left`. Two atoms can only
    /// contradict when they name the same one, and comparing the spelling is how that is decided.
    /// </param>
    public readonly record struct Atom(string Target, object? Literal, CufetType? Type, bool Negated);

    /// <summary>A clause in disjunctive normal form: an OR of ANDs.</summary>
    public sealed record Clause(IReadOnlyList<IReadOnlyList<Atom>> Conjuncts);

    /// <summary>
    /// The clause a `when` expression stands for, or null when it falls outside the fragment.
    /// </summary>
    /// <remarks>
    /// Null is not an error here — the caller turns it into one, because only the caller knows
    /// which version and which line to name.
    /// </remarks>
    public static Clause? Read(IExpression condition)
    {
        var conjuncts = Dnf(condition);
        return conjuncts is null ? null : new Clause(conjuncts);
    }

    /// <summary>Whether two clauses can never both hold.</summary>
    public static bool AreDisjoint(Clause left, Clause right)
    {
        // ★ Every conjunct against every conjunct. A disjunction holds when ANY of its conjuncts
        // does, so the two clauses overlap the moment one pair of conjuncts is jointly satisfiable.
        foreach (var l in left.Conjuncts)
            foreach (var r in right.Conjuncts)
                if (!Contradict(l, r)) return false;
        return true;
    }

    /// <summary>Whether two conjunctions can never both hold.</summary>
    private static bool Contradict(IReadOnlyList<Atom> left, IReadOnlyList<Atom> right)
    {
        foreach (var l in left)
            foreach (var r in right)
                if (Contradict(l, r)) return true;
        return false;
    }

    private static bool Contradict(Atom left, Atom right)
    {
        if (!string.Equals(left.Target, right.Target, StringComparison.Ordinal)) return false;

        // A literal test against a type test says nothing about the other — `node's kind is "eof"`
        // and `node is a token` are about different things even on one target.
        if ((left.Literal is null) != (right.Literal is null)) return false;

        if (left.Literal is not null)
        {
            bool same = Equals(left.Literal, right.Literal);
            //  is 5   /  is not 5   → contradict.     is 5 / is 7 → contradict.
            //  is 5   /  is 5       → agree.          is not 5 / is not 7 → both hold at 9.
            return same ? left.Negated != right.Negated
                        : !left.Negated && !right.Negated;
        }

        bool sameType = Equals(left.Type, right.Type);
        return sameType ? left.Negated != right.Negated
                        : !left.Negated && !right.Negated;
    }

    // ── Reading the fragment ──────────────────────────────────────────────────────────────────

    /// <summary>Normalises to an OR of ANDs, or null when something outside the fragment appears.</summary>
    private static List<IReadOnlyList<Atom>>? Dnf(IExpression e)
    {
        switch (e)
        {
            case BinaryExpression { Op: TokenType.Or } or1:
            {
                var left = Dnf(or1.Left); var right = Dnf(or1.Right);
                if (left is null || right is null) return null;
                left.AddRange(right);
                return left;
            }

            case BinaryExpression { Op: TokenType.And } and1:
            {
                var left = Dnf(and1.Left); var right = Dnf(and1.Right);
                if (left is null || right is null) return null;
                // (a ∨ b) ∧ (c ∨ d) = (a∧c) ∨ (a∧d) ∨ (b∧c) ∨ (b∧d)
                var product = new List<IReadOnlyList<Atom>>();
                foreach (var l in left)
                    foreach (var r in right)
                        product.Add([.. l, .. r]);
                return product;
            }

            // ⚠ `xor` adds no power — the atoms already negate, so `a xor b` is expressible
            // without it. It is here because leaving it out would be the arbitrary thing: `and`,
            // `xor` and `or` are one family on one precedence line, and allowing two of the three
            // becomes a footnote nobody remembers. The cost is that expanding it doubles the
            // conjuncts, where `or` only adds.
            case BinaryExpression { Op: TokenType.Xor } xor:
            {
                var a = xor.Left; var b = xor.Right;
                return Dnf(new BinaryExpression(
                    new BinaryExpression(a, TokenType.And, Negate(b), xor.Line, xor.Column),
                    TokenType.Or,
                    new BinaryExpression(Negate(a), TokenType.And, b, xor.Line, xor.Column),
                    xor.Line, xor.Column));
            }

            case IsTypeCheck typeTest when Target(typeTest.Target) is { } target:
                return [new[] { new Atom(target, null, typeTest.Type, typeTest.Negated) }];

            case BinaryExpression { Op: TokenType.Equal or TokenType.NotEqual } cmp:
            {
                bool negated = cmp.Op == TokenType.NotEqual;
                if (Literal(cmp.Right) is { } rhs && Target(cmp.Left) is { } lt)
                    return [new[] { new Atom(lt, rhs, null, negated) }];
                if (Literal(cmp.Left) is { } lhs && Target(cmp.Right) is { } rt)
                    return [new[] { new Atom(rt, lhs, null, negated) }];
                return null;
            }

            default:
                return null;   // ordering, arithmetic, a call — outside the fragment
        }
    }

    /// <summary>The negation of an expression, expressed inside the fragment.</summary>
    private static IExpression Negate(IExpression e) => e switch
    {
        IsTypeCheck t => t with { Negated = !t.Negated },
        BinaryExpression { Op: TokenType.Equal } b    => b with { Op = TokenType.NotEqual },
        BinaryExpression { Op: TokenType.NotEqual } b => b with { Op = TokenType.Equal },
        // De Morgan, so a negated compound stays inside the fragment rather than falling out of it.
        BinaryExpression { Op: TokenType.And } b =>
            new BinaryExpression(Negate(b.Left), TokenType.Or, Negate(b.Right), b.Line, b.Column),
        BinaryExpression { Op: TokenType.Or } b =>
            new BinaryExpression(Negate(b.Left), TokenType.And, Negate(b.Right), b.Line, b.Column),
        _ => new UnaryExpression(TokenType.Not, e, 0, 0),   // falls out of the fragment, and Dnf says so
    };

    /// <summary>The canonical spelling of what an atom tests, or null when it is not a plain access.</summary>
    private static string? Target(IExpression e) => e switch
    {
        VariableReference v    => v.Name,
        PossessiveAccess p     => Target(p.Target) is { } t ? $"{t}'s {p.Member}" : null,
        RecordNamedAccess r    => Target(r.Record) is { } t ? $"{t}'s {r.FieldName}" : null,
        _                      => null,
    };

    /// <summary>The value a literal holds, or null when the expression is not one.</summary>
    private static object? Literal(IExpression e) => e switch
    {
        NumberLiteral n  => n.Value,
        StringLiteral s  => s.Value,
        BooleanLiteral b => b.Value,
        _                => null,
    };
}
