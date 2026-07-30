using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// `the current directory` and `The current directory becomes <path>.`
//
// Every test that CHANGES the directory restores it, because the working directory is process-
// global and xunit runs a class's tests in one process — a leaked change would break whatever
// test happened to run next, in a way that looks like an unrelated failure.
public class CurrentDirectoryTests : IDisposable
{
    private readonly string _original = Directory.GetCurrentDirectory();

    public void Dispose() => Directory.SetCurrentDirectory(_original);

    private static string Run(string source)
    {
        var tokens = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output = new StringWriter();
        RunOnLargeStack(() => new Interpreter(output).Execute(program));
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static void RunOnLargeStack(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { caught = e; } },
                                16 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (caught is not null) ExceptionDispatchInfo.Capture(caught).Throw();
    }

    // A directory that exists on every platform this runs on, spelled with forward slashes so the
    // source needs no escaping (a literal "C:\..." is a lexer error — \W is not an escape).
    private static string SomeDirectory =>
        Path.GetTempPath().Replace('\\', '/').TrimEnd('/');

    // ── Reading ──────────────────────────────────────────────────────────

    [Fact]
    public void CurrentDirectory_IsPresent()
    {
        // Voidable, but a running process essentially always has one.
        Assert.Equal("yes", Run(
            "If the current directory is not void:\n" +
            "    State \"yes\".\n" +
            "Done."));
    }

    [Fact]
    public void CurrentDirectory_ReadsAsText()
    {
        Assert.Equal("true", Run(
            "Define here as the current directory but void is \"\".\n" +
            "State the length of here is greater than 0."));
    }

    // ── 'current' is still an ordinary name ──────────────────────────────
    // The whole reason it is recognised contextually rather than reserved. `current` is a far
    // more tempting variable name than `working` would have been.

    [Fact]
    public void Current_IsStillUsableAsAVariableName()
    {
        Assert.Equal("7", Run("Define current as 7. State current."));
    }

    [Fact]
    public void Current_IsStillUsableAsAParameterName()
    {
        Assert.Equal("9", Run(
            "Bind number to bump, given (the number current):\n" +
            "    Return current + 1.\n" +
            "Done.\n" +
            "State cast bump on (8)."));
    }

    [Fact]
    public void Current_IsStillUsableAsARecordField()
    {
        Assert.Equal("3", Run(
            "Define r as a record with (the current 3).\n" +
            "State the current of r."));
    }

    // ── Changing ─────────────────────────────────────────────────────────

    [Fact]
    public void CurrentDirectory_ChangeThenReadReflectsIt()
    {
        // The OS canonicalises the path it hands back, so compare against what .NET reports for
        // the same target rather than against the string that was written.
        var target = SomeDirectory;
        var expected = new DirectoryInfo(target).FullName.TrimEnd(Path.DirectorySeparatorChar);
        var actual = Run(
            $"The current directory becomes \"{target}\".\n" +
            "State the current directory but void is \"??\".");
        Assert.Equal(expected, actual.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void CurrentDirectory_MissingPath_FailsNotFound()
    {
        Assert.Equal("not-found", Run(
            "Try to:\n" +
            "    The current directory becomes \"/cufet-no-such-directory-xyz-9876\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the category of the failure but void is \"(none)\".\n" +
            "Done."));
    }

    [Fact]
    public void CurrentDirectory_MissingPath_MessageNamesThePath()
    {
        Assert.Equal("the directory '/cufet-no-such-directory-xyz-9876' was not found", Run(
            "Try to:\n" +
            "    The current directory becomes \"/cufet-no-such-directory-xyz-9876\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the message of the failure.\n" +
            "Done."));
    }

    [Fact]
    public void CurrentDirectory_PathIsAFile_FailsNotADirectory()
    {
        // The category that only exists because both backends check existence before changing:
        // .NET cannot tell this from "not found" by exception type alone.
        var file = Path.GetTempFileName().Replace('\\', '/');
        try
        {
            Assert.Equal("not-a-directory", Run(
                "Try to:\n" +
                $"    The current directory becomes \"{file}\".\n" +
                "Done.\n" +
                "In case of failure:\n" +
                "    State the category of the failure but void is \"(none)\".\n" +
                "Done."));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void CurrentDirectory_FailureIsRecoverable_LoopCarriesOn()
    {
        // The shape shell.cufe relies on: a bad `cd` must not end the program.
        Assert.Equal("caught\nstill here", Run(
            "Try to:\n" +
            "    The current directory becomes \"/cufet-no-such-directory-xyz-9876\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"caught\".\n" +
            "Done.\n" +
            "State \"still here\"."));
    }

    // ── Type errors ──────────────────────────────────────────────────────

    [Fact]
    public void CurrentDirectory_NonTextPath_IsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("The current directory becomes 42."));
    }

    [Fact]
    public void CurrentDirectory_ReadIsVoidableNotText()
    {
        // Using it as text without handling the void is a static error — the point of voidable.
        Assert.Throws<TypeException>(() =>
            Run("State the length of the current directory."));
    }
}
