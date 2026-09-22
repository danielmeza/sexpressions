namespace SExpressions.Tests;

/// <summary>
/// #31: the start of a document when its first top-level item changes.
/// </summary>
/// <remarks>
/// Inside a form, the header comes before the first child, so the separator in front of every
/// child is a real one. At the top level nothing comes before the first item but whatever
/// whitespace opens the file. A removed item takes the separator in front of it along, and the
/// first item has none, so removing it left the next item's line break at the top of the file;
/// putting something in front of it fused the two, because the item that used to open the file has
/// no separator of its own.
/// </remarks>
public class DocumentStartTests
{
    private const string Two = "(a)\n(b)\n";

    [Fact]
    public void RemovingTheFirstForm_LeavesNoBlankLineAtTheTop()
    {
        var document = SDocument.Parse(Two);
        document.Forms.RemoveAt(0);

        Assert.Equal("(b)\n", document.ToText());
    }

    [Fact]
    public void RemovingTheFirstFormOfADesignRuleFile_LeavesTheNextItemAtTheTop()
    {
        const string Rules =
            "(version 1)\n"
            + "\n"
            + "# Keep this clearance.\n"
            + "(rule \"Clearance\"\n"
            + "\t(constraint clearance (min 0.2mm))\n"
            + ")\n";
        var document = SDocument.Parse(Rules);
        document.Forms.RemoveAt(0);

        Assert.Equal(Rules["(version 1)\n\n".Length..], document.ToText());
    }

    [Fact]
    public void MovingTheFirstForm_IntoAnotherForm_LeavesNoBlankLineAtTheTop()
    {
        var document = SDocument.Parse(Two);
        new SExpression("x").AddChild(document.Root!);

        Assert.Equal("(b)\n", document.ToText());
    }

    [Fact]
    public void MovingTheFirstForm_ToTheEnd_PutsItOnALineOfItsOwn()
    {
        var document = SDocument.Parse(Two);
        document.Add(document.Forms[0]);

        Assert.Equal("(b)\n(a)\n", document.ToText());
    }

    [Fact]
    public void MovingTheLastForm_ToTheFront_KeepsTheTwoOnSeparateLines()
    {
        var document = SDocument.Parse(Two);
        document.Forms.Insert(0, document.Forms[1]);

        Assert.Equal("(b)\n(a)\n", document.ToText());
    }

    [Fact]
    public void InsertingAFormInFrontOfTheFirst_GivesTheFirstALineBreak()
    {
        var document = SDocument.Parse(Two);
        document.Forms.Insert(0, new SExpression("n"));

        Assert.Equal("(n)\n(a)\n(b)\n", document.ToText());
    }

    [Fact]
    public void ACommentPutInFrontOfTheFirstForm_OpensTheFile()
    {
        var document = SDocument.Parse(Two);
        document.Items.Insert(0, SItem.CreateComment(" header"));

        Assert.Equal("# header\n(a)\n(b)\n", document.ToText());
    }

    [Fact]
    public void RemovingALeadingComment_LeavesTheFirstFormAtTheTop()
    {
        var document = SDocument.Parse("# header\n(a)\n(b)\n");
        document.Items.RemoveAt(0);

        Assert.Equal(Two, document.ToText());
    }

    [Fact]
    public void WhitespaceThatOpensTheFile_StaysWhenTheFirstFormGoes()
    {
        // It belongs to the file, not to the form that followed it.
        var document = SDocument.Parse("\n\n(a)\n(b)\n");
        document.Forms.RemoveAt(0);

        Assert.Equal("\n\n(b)\n", document.ToText());
    }

    [Theory]
    [InlineData("", "(n)")]
    [InlineData("\n", "(n)\n")]
    public void AddingAFormToTextWithNoForms_StartsTheFileWithIt(string text, string expected)
    {
        var document = SDocument.Parse(text);
        document.Add(new SExpression("n"));

        Assert.Equal(expected, document.ToText());
    }
}
