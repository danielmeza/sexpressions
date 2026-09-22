namespace SExpressions.Tests;

/// <summary>
/// #35: which line ending a node the writer has to format gets, inside a parsed file.
/// </summary>
/// <remarks>
/// <para>
/// MEASURED on a9111fd: a new multi-line node in a CRLF file came out with mixed endings. Its first
/// line break was copied from a sibling's separator, so it was <c>\r\n</c>; every line break
/// inside it came from <see cref="SExpressionWriterOptions.NewLine"/>, which defaults to
/// <c>\n</c>:
/// <c>"(root\r\n\t(a 1)\r\n\t(b\n\t\t(c 2)\n\t)\r\n)\r\n"</c>.
/// </para>
/// <para>
/// Now every line break the writer formats inside a parsed file is the file's own, read off the
/// first line break in the text, exactly as the indentation unit is read off the file (#24).
/// <see cref="SExpressionWriterOptions.NewLine"/> is only the fallback: Canonical, a tree built in
/// memory, and text with no line break to follow. Assertions are on whole texts.
/// </para>
/// </remarks>
public class LineEndingTests
{
    private const string CrlfFile = "(root\r\n\t(a 1)\r\n)\r\n";

    // ------------------------------------------------------------- the issue, exactly as reported

    [Fact]
    public void AddingAMultiLineNode_ToACrlfFile_BreaksEveryLineOfItWithCrlf()
    {
        var document = SDocument.Parse(CrlfFile);
        document.Root!.CreateChild("b").CreateChild("c", "2");

        Assert.Equal("(root\r\n\t(a 1)\r\n\t(b\r\n\t\t(c 2)\r\n\t)\r\n)\r\n", document.ToText());
    }

    [Fact]
    public void AddingAMultiLineNode_ToACrlfFile_ThroughTheRootForm_BreaksEveryLineOfItWithCrlf()
    {
        var root = SDocument.Parse(CrlfFile).Root!;
        root.CreateChild("b").CreateChild("c", "2");

        // The same text as through the document, less the file's trailing line break.
        Assert.Equal("(root\r\n\t(a 1)\r\n\t(b\r\n\t\t(c 2)\r\n\t)\r\n)", root.ToText());
    }

    [Fact]
    public void AddingANodeThreeLevelsDeep_ToACrlfFile_BreaksEveryLineOfItWithCrlf()
    {
        var document = SDocument.Parse(CrlfFile);
        document.Root!.CreateChild("b").CreateChild("c").CreateChild("d", "3");

        Assert.Equal("(root\r\n\t(a 1)\r\n\t(b\r\n\t\t(c\r\n\t\t\t(d 3)\r\n\t\t)\r\n\t)\r\n)\r\n", document.ToText());
    }

    // ------------------------------------------------------- every line break the writer formats

    [Fact]
    public void AComment_ThatForcesALineBreak_InACrlfFile_BreaksTheLineWithCrlf()
    {
        var document = SDocument.Parse(CrlfFile);
        document.Root!.GetChild("a")!.AddComment(" note");

        // The comment runs to the end of its line, so (a's closing paren has to move to the next one.
        Assert.Equal("(root\r\n\t(a 1 # note\r\n\t)\r\n)\r\n", document.ToText());
    }

    [Fact]
    public void AddingAFormToAnEmptyMultiLineForm_InACrlfFile_BreaksTheLineWithCrlf()
    {
        // (empty has no separator to copy, only its own shape: it is broken over lines, so the new
        // child gets a line of its own.
        var document = SDocument.Parse("(root\r\n\t(empty\r\n\t)\r\n)\r\n");
        document.Root!.GetChild("empty")!.CreateChild("a", "1");

        Assert.Equal("(root\r\n\t(empty\r\n\t\t(a 1)\r\n\t)\r\n)\r\n", document.ToText());
    }

    [Fact]
    public void AddingATopLevelForm_ToACrlfDocument_BreaksEveryLineOfItWithCrlf()
    {
        var document = SDocument.Parse("(version 1)\r\n(a\r\n\t(b 1)\r\n)\r\n");
        var c = new SExpression("c");
        c.CreateChild("d", "2");
        document.Add(c);

        Assert.Equal("(version 1)\r\n(a\r\n\t(b 1)\r\n)\r\n(c\r\n\t(d 2)\r\n)\r\n", document.ToText());
    }

    // ---------------------------------------------------------------- what decides the ending

    [Fact]
    public void TheNewLineOption_DoesNotOverrideTheLineEndingTheFileAlreadyHas()
    {
        var document = SDocument.Parse("(root\n\t(a 1)\n)\n");
        document.Root!.CreateChild("b").CreateChild("c", "2");

        var text = document.ToText(new SExpressionWriterOptions { NewLine = "\r\n" });

        Assert.Equal("(root\n\t(a 1)\n\t(b\n\t\t(c 2)\n\t)\n)\n", text);
    }

    [Fact]
    public void TheFirstLineBreakInTheFile_DecidesTheEndingOfEveryFormattedOne()
    {
        // A file with mixed endings has one ending as far as the writer is concerned: the first.
        var document = SDocument.Parse("(root\r\n\t(a 1)\n\t(b 2)\n)\n");
        document.Root!.CreateChild("c").CreateChild("d", "3");

        Assert.Equal("(root\r\n\t(a 1)\n\t(b 2)\n\t(c\r\n\t\t(d 3)\r\n\t)\n)\n", document.ToText());
    }

    [Fact]
    public void AFileWithNoLineBreak_TakesTheNewLineOption()
    {
        var document = SDocument.Parse("(root (a 1))");
        document.Root!.CreateChild("b").CreateChild("c", "2");

        Assert.Equal("(root (a 1) (b\r\n\t\t(c 2)\r\n\t))", document.ToText(new SExpressionWriterOptions { NewLine = "\r\n" }));
    }

    [Fact]
    public void ATreeBuiltInMemory_TakesTheNewLineOption()
    {
        var root = new SExpression("root");
        root.CreateChild("a").CreateChild("b", "1");

        Assert.Equal("(root\r\n\t(a\r\n\t\t(b 1)\r\n\t)\r\n)\r\n", root.ToText(new SExpressionWriterOptions { NewLine = "\r\n" }));
    }

    [Fact]
    public void AFormBuiltInMemory_HoldingAParsedCrlfOne_BreaksItsLinesWithCrlf()
    {
        var root = new SExpression("root");
        root.AddChild(SExpression.Parse("(a\r\n\t(b 1)\r\n)"));

        Assert.Equal("(root\r\n\t(a\r\n\t\t(b 1)\r\n\t)\r\n)\r\n", root.ToText());
    }

    [Fact]
    public void CanonicalFormat_IgnoresTheFilesLineEnding_AndUsesTheOption()
    {
        var text = SDocument.Parse(CrlfFile).ToText(new SExpressionWriterOptions { Format = SExpressionFormat.Canonical });

        Assert.Equal("(root\n\t(a 1)\n)\n", text);
    }

    [Fact]
    public void AnUntouchedCrlfFile_StillComesBackByteForByte()
    {
        Assert.Equal(CrlfFile, SDocument.Parse(CrlfFile).ToText());
    }
}
