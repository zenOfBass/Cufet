namespace Cufet.Interpreter;

/// <summary>
/// Places what a `cufet` block holds, where a `Cite` says — and takes the blocks themselves out.
/// </summary>
/// <remarks>
/// <para>
/// ⭐⭐ A front-end pass, ahead of everything. After it a `cufet` block is not a thing that
/// exists: what it held are ordinary statements at the sites that cited them, and the block is
/// gone. That is why neither backend has a word to say about cufet axioms — the same arrangement
/// that lets no template and no `stash of T` survive the checker, and for the same reason.
/// </para>
/// <para>
/// ★★ Splicing INLINE is what settles where a cited declaration lands, without a rule of its
/// own. A TYPE declaration belongs to the program wherever it is written, so a cited object is
/// program-scope however deeply the `Cite` sits; a VALUE binding does not, so it would be local to
/// the site that cited it. Both fall straight out of what the language already says about where a
/// declaration belongs — nothing here has to say it a second time.
/// </para>
/// <para>
/// ⚠ The blocks are gathered with AstSearch, which does NOT look inside one — see the note on
/// that exception. A block declared and never cited places nothing, which is the whole point of
/// there being a `Cite` at all.
/// </para>
/// </remarks>
public static class CiteExpansion
{
    /// <summary>
    /// Hands back the very same list when the program has no cufet blocks and no citations, which
    /// is the usual case.
    /// </summary>
    public static IReadOnlyList<IStatement> Expand(IReadOnlyList<IStatement> statements)
    {
        var blocks = new Dictionary<string, CufetAxiomDefinition>(StringComparer.Ordinal);
        // The cufet axioms that say what they give back — lowered to functions by the parser, and
        // gathered here only so that citing one can say why it cannot be cited.
        var runnable = new HashSet<string>(StringComparer.Ordinal);
        bool cited = false;
        foreach (var statement in AstSearch.EveryStatement(statements))
            switch (statement)
            {
                case BindStatement { FromCufetAxiom: true } run:
                    runnable.Add(run.Name);
                    break;

                case CufetAxiomDefinition block:
                    // ⚠ Refused rather than shadowed. Every other redeclaration in this language
                    // has an answer already — `Define a shadow`, or last-wins for a type — and both
                    // are about a NAME that holds something. This name holds source waiting to be
                    // placed, and two blocks under it would leave every `Cite` of it ambiguous at a
                    // glance, which is the one thing a placement keyword cannot afford to be.
                    if (blocks.TryGetValue(block.Name, out var first))
                        throw TypeChecker.TypeError(
                            $"there is already cufet source called '{block.Name}', on line {first.Line}",
                            "A name holds one block, because a 'Cite' of it has to say which one "
                          + "without looking anywhere else",
                            block.Line, block.Column,
                            $"declare a second block called '{block.Name}'",
                            "Give this one another name, or fold the two together.");
                    blocks[block.Name] = block;
                    break;

                case CiteStatement:
                    cited = true;
                    break;
            }

        if (blocks.Count == 0 && !cited) return statements;

        foreach (var block in blocks.Values) RequireDeclarationsOnly(block);

        return AstRebuilder.Apply(statements, type => type, splice: statement =>
            statement is CiteStatement cite ? Held(blocks, runnable, cite) : null);
    }

    /// <summary>Takes the blocks themselves out, once everything that reads one has run.</summary>
    /// <remarks>
    /// ★ Separate from <see cref="Expand"/>, and after the checker rather than before it, because
    /// a block still has ONE thing to answer for once its contents have been placed: that its
    /// language is pulled around it. That is a question about scope, and only the checker can ask
    /// it — so the block stays until it has.
    ///
    /// ⚠ An empty splice is how a statement is removed here. It is the same rule that lets no
    /// template survive: what a backend cannot meet, it cannot get wrong.
    /// </remarks>
    public static IReadOnlyList<IStatement> WithoutBlocks(IReadOnlyList<IStatement> statements) =>
        AstRebuilder.Apply(statements, type => type,
            splice: statement => statement is CufetAxiomDefinition ? [] : null);

