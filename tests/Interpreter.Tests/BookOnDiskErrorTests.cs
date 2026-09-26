using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// A book of your own, in a file beside the program — the two first slips with one.
// Both found by writing the tutorial's lesson on books.
public class BookOnDiskErrorTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "cufet-book-on-disk-" + Guid.NewGuid().ToString("N"));

    public BookOnDiskErrorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private const string Members =
        "    Bind number to plants-in, given (the number area, the number spacing):\n"
      + "        Return area / spacing.\n"
      + "    Done.\n"
      + "Done.\n";

    private TypeException CheckFails(string source)
    {
        var main = Path.Combine(_folder, "plan.cufe");
        File.WriteAllText(main, source);
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        var checker = new TypeChecker { SourceDirectory = _folder, SourceFile = Path.GetFullPath(main) };
        return Assert.Throws<TypeException>(() => checker.Check(program));
    }

    [Fact]
    public void ABookFileWithoutAndBook_SaysWhatToAdd()
    {
        // ⚠ It said "there is nothing named 'planting' to pull … A book may also be a file named
        // 'planting.cufe', beside the file that pulls it" — describing the very file that was there.
        File.WriteAllText(Path.Combine(_folder, "planting.cufe"),
            "Define object planting with ():\n" + Members);
        var ex = CheckFails("Pull a book on planting.\n    State planting's plants-in of (20, 0.25).\nDone.");
        Assert.Contains("planting.cufe declares 'object planting', but not as a book", ex.Message);
        Assert.Contains("'Define object planting with () and book:'", ex.Message);
    }

    [Fact]
    public void YourOwnBookUsedWithoutAPull_IsCalledABook()
    {
        // ⚠ The bundled books got this in lesson 5; a book in a file beside the program still said
        // "'planting' isn't defined … Define it first".
        File.WriteAllText(Path.Combine(_folder, "planting.cufe"),
            "Define object planting with () and book:\n" + Members);
        var ex = CheckFails("State planting's plants-in of (20, 0.25).");
        Assert.Contains("'planting' is a book, and it is not pulled here", ex.Message);
        Assert.DoesNotContain("Define it first", ex.Message);
    }

    [Fact]
    public void APullThatFindsNoFile_KeepsItsOwnWords()
    {
        var ex = CheckFails("Pull a book on planting.\nDone.");
        Assert.Contains("there is nothing named 'planting' to pull", ex.Message);
    }
}
