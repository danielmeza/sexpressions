using System.Text;

namespace SExpressions.Tests;

/// <summary>
/// Regression coverage for three measured bugs: the canonical writer swallowing an atom that
/// follows a comment inside a form, a UTF-8 BOM being dropped through <see cref="SDocument"/>
/// Load/Save, and <see cref="SExpression.TryGetValue{T}"/> rejecting KiCad's <c>yes</c>/<c>no</c>
/// booleans.
/// </summary>
public class RegressionTests
{
    private static readonly SExpressionWriterOptions Canonical = new() { Format = SExpressionFormat.Canonical };

    // ---------------------------------------------------------- bug 1: comment-then-atom in canonical mode

    [Fact]
    public void CanonicalWriter_AtomAfterAComment_StartsOnItsOwnLine_AndSurvivesReparse()
    {
        var doc = SDocument.Parse("(a # note\n 1)");
        var root = doc.Root!;

        var written = root.ToText(Canonical);

        // The bug: "1" used to be appended with a plain space right after "# note" on the SAME
        // line, so it became part of the comment text and vanished on re-parse.
        var reparsed = SExpression.Parse(written);
        Assert.Equal(" note", Assert.Single(reparsed.Comments));
        Assert.Equal("1", reparsed.GetValue(0));
        Assert.Equal("1", Assert.Single(reparsed.Values));
    }

    [Fact]
    public void CanonicalWriter_MultipleValuesAfterAComment_EachSurviveReparse()
    {
        var doc = SDocument.Parse("(rule # why\n 1 2 3)");
        var written = doc.Root!.ToText(Canonical);

        var reparsed = SExpression.Parse(written);
        Assert.Equal([" why"], reparsed.Comments);
        Assert.Equal(["1", "2", "3"], reparsed.Values.ToArray());
    }

    // ---------------------------------------------------------------- bug 2: UTF-8 BOM round-trip

    [Fact]
    public void Load_ThenSave_PreservesALeadingUtf8Bom()
    {
        var srcPath = Path.Combine(Path.GetTempPath(), $"sexpr-bom-{Guid.NewGuid():N}.kicad_sch");
        var dstPath = Path.Combine(Path.GetTempPath(), $"sexpr-bom-out-{Guid.NewGuid():N}.kicad_sch");
        try
        {
            var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF };
            var textBytes = Encoding.ASCII.GetBytes("(a 1)\n");
            File.WriteAllBytes(srcPath, [.. bomBytes, .. textBytes]);

            var inputBytes = File.ReadAllBytes(srcPath);
            Assert.Equal(9, inputBytes.Length);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, inputBytes[..3]);

            var doc = SDocument.Load(srcPath);
            Assert.True(doc.HasByteOrderMark);

            // ToText() is a text-only concern: no BOM leaks into it, and it stays the exact 6-byte body.
            Assert.Equal("(a 1)\n", doc.ToText());

            doc.Save(dstPath);