    private static IReadOnlyList<IStatement> Held(
        Dictionary<string, CufetAxiomDefinition> blocks,
        HashSet<string> runnable,
        CiteStatement cite)
    {
        if (blocks.TryGetValue(cite.Name, out var block)) return block.Body;

        // ⚠ The name IS declared, just not as source — saying "there is no cufet source called
        // 'two'" and telling the writer to declare it would send them to fix a line that is
        // already right. Which of the two a cufet axiom is comes from one place: whether it says
        // what it gives back.
        if (runnable.Contains(cite.Name))
            throw TypeChecker.TypeError(
                $"'{cite.Name}' says what it gives back, so it is something you run rather than "
              + "source to cite",
                "A cufet axiom that names a result is a body with a name — the same rule the "
              + "c-language tag follows",
                cite.Line, cite.Column,
                $"cite '{cite.Name}'",
                $"Call it: 'cast {cite.Name} on (...)'. Only a block that says nothing about a "
              + "result is cited.");

        throw TypeChecker.TypeError(
            $"there is no cufet source called '{cite.Name}' to cite",
            "'Cite' places what a cufet block holds, and the name is the block's, not a "
          + "variable's",
            cite.Line, cite.Column,
            $"cite '{cite.Name}'",
            $"Declare it first: 'Define cufet {cite.Name} as [ ... ].'");
    }

    /// <summary>Refuses a block holding anything but a declaration.</summary>
    /// <remarks>
    /// ★ Objects and interfaces are what a block holds, and that is not an arbitrary stopping
    /// point. Both are hoisted to the program wherever they are written, and both are checked in a
    /// scope of their own — so a cited one cannot see a local at the site that cited it, whatever
    /// that site is, and the question of what a free name inside a block means never arises. A
    /// `Bind` or a `Define` can see one, and giving those the scope they should have is work of its
    /// own rather than a line here.
    ///
    /// ⚠ Checked for EVERY block, cited or not. A block is source the writer meant, and finding
    /// out it could never be placed at the moment someone first cites it would be a message about
    /// the wrong line.
    /// </remarks>
    private static void RequireDeclarationsOnly(CufetAxiomDefinition block)
    {
        // ⚠ The same refusal the c-language tag makes, for the same reason: a parameter's value
        // comes from a call, and nothing calls source — it is placed. Said here rather than left to
        // the checker because a `Cite` of this name would otherwise report first, and "there is no
        // cufet source called 'shape'" is a true sentence about the wrong line.
        if (block.HasParameterClause)
            throw TypeChecker.TypeError(
                $"'{block.Name}' says nothing about what it gives back, so it is source rather "
              + "than something to run — and source takes no parameters",
                "A parameter's value comes from a call, and nothing calls this: it is placed where "
              + "a 'Cite' says",
                block.Line, block.Column,
                $"give '{block.Name}' parameters",
                $"Drop the 'given' clause — or say what running it gives back, as in "
              + $"'Define cufet number {block.Name}, given (…), as [ … ].'");

        foreach (var statement in block.Body)
        {
            if (statement is ObjectDefinition or InterfaceDefinition) continue;
            var (line, column) = PositionOf(statement, block.Line, block.Column);
            throw TypeChecker.TypeError(
                $"cufet source holds declarations, and this is not one",
                "A cited block places what it declares where the 'Cite' is. An object and an "
              + "interface are what it can declare so far",
                line, column,
                $"put this inside '{block.Name}'",
                "Move it out of the block, or declare an object or an interface here.");
        }
    }

    /// <summary>Where a statement is, or where the block is when the statement does not say.</summary>
    /// <remarks>
    /// ⚠ Reflective, for the reason every whole-tree question here is: ten statement kinds carry
    /// no position at all, and a switch listing the ones that do is a list that goes stale in
    /// silence. The block's own position is the honest fallback — it is at least the right region
    /// of the right file, which is more than a zero would be.
    /// </remarks>
    private static (int Line, int Column) PositionOf(
        IStatement statement, int fallbackLine, int fallbackColumn)
    {
        var type = statement.GetType();
        return type.GetProperty("Line")?.GetValue(statement) is int line
            && type.GetProperty("Column")?.GetValue(statement) is int column
            ? (line, column)
            : (fallbackLine, fallbackColumn);
    }
}
