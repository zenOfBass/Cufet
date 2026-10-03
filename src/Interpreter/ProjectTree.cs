using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter;

/// <summary>
/// The project's tree, as its blueprint draws it — which folder is a namespace of its own, and
/// which is part of the folder above it.
/// </summary>
/// <remarks>
/// <para>
/// ★★ **A folder is two things without one of these** — how files are organised, and a namespace
/// wall — and at the size of a real program they pull apart. The drawing says, for each folder
/// with Cufet source in it, which one it is:
/// </para>
/// <code>
/// Bind text to tree:
///     Return &lt;&lt;
/// front-end
///     lexer
///     checker
/// snake
/// &gt;&gt;.
/// Done.
/// </code>
/// <para>
/// A folder at the left edge is its own namespace; one indented under another is PART of it — its
/// files count as if they sat in that folder, and its name appears in no qualification.
/// </para>
/// <para>
/// ★★ **Read, never run.** Resolving a name must execute nothing — the editor resolves as you type
/// — which is why a file list was kept out of the blueprint before. The tree is DATA: the body of
/// `tree` must be one `Return` of a written-out text, and that text is parsed here. Anything else
/// in it is refused rather than run.
/// </para>
/// <para>
/// ⚠ **It is the one place indentation means something**, so this reader never guesses: tabs and
/// spaces mixed, an indent matching no level above it, a folder that is not there, a folder drawn
/// twice, and a source folder left out are each refused, on the drawing's own line.
/// </para>
/// <para>
/// ★ **No drawing, no change.** A project whose blueprint has no `tree` keeps every folder its own
/// namespace, exactly as before this existed — nothing has to migrate.
/// </para>
/// </remarks>
public sealed class ProjectTree
{
    /// <summary>The function a blueprint draws its tree in.</summary>
    public const string Entry = "tree";

    // Every drawn folder, full path → the folder whose namespace it belongs to (itself, at the edge).
    private readonly Dictionary<string, string> _namespaceOf = new(StringComparer.OrdinalIgnoreCase);

    private ProjectTree() { }

    /// <summary>The folder whose namespace this one's files belong to — itself unless it is folded.</summary>
    public string NamespaceOf(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        return _namespaceOf.TryGetValue(full, out var owner) ? owner : full;
    }

    /// <summary>Whether this folder is PART of the one above it rather than a namespace.</summary>
    public bool IsFolded(string directory) =>
        !string.Equals(NamespaceOf(directory), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);

