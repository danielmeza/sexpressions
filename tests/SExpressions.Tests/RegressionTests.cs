using System.Runtime.CompilerServices;
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

    // ---------------------------------------------------------------- reused parser buffers

    /// <summary>
    /// The parser's item scratch stack is reused across parses on a thread, so it is left holding
    /// <see cref="SItem"/>s that point at the tree it just built. Unless it is wiped when the parse
    /// ends, one parse of a large file keeps that whole tree -- and the source text it came from --
    /// alive for as long as the thread lives.
    /// </summary>
    [Fact]
    public void AParsedTree_IsNotKeptAliveByTheParsersScratchBuffer()
    {
        var text = "(root " + string.Join(' ', Enumerable.Range(0, 400).Select(i => $"(n{i} {i})")) + ")";
        var weak = ParseAndDrop(text);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.IsAlive, "the parser is still holding the document it parsed");
    }

    /// <summary>Kept out of the test body so no local of the caller's frame can root the tree.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ParseAndDrop(string text) => new(SDocument.Parse(text).Root!);

    /// <summary>
    /// Two documents parsed in a row on one thread share the parser's atom cache and scratch stack.
    /// Nothing of the first may reach the second.
    /// </summary>
    [Fact]
    public void ParsingTwoDocumentsInARow_LeaksNothingBetweenThem()
    {
        const string First = "(a (b \"shared\" 1) (c 2))\n";
        const string Second = "(a (b \"shared\" 9) (d 3))\n";

        var one = SDocument.Parse(First);
        var two = SDocument.Parse(Second);

        Assert.Equal(First, one.ToText());
        Assert.Equal(Second, two.ToText());
        Assert.Equal("1", one.Root!.GetChild("b")!.Values[1]);
        Assert.Equal("9", two.Root!.GetChild("b")!.Values[1]);
        Assert.Null(one.Root.GetChild("d"));
        Assert.Null(two.Root.GetChild("c"));
    }

    /// <summary>
    /// The atom cache hands the same <see cref="string"/> instance to every occurrence of a repeated
    /// atom, across documents as well as within one. That is the point of it -- but the values must
    /// still compare equal to freshly built strings, never merely reference-equal.
    /// </summary>
    [Fact]
    public void PooledAtoms_AreValueEqualToUnpooledOnes()
    {
        const string Text = "(root (x \"layer\") (y \"layer\") (z 1.27))\n";

        var pooled = new SExpressionParser().ParseAll(Text);
        var unpooled = new SExpressionParser(new SExpressionParserOptions { PoolStrings = false }).ParseAll(Text);

        Assert.Equal(unpooled.ToText(), pooled.ToText());
        Assert.Equal(unpooled.Root!.GetChild("x")!.Values[0], pooled.Root!.GetChild("x")!.Values[0]);
        Assert.Same(pooled.Root.GetChild("x")!.Values[0], pooled.Root.GetChild("y")!.Values[0]);
    }

    /// <summary>A failed parse must still give the thread its scratch stack back, wiped.</summary>
    [Fact]
    public void AFailedParse_StillLeavesTheNextParseCorrect()
    {
        Assert.Throws<SExpressionFormatException>(() => SDocument.Parse("(a (b 1)"));

        const string Good = "(a (b 1))\n";
        Assert.Equal(Good, SDocument.Parse(Good).ToText());
    }

    /// <summary>
    /// The buffers are per thread and the async entry points resume on a pool thread, so a host
    /// needs a way to give them back. Releasing them must cost correctness nothing.
    /// </summary>
    [Fact]
    public void ClearThreadBuffers_LeavesParsingCorrect()
    {
        const string Text = "(a (b \"x\" 1) (c 2))\n";

        SExpressionParser.ClearThreadBuffers();      // before this thread has parsed anything
        var cold = SDocument.Parse(Text);
        SExpressionParser.ClearThreadBuffers();      // between parses
        var warm = SDocument.Parse(Text);
        SExpressionParser.ClearThreadBuffers();
        SExpressionParser.ClearThreadBuffers();      // twice in a row

        Assert.Equal(Text, cold.ToText());
        Assert.Equal(Text, warm.ToText());
        Assert.Equal(Text, SDocument.Parse(Text).ToText());
    }

    /// <summary>
    /// A document deep enough to grow the scratch stack past the retention cap must still parse, and
    /// must not leave the oversized buffer pinned on the thread. The cap is not observable from here,
    /// so this asserts the part that is: the parse is correct and the next one is unaffected.
    /// </summary>
    [Fact]
    public void ADocumentThatOutgrowsTheScratchCap_ParsesAndDoesNotDisturbTheNextParse()
    {
        // 4096 items in one form: past ScratchSize (512) and past MaxRetainedScratch (2048).
        var wide = "(root " + string.Join(' ', Enumerable.Range(0, 4096).Select(i => $"(n{i} {i})")) + ")";

        var big = SDocument.Parse(wide);
        Assert.Equal(4096, big.Root!.Children.Count);
        Assert.Equal(wide, big.ToText());

        const string Small = "(a (b 1))\n";
        Assert.Equal(Small, SDocument.Parse(Small).ToText());
    }
}
