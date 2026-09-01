using Cufet.Lexer;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter;

/// <summary>
/// Where a line in the checked program actually came from, when it came from another file.
/// </summary>
/// <remarks>
/// <para>
/// ⭐⭐ A loaded file is lexed at an OFFSET into a virtual line space, so the whole program keeps
/// one line numbering and nothing downstream learns that several files exist. Tokens, AST nodes
/// and exceptions carry a line and a column and no file — adding one to all three would touch
/// every position in the front end, and this needs neither.
/// </para>
/// <para>
/// ★ The reporter is the only thing that reads this. It turns a virtual line back into the file
/// and line a person can open, which is the one place the answer is needed.
/// </para>
/// </remarks>
public sealed class SourceMap
{
    /// <summary>Lines below this belong to the file the program was started from.</summary>
    /// <remarks>
    /// ⚠ A round number well past any real file's length, so a loaded file's lines can never be
    /// confused for the host's. A 100,000-line Cufet file is not a thing, and if it ever is, the
    /// symptom is a wrong filename in an error rather than a wrong answer.
    /// </remarks>
    public const int Stride = 100_000;

    internal readonly List<(int Start, string Path)> _blocks = [];

    /// <summary>The map for the program currently being checked, if it loaded anything.</summary>
    /// <remarks>
    /// ⚠ One program is checked per process in the CLI, which is the only caller that reports
    /// positions. A test that checks a program without a source directory loads nothing and
    /// leaves this null.
    /// </remarks>
    public static SourceMap? Current { get; set; }

    internal int Add(string path)
    {
        int start = Stride * (_blocks.Count + 1);
        _blocks.Add((start, path));
        return start;
    }

    /// <summary>The line to PRINT for a virtual line — the local one, when it came from a book.</summary>
    /// <remarks>
    /// ⚠⚠ Messages name the line in their prose as well as in the reporter’s header, and the two
    /// contradicting each other is worse than either being wrong alone. Every message built from
    /// a position goes through here.
    /// </remarks>
    public static int Display(int line) => Current?.Resolve(line)?.Line ?? line;

    /// <summary>A finished message with every virtual line in it turned back into a local one.</summary>
    /// <remarks>
    /// <para>
    /// ★★ Applied to the COMPOSED message rather than at each position, because 163 places in
    /// the front end put a line number into prose — "It was already declared on line 3", "you
    /// declared it on line 5" — and every one of them would otherwise print a number out of a
    /// file that does not exist. One funnel, not 163 edits.
    /// </para>
    /// <para>
    /// ⚠ Only a number that falls inside an ALLOCATED block is touched. A program that loaded
    /// nothing has no blocks, so this cannot change a message — and a number that happens to be
    /// large is left alone unless a book really was loaded at that offset.
    /// </para>
    /// </remarks>
    public static string Rewrite(string message)
    {
        if (Current is null || Current._blocks.Count == 0) return message;
        return System.Text.RegularExpressions.Regex.Replace(
            message, "[0-9]{6,}",
            m => int.TryParse(m.Value, out int n) && Current.Resolve(n) is { } at
                 ? at.Line.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 : m.Value);
    }

    /// <summary>The file and line a virtual line stands for, or null when it is the host's own.</summary>
    public (string Path, int Line)? Resolve(int line)
    {
        foreach (var (start, path) in _blocks)
            if (line >= start && line < start + Stride)
                return (path, line - start);
        return null;
    }
}