    /// <summary>Every folder that makes up this namespace — its own, then those folded into it.</summary>
    /// <remarks>★ In path order, ordinal, so a collision refusal names the same file first on every run.</remarks>
    public IReadOnlyList<string> Folders(string namespaceDirectory)
    {
        var owner = Path.GetFullPath(namespaceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var folders = new List<string> { owner };
        folders.AddRange(_namespaceOf
            .Where(entry => !string.Equals(entry.Key, owner, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(entry.Value, owner, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal));
        return folders;
    }

    /// <summary>The tree this project's blueprint draws, or null when it draws none.</summary>
    /// <remarks>
    /// The blueprint is lexed under its own block of <paramref name="map"/>, so every refusal is
    /// reported against the blueprint's line, not the file being checked.
    /// </remarks>
    public static ProjectTree? Read(string root, SourceMap map)
    {
        var path = Path.Combine(root, BookLoading.BlueprintFile);
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return null; }

        // ★ Most blueprints draw nothing; one without the word cannot, and is not parsed for it.
        if (!text.Contains(Entry, StringComparison.OrdinalIgnoreCase)) return null;

        int offset = map.Add(path);
        var program = new Parser(new CufetLexer(text, offset, 0).Tokenize()).Parse();
        var bind = Hoistable(program.Statements)
            .OfType<BindStatement>()
            .FirstOrDefault(b => b.UntoType is null && string.Equals(b.Name, Entry, StringComparison.Ordinal));
        if (bind is null) return null;

        if (bind.ReturnType is not TextType
            || bind.Body is not [ReturnStatement { Value: StringLiteral drawing } ret])
            throw TypeChecker.TypeError(
                "the blueprint's 'tree' has to be written out, not worked out",
                "The tools read the tree without running anything, so its body can only give back "
              + "the drawing itself",
                bind.Line, bind.Column,
                "work out the tree",
                "Write it as 'Bind text to tree:', then 'Return <<', a line for each folder, and '>>.'.");

        // Where the drawing starts: the `<<` after the `Return`, counted in the blueprint's own lines.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int returnLine = ret.Line - offset;                 // 1-based, in the blueprint itself
        int openLine = returnLine;
        for (int i = returnLine - 1; i < lines.Length; i++)
            if (lines[i].Contains("<<", StringComparison.Ordinal)) { openLine = i + 1; break; }

        var tree = new ProjectTree();
        tree.Draw(drawing.Value, root, offset + openLine);
        tree.RequireEverySourceFolder(root, offset + openLine);
        return tree;
    }

    private void Draw(string drawing, string root, int firstLine)
    {
        var drawn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // folder → line
        var levels = new List<(int Width, string Folder, string Namespace)>();
        char? indentWith = null;

        var lines = drawing.Replace("\r\n", "\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            if (raw.Trim().Length == 0) continue;
            int line = firstLine + index;

            int width = raw.Length - raw.TrimStart(' ', '\t').Length;
            var name = raw.Trim();
            var indent = raw[..width];

            foreach (var ch in indent)
            {
                indentWith ??= ch;
                if (ch != indentWith)
                    throw TypeChecker.TypeError(
                        "the tree's drawing mixes tabs and spaces",
                        null, line, 1,
                        $"indent '{name}' with {(ch == '\t' ? "a tab" : "spaces")}",
                        "Indent the whole drawing one way — all spaces or all tabs — so a folder's "
                      + "depth means one thing.");
            }

            string folder, owner;
            if (width == 0)
            {
                levels.Clear();
                folder = Path.Combine(root, name);
                owner  = folder;
            }
            else
            {
                bool dedented = false;
                while (levels.Count > 0 && levels[^1].Width > width) { levels.RemoveAt(levels.Count - 1); dedented = true; }
                if (levels.Count > 0 && levels[^1].Width == width) levels.RemoveAt(levels.Count - 1);
                else if (dedented)
                    throw TypeChecker.TypeError(
                        $"'{name}' is indented to a depth no folder above it has",
                        null, line, width + 1,
                        $"indent '{name}' between two levels",
                        "Line it up with the folder it sits beside, or indent it further than the "
                      + "folder it belongs to.");
                if (levels.Count == 0)
                    throw TypeChecker.TypeError(
                        $"'{name}' is indented, but there is no folder above it to belong to",
                        null, line, width + 1,
                        $"indent '{name}'",
                        "Start the drawing at the left edge — a folder there is a namespace of its own.");
                var parent = levels[^1];
                folder = Path.Combine(parent.Folder, name);
                owner  = parent.Namespace;
            }

            folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            var shown = Shown(root, folder);
            if (!Directory.Exists(folder))
                throw TypeChecker.TypeError(
                    $"the tree draws '{shown}', but there is no such folder",
                    null, line, width + 1,
                    $"draw '{shown}'",
                    "Create the folder, or take its line out — the tree describes the folders the "
                  + "project has; it does not make them.");
            if (drawn.TryGetValue(folder, out var first))
                throw TypeChecker.TypeError(
                    $"the tree draws '{shown}' twice",
                    $"It is first drawn on line {first}",
                    line, width + 1,
                    $"draw '{shown}' again",
                    "Take one of the two lines out.");
            drawn[folder] = line;

            _namespaceOf[folder] = Path.GetFullPath(owner).TrimEnd(Path.DirectorySeparatorChar);
            levels.Add((width, folder, _namespaceOf[folder]));
        }
    }

    /// <summary>Once a project draws its tree, it draws every folder with Cufet source in it.</summary>
    /// <remarks>
    /// ★ A partial drawing would be read as the whole project and be wrong. Folders with no Cufet
    /// source anywhere beneath them — build output, `.git`, assets — are not part of it, and
    /// `books/` belongs to the pins rather than the tree.
    /// </remarks>
    private void RequireEverySourceFolder(string root, int line)
    {
        var books = Path.GetFullPath(Path.Combine(root, BookLoading.SharedFolder));
        foreach (var folder in SourceFolders(root, books))
        {
            if (_namespaceOf.ContainsKey(folder)) continue;
            var shown = Shown(root, folder);
            throw TypeChecker.TypeError(
                $"the folder '{shown}' holds Cufet source, and the tree does not draw it",
                "Once a project draws its tree it draws every folder, so the drawing can be "
              + "trusted to be the whole project",
                line, 1,
                $"leave '{shown}' out of the tree",
                "Add a line for it — at the left edge for a namespace of its own, or indented under "
              + "the folder it belongs to.");
        }
    }

    /// <summary>Every folder below the root with a `.cufe` in it or beneath it, in path order.</summary>
    private static List<string> SourceFolders(string root, string books)
    {
        var found = new List<string>();
        Walk(Path.GetFullPath(root), isRoot: true);
        return found;

        bool Walk(string directory, bool isRoot)
        {
            if (string.Equals(directory, books, StringComparison.OrdinalIgnoreCase)) return false;
            string[] files, below;
            try
            {
                files = Directory.GetFiles(directory, "*.cufe");
                below = Directory.GetDirectories(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }

            Array.Sort(below, StringComparer.Ordinal);
            int at = found.Count;
            bool any = files.Length > 0;
            foreach (var child in below)
                if (Walk(Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar), isRoot: false)) any = true;
            if (any && !isRoot) found.Insert(at, directory);
            return any;
        }
    }

    /// <summary>A folder as the drawing would name it, relative to the project root.</summary>
    private static string Shown(string root, string folder) =>
        Path.GetRelativePath(root, folder).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Top-level statements, and those inside a `Pull` — where a blueprint's functions sit.</summary>
    private static IEnumerable<IStatement> Hoistable(IReadOnlyList<IStatement> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;
            if (statement is PullStatement pull)
                foreach (var inner in Hoistable(pull.Body)) yield return inner;
        }
    }
}
