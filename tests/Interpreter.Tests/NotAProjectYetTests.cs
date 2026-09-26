using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// A name declared in a file beside this one, in a folder that is not a project.
//
// ⚠ It said "'bed' is not a defined object type … Define the object type first" to someone who had,
// in `beds.cufe` next door. The fact they were missing is that a folder's files see each other only
// inside a project. Found by writing the tutorial's lesson on projects.
public class NotAProjectYetTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "cufet-not-a-project-" + Guid.NewGuid().ToString("N"));

    public NotAProjectYetTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "beds.cufe"),
            "Define object bed with (the number width, the number depth).\n");
        File.WriteAllText(Path.Combine(_folder, "counts.cufe"),
            "Bind number to bed-count, given (the number rows): Return rows * 2. Done.\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private TypeException CheckFails(string source)
    {
        var main = Path.Combine(_folder, "garden.cufe");
        File.WriteAllText(main, source);
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        var checker = new TypeChecker { SourceDirectory = _folder, SourceFile = Path.GetFullPath(main) };
        return Assert.Throws<TypeException>(() => checker.Check(program));
    }

    [Theory]
    [InlineData("Define front as a new bed { the width 4, the depth 5 }.", "'bed' is declared in beds.cufe")]
    [InlineData("State cast bed-count on (3).",                             "'bed-count' is declared in counts.cufe")]
    public void ANameANeighbourDeclares_SaysTheFolderIsNotAProject(string source, string names)
    {
        var ex = CheckFails(source);
        Assert.Contains(names, ex.Message);
        Assert.Contains("this folder is not a project", ex.Message);
        Assert.Contains("'blueprint.cufe'", ex.Message);
    }

    [Fact]
    public void InsideAProject_ItIsNotTheReason()
    {
        // With a blueprint the folder IS a project, so whatever else is wrong, this is not it.
        File.WriteAllText(Path.Combine(_folder, "blueprint.cufe"), "");
        var ex = CheckFails("State cast nowhere-at-all on (3).");
        Assert.DoesNotContain("not a project", ex.Message);
    }

    [Fact]
    public void ANameNoNeighbourDeclares_KeepsItsOwnWords()
    {
        var ex = CheckFails("State cast nowhere-at-all on (3).");
        Assert.Contains("'nowhere-at-all' isn't defined", ex.Message);
        Assert.DoesNotContain("not a project", ex.Message);
    }
}