/// <summary>
/// Brings a book that lives in another file into the program, before anything looks for it.
/// </summary>
/// <remarks>
/// <para>
/// ⭐⭐ A front-end pass, ahead of the hoist and ahead of `Cite`, the same arrangement
/// <see cref="CiteExpansion"/> and <see cref="DispatchExpansion"/> use. After it, a loaded file is
/// not a thing that exists: its statements are the program's statements, and everything downstream
/// — the hoist, the checker, both backends — meets one program that happens to be longer.
/// </para>
/// <para>
/// ★★ It is not a new mechanism. `Pull a book on ‹name›.` already resolves a bundled book, and
/// `Pull ‹name›.` already resolves a module defined in the file; a module is an object claiming
/// the `module` interface, and a book is a module the language ships with. All this adds is a
/// third place to look for the same object — a file beside the one being run. The namespace, the
/// member access and the scope ending at `Done.` are already decided and already shipped.
/// </para>
/// <para>
/// ⚠ Resolved and compiled TOGETHER, never separately. Whole-program visibility is what lets
/// dispatch prove coverage over every version of a name, bounds the open-union tag set, and lets a
/// generic be monomorphized from every filling the program contains. Compiling files independently
/// would reopen all three, and buys only build speed — see the deferred entry.
/// </para>
/// </remarks>
public static class BookLoading
{
    /// <summary>
    /// Hands back the very same list when nothing pulls a book that is not already here, which is
    /// every single-file program.
    /// </summary>
    public static IReadOnlyList<IStatement> Expand(
        IReadOnlyList<IStatement> statements, string? directory, SourceMap map,
        Func<string, bool> alreadyKnown)
    {
        if (directory is null) return statements;

        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var brought = new List<IStatement>();
        Gather(statements, directory, map, alreadyKnown, loaded, brought, []);
        if (brought.Count == 0) return statements;

        // ⚠ BEFORE the host's own statements. Functions are hoisted, so order does not decide what
        // can call what — but a definition reading in the order it is depended on is what a person
        // expects when they open the spliced program to find out what ran.
        brought.AddRange(statements);
        return brought;
    }

    private static void Gather(
        IReadOnlyList<IStatement> statements, string directory, SourceMap map,
        Func<string, bool> alreadyKnown, HashSet<string> loaded, List<IStatement> brought,
        List<string> chain)
    {
        foreach (var statement in AstSearch.EveryStatement(statements))
        {
            // ⚠ EITHER spelling reaches a file. The form says what the thing IS — a book you
            // consult, or a module you hold one of — and where it happens to live is a separate
            // question. Gating the loader on the book form made one word carry both, so a module
            // could only be split into its own file by calling it something it was not.
            if (statement is not PullStatement pull) continue;

            foreach (var (bookName, _) in pull.Books)
            {
                if (alreadyKnown(bookName)) continue;

                // ★ A cycle is refused rather than quietly stopped, and this is checked BEFORE
                // the already-loaded test — otherwise a ring looks exactly like the ordinary case
                // of two books both pulling a third, and is skipped in silence.
                if (chain.Contains(bookName, StringComparer.OrdinalIgnoreCase))
                    throw TypeChecker.TypeError(
                        $"the book '{bookName}' pulls itself, round a ring",
                        $"The ring is {string.Join(" → ", chain.Append(bookName))}",
                        pull.Line, pull.Column,
                        $"pull '{bookName}' from inside itself",
                        "Break the ring — move what both of them need into a third book they can "
                      + "each pull.");

                // Loaded once, however many books pull it — a diamond is not a ring.
                if (!loaded.Add(bookName)) continue;

                var path = Path.Combine(directory, bookName + ".cufe");
                if (!File.Exists(path))
                {
                    // ⚠ Not an error HERE. A name that is neither bundled, nor defined, nor a file
                    // is refused by the checker, which already says what is available and how to
                    // define one — and says it about every kind of pull, not just this one.
                    loaded.Remove(bookName);
                    continue;
                }

                string text;
                try { text = File.ReadAllText(path); }
                catch (IOException e)
                {
                    throw TypeChecker.TypeError(
                        $"the book '{bookName}' is there but could not be read",
                        e.Message, pull.Line, pull.Column,
                        $"pull '{bookName}'",
                        "Check the file's permissions.");
                }

                // ★★ Lexed at an OFFSET, which is what keeps its errors pointing at it. The lexer
                // has taken a line offset since the `cufet` axiom arc, for the same reason: text
                // lexed on its own reports positions in a file that does not exist.
                int offset = map.Add(Path.GetFullPath(path));
                var inner = new Parser(new CufetLexer(text, offset, 0).Tokenize()).Parse();

                chain.Add(bookName);
                Gather(inner.Statements, directory, map, alreadyKnown, loaded, brought, chain);
                chain.RemoveAt(chain.Count - 1);

                brought.AddRange(MakePrivate(inner.Statements, bookName));
            }
        }
    }

