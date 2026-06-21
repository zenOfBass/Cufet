namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Books (bundled standard-library modules) ──────────────────────────────

    private static readonly Dictionary<string, BookType> BuiltinBooks = BuildBuiltinBooks();

    private static Dictionary<string, BookType> BuildBuiltinBooks()
    {
        var books = new Dictionary<string, BookType>(StringComparer.OrdinalIgnoreCase);

        // math book — deterministic operations return number;
        // partial operations (undefined for some inputs) return voidable number.
        var mathMembers = new List<(string, CufetType)>
        {
            ("floor",          new FunctionType([CufetType.Number], CufetType.Number)),
            ("ceiling",        new FunctionType([CufetType.Number], CufetType.Number)),
            ("round",          new FunctionType([CufetType.Number], CufetType.Number)),
            ("absolute value", new FunctionType([CufetType.Number], CufetType.Number)),
            ("square root",    new FunctionType([CufetType.Number], new VoidableType(CufetType.Number))),
            ("log",            new FunctionType([CufetType.Number], new VoidableType(CufetType.Number))),
            ("power",          new FunctionType([CufetType.Number, CufetType.Number], new VoidableType(CufetType.Number))),
            ("pi",             CufetType.Number),
            ("e",              CufetType.Number),
        };
        books["math"] = new BookType("math", mathMembers);

        return books;
    }

    private void CheckPullStatement(PullStatement ps)
    {
        if (!BuiltinBooks.TryGetValue(ps.BookName, out var bookType))
        {
            var available = string.Join(", ", BuiltinBooks.Keys.OrderBy(k => k).Select(k => $"'{k}'"));
            throw new TypeException(FormatTypeError(
                $"there is no book named '{ps.BookName}'",
                null, ps.Line,
                $"pull a book named '{ps.BookName}'",
                $"Available books: {available}."));
        }

        if (Scope.ContainsKey(ps.LocalName))
            throw new TypeException(FormatTypeError(
                $"'{ps.LocalName}' is already defined in this scope",
                null, ps.Line,
                $"bind book '{ps.BookName}' to '{ps.LocalName}'",
                "Choose a different local name."));

        Scope[ps.LocalName] = new TypeInfo(bookType, new VariableReference(ps.LocalName, ps.Line), ps.Line);
    }

    private CufetType InferBookPossessiveAccess(PossessiveAccess poss, BookType bt)
    {
        var memberType = bt.FindMember(poss.Member);
        if (memberType != null) return memberType;

        var available = string.Join(", ", bt.Members.Select(m => $"'{m.MemberName}'"));
        throw new TypeException(FormatTypeError(
            $"book '{bt.Name}' has no member '{poss.Member}'",
            null, poss.Line,
            $"access '{poss.Member}' from book '{bt.Name}'",
            available.Length > 0 ? $"Available: {available}." : $"Book '{bt.Name}' has no members."));
    }
}
