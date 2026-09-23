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

    /// <summary>Every FILE this program loaded, in the order the loader reached them.</summary>
    /// <remarks>
    /// ★ The map already knew this — it records a path per block so the reporter can turn a virtual
    /// line back into a file. Reading it out is what `cufet pulls` needs and nothing more: no new
    /// walk, no second resolution, and no way for the answer to disagree with what was loaded.
    /// <para>
    /// ⚠ FILES ONLY, by construction. A bundled book is spliced in without ever being a path, so it
    /// never reaches this list — which is exactly right for a build, where a `need` has to be
    /// something that can be hashed.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> LoadedFiles => [.. _blocks.Select(b => b.Path)];

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
        // ⚠⚠ CAPTURED ONCE, and that is a fix rather than a tidy-up. This used to test `Current`
        // for null and then read it again inside the lambda — a check-then-use on a static
        // mutable field, which is a race the moment more than one program is checked at a time.
        //
        // The field's own remark says "one program is checked per process in the CLI", and that is
        // true of the CLI and false of `dotnet test`, which checks thousands of programs in one
        // process with test classes running in parallel. MEASURED: a checker error whose message
        // contained a seven-digit number threw NullReferenceException in a full-solution run and
        // passed every time it was run alone — because only a message with SIX OR MORE digits in
        // it ever enters the lambda at all.
        var map = Current;
        if (map is null || map._blocks.Count == 0) return message;
        return System.Text.RegularExpressions.Regex.Replace(
            message, "[0-9]{6,}",
            m => int.TryParse(m.Value, out int n) && map.Resolve(n) is { } at
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
    /// <summary>The file whose presence marks the root of a project.</summary>
    /// <remarks>
    /// ★★ Its LOCATION is what is read, never its contents. Every tool that resolves a pull —
    /// `check`, the editor, `run` on a single file — needs the project root, and none of them
    /// should have to EXECUTE a build description to import a book. Finding a file is a walk up
    /// the tree; running one is arbitrary code.
    /// </remarks>
    public const string BlueprintFile = "blueprint.cufe";

    /// <summary>Where a project keeps books that more than one of its programs pulls.</summary>
    /// <remarks>
    /// ⚠ A fixed name, and deliberately the same kind of decision as <see cref="BlueprintFile"/>
    /// rather than a new one. Without it the shared place is the project root itself, and a root
    /// holding a build description plus every library file is what a package manager would then
    /// install into.
    /// </remarks>
    public const string SharedFolder = "books";

    /// <summary>The nearest directory at or above this one holding a blueprint, or null.</summary>
    /// <remarks>
    /// ⚠ No blueprint above the file means NO PROJECT, and everything gated on this is then
    /// exactly what it was before any of it existed: the pulling file's own directory and nowhere
    /// else, and no neighbours. A loose `.cufe` in a downloads folder keeps working and keeps
    /// meaning the same thing — and so does every file under `examples/`, which is why the
    /// directory-namespace rule could land without touching the corpus.
    /// </remarks>
    public static string? ProjectRoot(string? directory)
    {
        if (directory is null) return null;
        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(Path.GetFullPath(directory)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }

        for (; dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, BlueprintFile))) return dir.FullName;
        return null;
    }

    /// <summary>
    /// The project's shared book folder, or null when there is no project or no such folder.
    /// </summary>
    /// <remarks>
    /// ⚠ The NEAREST blueprint ancestor wins and is the only one asked — a project inside a
    /// project consults its own `books/` and never the outer one's.
    /// </remarks>
    public static string? SharedBooks(string? directory)
    {
        if (ProjectRoot(directory) is not { } root) return null;
        var books = Path.Combine(root, SharedFolder);
        return Directory.Exists(books) ? books : null;
    }

    /// <summary>The file a pull names, looked for beside the puller and then in the project's.</summary>
    /// <remarks>
    /// ★★ LOCAL WINS. A program may keep its own `canvas.cufe` beside it and have that one rather
    /// than the project's, which is what makes the shared folder a default instead of a ceiling.
    /// </remarks>
    private static string? Resolve(string bookName, string directory, string? shared)
    {
        var beside = Path.Combine(directory, bookName + ".cufe");
        if (File.Exists(beside)) return beside;
        if (shared is null) return null;
        var atRoot = Path.Combine(shared, bookName + ".cufe");
        return File.Exists(atRoot) ? atRoot : null;
    }

    /// <summary>
    /// Brings in the other files of this file's DIRECTORY, when the directory is inside a project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⭐⭐ **A directory is a namespace, and its files share one set of names.** SETTLED
    /// 2026-09-20 in `docs/DESIGN.md` after `tools/snake` met the module system three times in a
    /// session. A folder's files already ARE a group of declarations under one name; a `Pull`
    /// between two of them says that a second time, and saying it twice is what this removes.
    /// Nothing is renamed and nothing is hidden — neighbours are one scope by definition, which is
    /// the whole difference between this and <see cref="MakePrivate"/>.
    /// </para>
    /// <para>
    /// ⚠⚠ GATED ON A BLUEPRINT ABOVE, and that gate is what makes the rule safe to land. A file
    /// with no project over it has no neighbours, so every `.cufe` under `examples/` behaves
    /// exactly as it did — which matters, because top-level name collisions between files in one
    /// directory are everywhere there (`play` in five files of `examples/parsing` alone) and not
    /// one of those directories is a project.
    /// </para>
    /// <para>
    /// ★★ ONLY THE FILE YOU RUN RUNS. A neighbour contributes its top-level DECLARATIONS and
    /// nothing else — see <see cref="DeclarationsOnly"/>. That is not the same decision as
    /// <see cref="RefuseAProgram"/>, and deliberately: a pulled file is a LIBRARY and may not be a
    /// program, but two programs sharing a directory is the ordinary case — `tools/shell.cufe` and
    /// `tools/repl.cufe` both start something on their last line, and refusing that would make the
    /// namespace unusable for the very tree it was designed for.
    /// </para>
    /// <para>
    /// ⚠ `blueprint.cufe` is in the directory and is NOT in the namespace, in either direction: it
    /// is never brought in as a neighbour, and checking it brings in nobody. It describes the
    /// project rather than belonging to it, and letting it join would mean `cufet build` parsed
    /// every file in the root to read a plan.
    /// </para>
    /// <para>
    /// ★ Neighbours are taken in FILENAME ORDER, ordinal. The order cannot change what a program
    /// means — declarations are hoisted — but it decides which file a collision refusal calls the
    /// first one, and two backends must agree about that.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<IStatement> Neighbours(
        IReadOnlyList<IStatement> statements, string? sourceFile, SourceMap map)
    {
        if (sourceFile is null) return statements;

        string self;
        try { self = Path.GetFullPath(sourceFile); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return statements; }

        if (string.Equals(Path.GetFileName(self), BlueprintFile, StringComparison.OrdinalIgnoreCase))
            return statements;

        var directory = Path.GetDirectoryName(self);
        if (directory is null || ProjectRoot(directory) is not { } root) return statements;

        // ── Its own directory: one flat scope, nothing renamed ───────────────
        //
        // The file being checked claims its own names first, so a collision is always reported
        // against the file a person is actually looking at.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _, _) in TopLevelNames(statements)) claimed[name] = self;

        var here = Gathered(directory, map, self, claimed);

        // ── The directories this program QUALIFIES, and the ones they do ─────
        //
        // ★ Loaded ON DEMAND rather than wholesale, the same way a pull is: a project may hold a
        // hundred directories, and a program naming none of them must pay for none of them. The
        // loop runs to a fixpoint because a loaded namespace may qualify a third.
        var everyNamespace = Namespaces(root);
        var loaded = new Dictionary<string, IReadOnlyList<IStatement>>(StringComparer.OrdinalIgnoreCase);
        var ownName = Path.GetFileName(directory);

        var frontier = new List<IReadOnlyList<IStatement>> { statements, here };
        while (frontier.Count > 0)
        {
            var wanted = new SortedDictionary<string, (int Line, int Column)>(StringComparer.Ordinal);
            foreach (var group in frontier)
                foreach (var (name, line, column) in Qualifiers(group))
                    if (!loaded.ContainsKey(name)
                        && !name.Equals(ownName, StringComparison.OrdinalIgnoreCase)
                        && everyNamespace.ContainsKey(name)
                        && !wanted.ContainsKey(name))
                        wanted[name] = (line, column);

            frontier = [];
            foreach (var (name, at) in wanted)
            {
                var directories = everyNamespace[name];

                // ⚠ Refused HERE rather than where the duplicate was found, so an unused clash
                // breaks nobody and the message has a line to point at.
                if (directories.Count > 1)
                    throw TypeChecker.TypeError(
                        $"two directories of this project are both named '{name}'",
                        $"They are {string.Join(" and ", directories.Select(d => $"'{d}'"))}, and "
                      + "a directory is a namespace named after itself — so this qualification "
                      + "would have two meanings",
                        at.Line, at.Column,
                        $"qualify with '{name}'",
                        "Rename one of them. A qualifier reaches one directory, and which one has "
                      + "to be decidable from the name alone.");

                var gathered = Gathered(
                    directories[0], map, skipFile: null,
                    claimed: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                loaded[name] = gathered;
                frontier.Add(gathered);
            }
        }

        if (loaded.Count == 0)
        {
            if (here.Count == 0) return statements;
            var only = new List<IStatement>(here);
            only.AddRange(statements);
            return only;
        }

        // ── The rename, and the qualifications that now point at it ──────────
        //
        // ⚠⚠ ONE MAP FOR ALL OF THEM, built before anything is rewritten. Two namespaces may
        // qualify each other, and rewriting one at a time would leave whichever went first
        // pointing at short names the second had already renamed away.
        var reach = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, gathered) in loaded)
        {
            var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (member, _, _) in TopLevelNames(gathered))
                members[member] = ModuleTypeLifting.LiftedName(name, member);
            reach[name] = members;
        }

        foreach (var group in loaded.Values.Append(here).Append(statements))
        {
            RefuseAShadowedNamespace(group, everyNamespace, ownName);
            RefuseAMemberTheDirectoryHasNot(group, reach);
        }

        var brought = new List<IStatement>();
        foreach (var (name, gathered) in loaded.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            brought.AddRange(MakePrivate(Qualify(gathered, reach), name, exemptModules: false));

        brought.AddRange(Qualify(here, reach));
        brought.AddRange(Qualify(statements, reach));
        return brought;
    }

    /// <summary>Refuses a declaration that takes a name a directory of this project already has.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ THIS IS WHAT MAKES <see cref="Qualify"/> SOUND, and it is not tidiness. That rewrite runs
    /// before any scope exists, so it cannot tell the directory `terminals` from a name that
    /// happens to be spelled the same — and if both could exist, `terminals's read-key` would have
    /// two readings with nothing to choose between them. Removing one of the two readings is
    /// cheaper and far clearer than teaching a pre-hoist pass about scope.
    /// </para>
    /// <para>
    /// ⚠ A file's OWN directory is exempt, because its name is never a qualifier from inside it —
    /// there is nothing to be ambiguous with.
    /// </para>
    /// <para>
    /// ⚠ Declarations and `Define`s, not every binding form. A PARAMETER or a loop variable named
    /// after a directory is still capturable, and is left as a known gap rather than met with a
    /// walk over every name-introducing node in the language — it needs a member name to collide
    /// as well before anything goes wrong, and no witness has produced one.
    /// </para>
    /// </remarks>
    private static void RefuseAShadowedNamespace(
        IReadOnlyList<IStatement> statements,
        IReadOnlyDictionary<string, IReadOnlyList<string>> everyNamespace, string ownName)
    {
        foreach (var statement in AstSearch.EveryStatement(statements))
        {
            var (name, line, column) = statement switch
            {
                ObjectDefinition o    => (o.Name, o.Line, o.Column),
                InterfaceDefinition i => (i.Name, i.Line, i.Column),
                DefineStatement d     => (d.Name, d.Line, d.Column),
                BindStatement { UntoType: null } b => (b.Name, b.Line, b.Column),
                _ => (null as string, 0, 0),
            };
            if (name is null
                || name.Equals(ownName, StringComparison.OrdinalIgnoreCase)
                || !everyNamespace.ContainsKey(name)) continue;

            throw TypeChecker.TypeError(
                $"'{name}' is a directory of this project, so it cannot also be a name",
                $"A directory is a namespace named after itself, and '{name}' is how anything "
              + $"outside it writes '{name}'s ‹something›' — so the two readings would be "
              + "indistinguishable",
                line, column,
                $"declare '{name}'",
                "Rename this one. The directory's name belongs to the directory.");
        }
    }

    /// <summary>Refuses `‹directory›'s ‹member›` where that directory declares no such thing.</summary>
    /// <remarks>
    /// ★ Without this the reader gets the ordinary unresolved-name refusal about the DIRECTORY —
    /// "'terminals' isn't defined" — which is both false and useless, since the directory is
    /// plainly there. Measured on the first program written against this.
    /// </remarks>
    private static void RefuseAMemberTheDirectoryHasNot(
        IReadOnlyList<IStatement> statements,
        IReadOnlyDictionary<string, Dictionary<string, string>> reach)
    {
        AstSearch.Visit(statements, node =>
        {
            if (node is not PossessiveAccess { Target: VariableReference target } access) return;
            if (!reach.TryGetValue(target.Name, out var members)) return;
            if (members.ContainsKey(access.Member)) return;

            var offered = members.Keys.Order(StringComparer.Ordinal).ToList();
            throw TypeChecker.TypeError(
                $"the directory '{target.Name}' declares nothing called '{access.Member}'",
                offered.Count == 0
                    ? $"'{target.Name}' is a directory of this project and its files declare nothing"
                    : $"What its files declare is {string.Join(", ", offered)}",
                access.Line, access.Column,
                $"reach '{target.Name}'s {access.Member}'",
                "Check the spelling, or declare it in one of that directory's files — everything "
              + "a directory's files declare at their top level is what the directory offers.");
        });
    }

    /// <summary>Every name used as a QUALIFIER in this tree — the `X` of every `X's y`.</summary>
    /// <remarks>
    /// ★ Names, not resolutions. Whether an `X` is a directory is decided by the caller against
    /// the project; this only reports which names were written in the qualifying position, so that
    /// a namespace is loaded on demand rather than every directory being read for every program.
    /// </remarks>
    private static IEnumerable<(string Name, int Line, int Column)> Qualifiers(
        IReadOnlyList<IStatement> statements)
    {
        var found = new List<(string, int, int)>();
        AstSearch.Visit(statements, node =>
        {
            if (node is PossessiveAccess { Target: VariableReference target } access)
                found.Add((target.Name, access.Line, access.Column));
        });
        return found;
    }

    /// <summary>Turns `terminals's read-key` into the one name that declaration now has.</summary>
    /// <remarks>
    /// <para>
    /// ★★ A REWRITE BEFORE THE HOIST, into a name with a space in it — the third pass to do
    /// exactly this, after the loader's file privacy and <see cref="ModuleTypeLifting"/>. After it
    /// nothing downstream learns that directories exist: the checker, the interpreter and the
    /// compiler each meet an ordinary top-level declaration reached by an ordinary name. That is
    /// the whole reason this is a rewrite and not three new arms in three places that could
    /// disagree.
    /// </para>
    /// <para>
    /// ⚠⚠ ONLY WHERE THE NAMESPACE REALLY DECLARES THE MEMBER, and that condition is the safety
    /// story, not an optimisation. This pass runs before any scope exists, so it cannot tell a
    /// directory's name from a LOCAL that happens to share it — and a local named `terminals`
    /// whose object has a `read-key` is the one shape that would be captured wrongly. Requiring
    /// the member to exist narrows that to a coincidence in both halves at once, and the
    /// top-level refusal below removes the half anyone is likely to write.
    /// </para>
    /// <para>
    /// ⚠ A member the namespace does NOT declare is left alone on purpose, so the reader gets the
    /// ordinary "no such member" refusal naming what they wrote, rather than one about a
    /// synthesized name they have never seen.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<IStatement> Qualify(
        IReadOnlyList<IStatement> statements,
        IReadOnlyDictionary<string, Dictionary<string, string>> reach) =>
        AstRebuilder.Apply(
            statements, type => type,
            rewrite: expression =>
                expression is PossessiveAccess { Target: VariableReference target } access
                && reach.TryGetValue(target.Name, out var members)
                && members.TryGetValue(access.Member, out var lifted)
                    ? new VariableReference(lifted, access.Line, access.Column)
                    : null);

    /// <summary>Every directory of a project that holds Cufet, keyed by the name you qualify with.</summary>
    /// <remarks>
    /// <para>
    /// ★★ A DIRECTORY IS THE NAMESPACE, named after itself — so the key is the folder name and
    /// nothing declares it. That is the whole of the rule: a folder's files already are a group of
    /// declarations under one name, and a `Pull` between two of them says it a second time.
    /// </para>
    /// <para>
    /// ⚠ `books/` is NOT one of them, and neither is anything under it. That folder holds someone
    /// else's code, which is what `Pull` still exists for — a qualification reaches inside this
    /// project, and a pull crosses out of it. Keeping the two apart is what makes "no pulls within
    /// a project" a rule you can state.
    /// </para>
    /// <para>
    /// ⚠ Only directories holding a `.cufe` count. `tools/repl` is a build artifact sharing a name
    /// with `tools/repl.cufe`, and an empty folder that happens to be named after something would
    /// otherwise claim a qualifier and answer for nothing.
    /// </para>
    /// <para>
    /// ★ Two directories of one name at different depths would give one qualifier two meanings, so
    /// the SECOND one found refuses rather than winning. Same shape as the repeated top-level name
    /// a directory already refuses, one level up.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Namespaces(string root)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var books = Path.Combine(root, SharedFolder);
        Walk(root);
        return found.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.OrdinalIgnoreCase);

        void Walk(string directory)
        {
            if (string.Equals(directory, books, StringComparison.OrdinalIgnoreCase)) return;

            string[] here, below;
            try
            {
                here  = Directory.GetFiles(directory, "*.cufe");
                below = Directory.GetDirectories(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

            if (here.Any(f => !string.Equals(Path.GetFileName(f), BlueprintFile,
                                             StringComparison.OrdinalIgnoreCase)))
            {
                // ⚠ RECORDED, NOT REFUSED. Two directories of one name is only a problem for
                // somebody who writes that qualifier, and refusing here would break every
                // unrelated program in the project over a name none of them uses. The refusal
                // waits for the ambiguous qualification, where it also has a line to point at.
                var name = Path.GetFileName(directory);
                if (!found.TryGetValue(name, out var already)) found[name] = already = [];
                if (!already.Contains(directory, StringComparer.OrdinalIgnoreCase))
                    already.Add(directory);
            }

            Array.Sort(below, StringComparer.Ordinal);
            foreach (var child in below) Walk(child);
        }
    }

    /// <summary>The statements a directory contributes, as one group, before they are renamed.</summary>
    private static IReadOnlyList<IStatement> Gathered(
        string directory, SourceMap map, string? skipFile, Dictionary<string, string> claimed)
    {
        string[] files;
        try { files = Directory.GetFiles(directory, "*.cufe"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        Array.Sort(files, StringComparer.Ordinal);

        var gathered = new List<IStatement>();
        foreach (var path in files)
        {
            if (skipFile is not null && string.Equals(path, skipFile, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(Path.GetFileName(path), BlueprintFile, StringComparison.OrdinalIgnoreCase))
                continue;

            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException e) { throw Unreadable(Path.GetFileName(path), e.Message); }

            int offset = map.Add(path);
            var inner = new Parser(new CufetLexer(text, offset, 0).Tokenize()).Parse();

            foreach (var (name, line, column) in TopLevelNames(inner.Statements))
            {
                if (!claimed.TryGetValue(name, out var already))
                { claimed[name] = path; continue; }

                throw TypeChecker.TypeError(
                    $"'{name}' is declared in two files of one directory",
                    $"'{Path.GetFileName(already)}' declares it too, and a directory is one "
                  + "namespace — its files share a single set of names, with nothing between them "
                  + "to keep two apart",
                    line, column,
                    $"declare '{name}' in '{Path.GetFileName(path)}' as well",
                    "Rename one of them, or move one of the two files into a directory of its "
                  + "own — a directory is what separates one set of names from another.");
            }

            gathered.AddRange(DeclarationsOnly(inner.Statements));
        }
        return gathered;
    }

    private static TypeException Unreadable(string file, string why) =>
        TypeChecker.TypeError(
            $"'{file}' is in this directory but could not be read",
            why, 0, 0,
            $"read '{file}' as part of its directory",
            "Check the file's permissions.");

    /// <summary>Every name a file's TOP LEVEL declares, in the sense the hoist means.</summary>
    /// <remarks>
    /// ⚠ `FlattenHoistable` is asked rather than walked again, so "top level" here is the same
    /// answer <see cref="RefuseAProgram"/> and the hoist's own duplicate check already give — it
    /// descends through `Pull … Done.`, which `examples/language/pennies.cufe` needs and which a
    /// file keeping its declarations inside a pull (`tools/snake/screen.cufe`) needs too.
    /// <para>
    /// ★ An `unto` method is not a name of its own — it is a member of the type it attaches to,
    /// and two files may legitimately extend two different types with the same member name. Same
    /// exemption <see cref="MakePrivate"/> makes, for the same reason.
    /// </para>
    /// <para>
    /// ⚠ A type a module CARRIES is not here either. It is nested inside the module's definition
    /// rather than a statement, and <see cref="ModuleTypeLifting"/> gives it a name with the
    /// module's in it — so two neighbours may each carry a `spot` without collision.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Name, int Line, int Column)> TopLevelNames(
        IReadOnlyList<IStatement> statements)
    {
        foreach (var statement in TypeChecker.FlattenHoistable(statements))
            switch (statement)
            {
                case ObjectDefinition o:            yield return (o.Name, o.Line, o.Column); break;
                case InterfaceDefinition i:         yield return (i.Name, i.Line, i.Column); break;
                case BindStatement { UntoType: null } b: yield return (b.Name, b.Line, b.Column); break;

                // ⚠⚠ A PLAIN `Define` IS NOT A NAME A DIRECTORY OFFERS, and `permanently` is
                // exactly the word that says otherwise. `AstSearch.EveryStatement` states the
                // distinction this rests on: a TYPE declaration is program-scope wherever it is
                // written, and a VALUE binding is not.
                //
                // ⚠⚠ MEASURED 2026-09-20, and it is what the rule costs if you get it wrong:
                // `tools/shell.cufe` and `tools/repl.cufe` each open their body with
                // `Define asking as cast terminals's interactive.` Counting those made two
                // programs' WORKING VARIABLES a name clash between two libraries — and splicing
                // them would have asked the terminal whether anybody was there at the top of an
                // unrelated program. A program's body is not the directory's vocabulary.
                case DefineStatement { Permanent: true } shared:
                    yield return (shared.Name, shared.Line, shared.Column); break;
            }
    }

    /// <summary>What a neighbour contributes: everything it DECLARES, and nothing it DOES.</summary>
    /// <remarks>
    /// <para>
    /// ★★ The allowlist is <see cref="RefuseAProgram"/>'s, used as a FILTER instead of as a
    /// refusal — so the two answers to "what is a declaration" stay one answer. What differs is
    /// only what happens to the rest: a pulled library may not have any, and a neighbour simply
    /// keeps its own.
    /// </para>
    /// <para>
    /// ⚠ A `Pull … Done.` is KEPT, with its body filtered the same way. It has to be: a file may
    /// hold its whole declaration inside one — `screen.cufe` declares `screen` inside
    /// `Pull a book on board.` so that a signature can name a carried type — and dropping the pull
    /// would drop the declaration with it. What is left is a block that binds a module and runs
    /// nothing, which is what a pull of a book already costs.
    /// </para>
    /// <para>
    /// ⚠ The hole this leaves is the one `RefuseAProgram` names and leaves too:
    /// `Define x as ‹something effectful›` still runs. Closing it needs an effect system, and
    /// nobody has asked for one.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<IStatement> DeclarationsOnly(IReadOnlyList<IStatement> statements)
    {
        var kept = new List<IStatement>();
        foreach (var statement in statements)
            switch (statement)
            {
                case PullStatement pull:
                    kept.Add(pull with { Body = DeclarationsOnly(pull.Body) });
                    break;
                case PullRabbitStatement rabbit:
                    kept.Add(rabbit with { Body = DeclarationsOnly(rabbit.Body) });
                    break;

                // ⚠⚠ The same line as in TopLevelNames, and for a sharper reason here: keeping
                // a plain `Define` would EXECUTE it. A `permanently` constant is a declaration
                // and is kept; everything else is the file's own working material.
                case DefineStatement { Permanent: true }:
                    kept.Add(statement);
                    break;

                case BindStatement or ObjectDefinition or InterfaceDefinition or GetterDeclaration
                  or SetterDeclaration or UnmakerDeclaration or OperatorOverloadDeclaration:
                    kept.Add(statement);
                    break;
            }
        return kept;
    }

    /// <summary>
    /// Hands back the very same list when nothing pulls a book that is not already here, which is
    /// every single-file program.
    /// </summary>
    public static IReadOnlyList<IStatement> Expand(
        IReadOnlyList<IStatement> statements, string? directory, SourceMap map,
        Func<string, bool> alreadyKnown)
    {
        if (directory is null) return statements;

        var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var brought = new List<IStatement>();
        Gather(statements, directory, SharedBooks(directory), map, alreadyKnown, loaded, brought, []);
        if (brought.Count == 0) return statements;

        // ⚠ BEFORE the host's own statements. Functions are hoisted, so order does not decide what
        // can call what — but a definition reading in the order it is depended on is what a person
        // expects when they open the spliced program to find out what ran.
        brought.AddRange(statements);
        return brought;
    }

    /// <param name="directory">
    /// The directory of the file these statements came from — NOT the program's. ⚠⚠ That
    /// distinction is the whole of what makes a book portable: a book's own pulls resolve beside
    /// the BOOK, so a folder holding a book and what it needs works wherever it is dropped. This
    /// used to pass the entry program's directory the whole way down, which meant a book depended
    /// on where whoever used it happened to live — and no book could ever be installed.
    /// </param>
    /// <param name="shared">The project's shared book folder, computed once and passed unchanged.</param>
    private static void Gather(
        IReadOnlyList<IStatement> statements, string directory, string? shared, SourceMap map,
        Func<string, bool> alreadyKnown, Dictionary<string, string> loaded, List<IStatement> brought,
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

                // ⚠ One book per NAME in a program, whichever place it came from. That is not a
                // simplification: `MakePrivate` renames what a book declares to "<name> in <book>",
                // so two files answering to one name would collide on every declaration they have.
                // The name IS the namespace, and local winning above is how a program chooses.
                var path = Resolve(bookName, directory, shared);
                if (path is null)
                    // ⚠ Not an error HERE. A name that is neither bundled, nor defined, nor a file
                    // is refused by the checker, which already says what is available and how to
                    // define one — and says it about every kind of pull, not just this one.
                    continue;

                var from = Path.GetFullPath(path);

                // ★★ LOADED ONCE PER FILE, NOT PER NAME. A diamond is not a ring — and it is
                // not a CONFLICT either, which is the distinction this used to lose. Keyed by name
                // alone, the FIRST pull of a name claimed it and every later one was skipped in
                // silence, however different the file it had resolved to.
                //
                // ⚠⚠ MEASURED 2026-09-16: a library with its own `utils` beside it, and a
                // program with a different `utils` beside THAT, gave a different answer depending
                // on the ORDER the two names were written in — `Pull books on utils, and alpha.`
                // handed the library the program's helper, and swapping the two names handed the
                // program the library's. Exit 0 either way, and identical in both backends, so the
                // oracle was blind to it.
                //
                // ★ Compared case-insensitively on purpose: a program may spell the same book
                // `Utils` in one pull and `utils` in another, and book names ARE case-insensitive,
                // so those must stay one book rather than become a false conflict.
                if (loaded.TryGetValue(bookName, out var alreadyFrom))
                {
                    if (!string.Equals(alreadyFrom, from, StringComparison.OrdinalIgnoreCase))
                        throw TypeChecker.TypeError(
                            $"'{bookName}' is pulled from two different files",
                            $"One is '{alreadyFrom}' and the other is '{from}', and a program holds "
                          + "one book per NAME — what a book declares is renamed to "
                          + "'<name> in <book>', so two files answering to one name would collide "
                          + "on every declaration they have",
                            pull.Line, pull.Column,
                            $"pull '{bookName}' from both",
                            "Rename one of them. A book's name is its namespace, so the language "
                          + "cannot hold two.");
                    continue;
                }
                loaded[bookName] = from;

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

                RefuseAProgram(inner.Statements, bookName, pull);

                chain.Add(bookName);
                // ★★ The BOOK's directory, not the one we arrived from — this is what "beside the
                // pulling file" actually says, applied at every level rather than only the first.
                //
                // ⚠⚠ MEASURED UNOBSERVABLE TODAY, and recorded so nobody reads the test names above
                // as covering it. With exactly two places to look — the puller's directory and the
                // project's shared folder — a book is always FOUND in one of those two, and the
                // shared folder is a fallback at every level anyway. So passing the entry's
                // directory down instead cannot change any answer, and reverting this line leaves
                // the whole suite green. It becomes observable the moment a book can live somewhere
                // that is not itself a search root — an installed book in its own folder with its
                // dependencies beside it, which is the shape a package manager needs.
                Gather(inner.Statements, Path.GetDirectoryName(Path.GetFullPath(path))!, shared,
                       map, alreadyKnown, loaded, brought, chain);
                chain.RemoveAt(chain.Count - 1);

                brought.AddRange(MakePrivate(inner.Statements, bookName));
            }
        }
    }

    /// <summary>Refuses a pulled file whose top level DOES something rather than declaring.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ A PULL RUNS THE LOADED FILE'S TOP LEVEL, and used to do it in SILENCE. MEASURED: a book
    /// file with one `State` line at the bottom printed it when another program pulled it, before
    /// that program's own first line, exit 0, no warning. Pulling somebody's library executed their
    /// program inside your block.
    /// </para>
    /// <para>
    /// ★★ SO A FILE IS A PROGRAM OR A LIBRARY, and pulling decides which one it has to be — the
    /// answer Rust and Go both give, rather than a marker for "only when I am the one being run".
    /// The witness is `tools/shell.cufe`: its machinery is worth pulling and its last line STARTS
    /// THE SHELL, so nothing can borrow it. Splitting such a file needs no language feature, and
    /// inventing one when a split suffices would be a rule wider than its reason.
    /// </para>
    /// <para>
    /// ⚠ The running was never the un-Cufet part — the silence was. This language's claim is that
    /// it refuses clearly and says why, and here it did something surprising without a word.
    /// </para>
    /// <para>
    /// ★ `Define` IS ALLOWED, loose or `permanently`: a constant is how a library is written, and
    /// the witness only asks that ACTIONS be refused. ⚠ That knowingly leaves a hole —
    /// `Define x as &lt;something effectful&gt;` still runs at pull time — and closing it means
    /// deciding what makes an expression effectful, which needs an effect system Cufet does not
    /// have and no witness has asked for.
    /// </para>
    /// <para>
    /// ⚠⚠ IT DESCENDS THROUGH `Pull ... Done.`, by asking
    /// <see cref="TypeChecker.FlattenHoistable"/> rather than walking again. That is not tidiness:
    /// `examples/language/pennies.cufe` keeps its ENTIRE body inside a pull, so a check that
    /// stopped at the outermost statement would refuse a file the corpus already relies on. One
    /// answer to "which scopes is this transparent to", asked where it already lives.
    /// </para>
    /// <para>
    /// ★ MEASURED at zero blast radius when it landed: every pulled file in the tree and every
    /// prelude file was already declarations-only. The rule was universally observed and simply
    /// unenforced.
    /// </para>
    /// </remarks>
    private static void RefuseAProgram(
        IReadOnlyList<IStatement> statements, string bookName, PullStatement pull)
    {
        foreach (var statement in TypeChecker.FlattenHoistable(statements))
        {
            if (statement is BindStatement or ObjectDefinition or InterfaceDefinition
                          or GetterDeclaration or SetterDeclaration or UnmakerDeclaration
                          or OperatorOverloadDeclaration or DefineStatement
                          or PullStatement or PullRabbitStatement)
                continue;

            throw TypeChecker.TypeError(
                $"'{bookName}' does something at its top level, so it cannot be pulled",
                "A pulled file is a LIBRARY: its top level may DECLARE things, but anything that "
              + "runs would run inside your pull, before your own first line",
                pull.Line, pull.Column,
                $"pull '{bookName}'",
                $"Split it in two: leave the declarations in '{bookName}.cufe', and move the lines "
              + "that DO something into a program of their own that pulls it.");
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
    /// <param name="exemptModules">
    /// Whether a module-conforming object keeps its public name — true for a BOOK, whose modules
    /// are the face it offers, and false for a DIRECTORY namespace, which offers every name it
    /// declares and offers all of them the same way. ⚠ A directory reached by qualification has
    /// no public face to preserve: <c>terminals's read-key</c> names the declaration directly, so
    /// leaving anything unrenamed would put it in the qualifier's scope under its short name and
    /// make two directories able to collide.
    /// </param>
    internal static IReadOnlyList<IStatement> MakePrivate(
        IReadOnlyList<IStatement> statements, string bookName, bool exemptModules = true)
    {
        // What the host is meant to see: the modules. Everything else the file declares at its
        // top level is its own.
        //
        // ⚠⚠ "TOP LEVEL" IS WHAT THE HOIST MEANS BY IT, and asking the wrong question here let a
        // file's working material escape. This walked the FLAT list, so a declaration written
        // inside a top-level `Pull … Done.` was never renamed — and a `Bind` there is hoisted to a
        // free function by both backends, so the host could call it by name.
        //
        // MEASURED 2026-09-22 on two books differing only in that wrapper: the flat one's helper
        // was refused with "isn't defined", and the wrapped one's answered 42 to whoever pulled
        // the book. ★ It is not a rare shape — a file keeps its declarations inside a pull
        // precisely so a signature can name a type the pull introduces, which is why
        // `tools/snake/screen.cufe` was written that way.
        //
        // ★ `FlattenHoistable` is the one answer to "which scopes is hoisting transparent to",
        // already public because both backends need it. Asking it cannot drift from the hoist.
        var hidden = new Dictionary<string, string>(StringComparer.Ordinal);
        var flat   = new HashSet<IStatement>(statements, ByReference.Instance);

        foreach (var statement in TypeChecker.FlattenHoistable(statements))
        {
            string? name = statement switch
            {
                ObjectDefinition o when !exemptModules
                                     || !TypeChecker.IsModuleConformer(o.ConformedInterfaces) => o.Name,
                BindStatement { UntoType: null } b => b.Name,
                // ⚠ An interface was the one declaration kind missing here, so one written beside a
                // module escaped the file it belongs to — silently, and against the rule stated just
                // above. Nobody decided that; the switch simply had no case for it.
                InterfaceDefinition i => i.Name,

                // ⚠⚠ A `Define` ONLY AT THE FLAT TOP LEVEL, unlike the three above. The others are
                // program-scope wherever they are written — that is what makes them reachable and
                // therefore what makes hiding them necessary. A plain `Define` inside a pull is a
                // VALUE BINDING living in that block, which the host could not name if it tried:
                // hiding it would rename somebody's local for no reason. Same line the directory
                // namespaces draw, and `AstSearch.EveryStatement` states it.
                DefineStatement d when flat.Contains(d) || d.Permanent => d.Name,
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
        //
        // ⚠⚠ REACHING THE SAME STATEMENTS THE GATHERING DID, which a flat loop did not. The set
        // is taken from `FlattenHoistable` again and matched by REFERENCE, so the two halves ask
        // one question and cannot answer it differently — a declaration inside a top-level pull is
        // renamed exactly when it was hidden.
        //
        // ★ By reference rather than by name, because a name is not unique in a file: a local
        // `Define` inside some function body may be spelled like a hidden helper, and renaming
        // THAT would rewrite a binding the host was never able to reach.
        var toRename = new HashSet<IStatement>(TypeChecker.FlattenHoistable(rebuilt), ByReference.Instance);

        return AstRebuilder.Apply(rebuilt, type => type, replace: statement =>
            !toRename.Contains(statement) ? statement : statement switch
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
    }
}
