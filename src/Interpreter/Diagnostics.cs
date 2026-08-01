namespace Cufet.Interpreter;

// Everything Cufet reports has been an exception until now, which makes every problem fatal and
// stops at the first one. That is right for an error — a program that does not type-check cannot
// be run, so there is nothing to gain by continuing — but it leaves no way to say something worth
// hearing about a program that is still perfectly runnable.
//
// A warning is exactly that: true, worth saying, and not a reason to stop. So it cannot be thrown.
// It is collected here instead, and the front end goes on working.

public enum DiagnosticSeverity
{
    Error,    // the program cannot run; still raised as an exception, never collected
    Warning,  // the program runs, and something about it is worth knowing
}

// Line and Column are 1-based, matching what the lexer and the AST carry. A diagnostic with no
// position of its own uses line 1, column 1 — never zero, so a reader can always point somewhere.
public sealed record Diagnostic(DiagnosticSeverity Severity, string Message, int Line, int Column)
{
    public string SeverityName => Severity == DiagnosticSeverity.Warning ? "warning" : "error";
}

// The collection a pass appends to while it works. Deliberately not thread-shared: a checker or a
// generator owns exactly one, and the caller reads it once the pass has returned.
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    public IReadOnlyList<Diagnostic> Items => _items;
    public bool Any => _items.Count > 0;

    public void Warn(string message, int line = 1, int column = 1) =>
        _items.Add(new Diagnostic(DiagnosticSeverity.Warning, message, Math.Max(line, 1), Math.Max(column, 1)));

    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);
}