    /// <summary>
    /// Puts everything a loaded file declares BESIDE its modules out of the host’s reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★★ A file is what does the hiding, and no marker does. A module’s members are reached
    /// through its name because a module is an object; everything else in the file is the
    /// author’s own working material, and pulling their book should not hand you it. This is the
    /// case that already bit once — an overflow-guarded multiply wanted in two places inside
    /// `math`, which was inlined twice rather than become a permanent public member.
    /// </para>
    /// <para>
    /// ★ Done by RENAMING to something unwritable, which is the trick monomorphization and
    /// dispatch versions both use — a space cannot appear in an identifier. So there is no new
    /// scope for the checker to learn: the host cannot name what it cannot spell, and the file’s
    /// own references were renamed with it.
    /// </para>
    /// <para>
    /// ⚠ The rewrite rides on AstSearch, which walks every property of every node by reflection
    /// rather than by a hand-written list. A walk that forgets a node kind would leave a reference
    /// pointing at a name that no longer exists — loud at check time, which is the direction to
    /// fail in, but the reflection walk means it cannot happen at all.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<IStatement> MakePrivate(
        IReadOnlyList<IStatement> statements, string bookName)
    {
        // What the host is meant to see: the modules. Everything else the file declares at its
        // top level is its own.
        var hidden = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            string? name = statement switch
            {
                ObjectDefinition o when !TypeChecker.IsModuleConformer(o.ConformedInterfaces) => o.Name,
                BindStatement { UntoType: null } b => b.Name,
                DefineStatement d => d.Name,
                // ⚠ An interface was the one declaration kind missing here, so one written beside a
                // module escaped the file it belongs to — silently, and against the rule stated just
                // above. Nobody decided that; the switch simply had no case for it.
                InterfaceDefinition i => i.Name,
                _ => null,
            };
            // A space keeps it unwritable, and naming the book keeps two books’ helpers apart.
            if (name is not null) hidden[name] = $"{name} in {bookName}";
        }
        if (hidden.Count == 0) return statements;

        // Types first — AstSearch deliberately does not descend into a CufetType, so the two
        // rewrites do not overlap.
        var rebuilt = AstRebuilder.Apply(statements,
            t => AstRebuilder.SubstituteDeep(t, leaf => leaf switch
            {
                ObjectType o when hidden.TryGetValue(o.Name, out var to)
                    => new ObjectType(to, o.PositionalTypes, o.NamedFields, o.Methods),
                // An interface is a type as well as a declaration — `given (the speaker who)`.
                InterfaceType i when hidden.TryGetValue(i.Name, out var ito) => new InterfaceType(ito),
                _ => leaf,
            }));

        AstSearch.Visit(rebuilt, node =>
        {
            switch (node)
            {
                case VariableReference v when hidden.TryGetValue(v.Name, out var to):
                    v.Name = to; break;
                case ObjectLiteral lit when hidden.TryGetValue(lit.TypeName, out var to):
                    lit.TypeName = to; break;
            }
        });

        // The declarations themselves, renamed to match what now refers to them.
        var renamed = new List<IStatement>(rebuilt.Count);
        foreach (var statement in rebuilt)
            renamed.Add(statement switch
            {
                // ⚠ ConformedInterfaces is a list of STRINGS, so neither the type substitution nor
                // the reflective walk reaches it — renaming a private interface without this left the
                // file unable to use its OWN, claiming to satisfy something no longer defined.
                ObjectDefinition o => o with
                {
                    Name = hidden.TryGetValue(o.Name, out var to) ? to : o.Name,
                    ConformedInterfaces = [.. o.ConformedInterfaces.Select(
                        name => hidden.TryGetValue(name, out var ito) ? ito : name)],
                },
                BindStatement b when hidden.TryGetValue(b.Name, out var to) => b with { Name = to },
                DefineStatement d when hidden.TryGetValue(d.Name, out var to) => d with { Name = to },
                InterfaceDefinition i when hidden.TryGetValue(i.Name, out var to) => i with { Name = to },
                _ => statement,
            });
        return renamed;
    }
}