            var outputBytes = File.ReadAllBytes(dstPath);
            Assert.Equal(9, outputBytes.Length);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, outputBytes[..3]);
            Assert.Equal(inputBytes, outputBytes);
        }
        finally
        {
            File.Delete(srcPath);
            File.Delete(dstPath);
        }
    }

    [Fact]
    public void Load_ThenSave_ANonBomFile_StaysWithoutABom()
    {
        var srcPath = Path.Combine(Path.GetTempPath(), $"sexpr-nobom-{Guid.NewGuid():N}.kicad_sch");
        var dstPath = Path.Combine(Path.GetTempPath(), $"sexpr-nobom-out-{Guid.NewGuid():N}.kicad_sch");
        try
        {
            File.WriteAllBytes(srcPath, Encoding.ASCII.GetBytes("(a 1)\n"));

            var inputBytes = File.ReadAllBytes(srcPath);
            Assert.Equal(6, inputBytes.Length);

            var doc = SDocument.Load(srcPath);
            Assert.False(doc.HasByteOrderMark);

            doc.Save(dstPath);

            var outputBytes = File.ReadAllBytes(dstPath);
            Assert.Equal(6, outputBytes.Length);
            Assert.Equal(inputBytes, outputBytes);
        }
        finally
        {
            File.Delete(srcPath);
            File.Delete(dstPath);
        }
    }

    [Fact]
    public void HasByteOrderMark_DefaultsToFalse_ForAnInMemoryOrParsedDocument()
    {
        Assert.False(new SDocument().HasByteOrderMark);
        Assert.False(SDocument.Parse("(a 1)").HasByteOrderMark);
    }

    [Fact]
    public async Task LoadAsync_ThenSaveAsync_PreservesALeadingUtf8Bom()
    {
        var srcPath = Path.Combine(Path.GetTempPath(), $"sexpr-bom-async-{Guid.NewGuid():N}.kicad_sch");
        var dstPath = Path.Combine(Path.GetTempPath(), $"sexpr-bom-async-out-{Guid.NewGuid():N}.kicad_sch");
        try
        {
            var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF };
            var textBytes = Encoding.ASCII.GetBytes("(a 1)\n");
            await File.WriteAllBytesAsync(srcPath, [.. bomBytes, .. textBytes]);

            var doc = await SDocument.LoadAsync(srcPath);
            Assert.True(doc.HasByteOrderMark);

            await doc.SaveAsync(dstPath);

            var outputBytes = await File.ReadAllBytesAsync(dstPath);
            Assert.Equal(9, outputBytes.Length);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, outputBytes[..3]);
        }
        finally
        {
            File.Delete(srcPath);
            File.Delete(dstPath);
        }
    }

    [Fact]
    public void SExpressionWriter_WriteToFile_HonoursHasByteOrderMark()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sexpr-writer-bom-{Guid.NewGuid():N}.kicad_sch");
        try
        {
            var doc = SDocument.Parse("(a 1)");
            doc.HasByteOrderMark = true;

            new SExpressionWriter().WriteToFile(doc, path);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // -------------------------------------------------------- bug 3: TryGetValue<bool> vs yes/no

    [Fact]
    public void TryGetValueOfBool_AcceptsYesAndNo_LikeGetValueAsBoolDoes()
    {
        var root = SDocument.Parse("(a yes no)").Root!;

        Assert.True(root.TryGetValue<bool>(0, out var yes));
        Assert.True(yes);

        // The naive fix gets this one wrong: "no" must be PARSED, with a FALSE value -- not
        // reported as "not parsed".
        Assert.True(root.TryGetValue<bool>(1, out var no));
        Assert.False(no);

        Assert.Equal(root.GetValueAsBool(0), yes);
        Assert.Equal(root.GetValueAsBool(1), no);
    }

    [Fact]
    public void GetValueOfBool_AgreesWithGetValueAsBool_OnYesAndNo()
    {
        var root = SDocument.Parse("(a yes no)").Root!;

        Assert.True(root.GetValue<bool>(0, fallback: false));
        Assert.False(root.GetValue<bool>(1, fallback: true));
    }

    [Fact]
    public void GetRequiredValueOfBool_AcceptsYesAndNo()
    {
        var root = SDocument.Parse("(a yes no)").Root!;

        Assert.True(root.GetRequiredValue<bool>(0));
        Assert.False(root.GetRequiredValue<bool>(1));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("YES", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("NO", false)]
    [InlineData("0", false)]
    public void TryGetValueOfBool_AcceptsEveryFormGetValueAsBoolAccepts(string raw, bool expected)
    {
        var root = SDocument.Parse($"(a {raw})").Root!;
        Assert.True(root.TryGetValue<bool>(0, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryGetValueOfBool_RejectsGarbage_ReturningFalseNotParsed()
    {
        var root = SDocument.Parse("(a maybe)").Root!;
        Assert.False(root.TryGetValue<bool>(0, out var value));
        Assert.False(value);
    }

    // ---------------------------------------------------- a document is not an extra indent level

    [Fact]
    public void AddingANode_IndentsItTheSameThroughADocumentAsThroughAnExpression()
    {
        const string Text = "(root\n\t(a\n\t\t(b 1)\n\t)\n)\n";

        var document = SDocument.Parse(Text);
        document.Root!.GetChild("a")!.CreateChild("c").AddValue("2");

        var expression = SExpression.Parse(Text);
        expression.GetChild("a")!.CreateChild("c").AddValue("2");

        // The container holding the top-level forms is not itself a form, so it must not push its
        // children in by one. It used to, which put every re-formatted line one tab too deep.
        Assert.Equal("(root\n\t(a\n\t\t(b 1)\n\t\t(c 2)\n\t)\n)\n", document.ToText());
        // The document keeps the file's trailing newline; a bare expression has none to keep.
        Assert.Equal(expression.ToText(), document.ToText().TrimEnd('\n'));
    }

    [Fact]
    public void AddingANode_LeavesEveryOtherLineExactlyAsItWas()
    {
        const string Text = "(root\n\t(a\n\t\t(b 1)\n\t)\n\t(keep \"me\")\n)\n";

        var document = SDocument.Parse(Text);
        document.Root!.GetChild("a")!.CreateChild("c").AddValue("2");

        Assert.Equal("(root\n\t(a\n\t\t(b 1)\n\t\t(c 2)\n\t)\n\t(keep \"me\")\n)\n", document.ToText());
    }

    [Fact]
    public void ADocumentWithSeveralTopLevelForms_KeepsThemAtColumnZero()
    {
        const string Text = "(one 1)\n\n(two\n\t(x 1)\n)\n";

        var document = SDocument.Parse(Text);
        document[1].CreateChild("y").AddValue("2");

        Assert.Equal("(one 1)\n\n(two\n\t(x 1)\n\t(y 2)\n)\n", document.ToText());
    }
}
