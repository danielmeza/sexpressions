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

    // ------------------------------------------------------- one item block per document, sliced

    /// <summary>
    /// Sibling forms take slices of ONE block, so their items sit in the same array with nothing
    /// between them. Each form must still see exactly its own, including the empty one -- an
    /// off-by-one in either direction reads a neighbour's item rather than running off the end,
    /// which is the failure this shape exists to catch.
    /// </summary>
    [Fact]
    public void FormsSharingOneItemBlock_EachSeeOnlyTheirOwnItems()
    {
        var doc = SDocument.Parse("(root (a 1) (b 2 3) (c) (d 4 5 6))\n");
        var root = doc.Root!;

        Assert.Equal(4, root.Items.Count);
        Assert.Equal(new[] { "1" }, root.GetChild("a")!.Values.ToArray());
        Assert.Equal(new[] { "2", "3" }, root.GetChild("b")!.Values.ToArray());
        Assert.Empty(root.GetChild("c")!.Items);
        Assert.Equal(new[] { "4", "5", "6" }, root.GetChild("d")!.Values.ToArray());
    }

    /// <summary>
    /// Replacing an atom writes straight into the shared block, because the slot belongs to that one
    /// form. The bytes around it -- its siblings' slices in the same array -- must not move.
    /// </summary>
    [Fact]
    public void ReplacingAValue_WritesIntoTheSharedBlockWithoutDisturbingNeighbours()
    {
        var doc = SDocument.Parse("(root (a 1) (b 2) (c 3))\n");

        doc.Root!.GetChild("b")!.SetValue(0, "9");

        Assert.Equal("(root (a 1) (b 9) (c 3))\n", doc.ToText());
    }

    /// <summary>
    /// Inserting copies the form out of the block it was sharing. Its siblings keep reading their own
    /// slices out of that block, and everything but the edit still comes back byte for byte.
    /// </summary>
    [Fact]
    public void EditingOneFormInASharedBlock_LeavesItsSiblingsAlone()
    {
        var doc = SDocument.Parse("(root\n\t(a 1)\n\t(b 2)\n\t(c 3)\n)\n");

        doc.Root!.GetChild("b")!.AddValue("9");
        var written = doc.ToText();

        Assert.Contains("(a 1)", written);
        Assert.Contains("(c 3)", written);

        var reparsed = SDocument.Parse(written);
        Assert.Equal("1", reparsed.Root!.GetChild("a")!.GetValue(0));
        Assert.Equal(new[] { "2", "9" }, reparsed.Root.GetChild("b")!.Values.ToArray());
        Assert.Equal("3", reparsed.Root.GetChild("c")!.GetValue(0));
    }

    /// <summary>
    /// Removing an item detaches the form from the block as well, and the forms that stay behind in
    /// that block must be unaffected.
    /// </summary>
    [Fact]
    public void RemovingAnItemFromASharedBlock_LeavesItsSiblingsAlone()
    {
        var doc = SDocument.Parse("(root (a 1) (b 2 3) (c 4))\n");

        Assert.True(doc.Root!.GetChild("b")!.Values.Remove("2"));

        var reparsed = SDocument.Parse(doc.ToText());
        Assert.Equal("1", reparsed.Root!.GetChild("a")!.GetValue(0));
        Assert.Equal(new[] { "3" }, reparsed.Root.GetChild("b")!.Values.ToArray());
        Assert.Equal("4", reparsed.Root.GetChild("c")!.GetValue(0));
    }

    /// <summary>
    /// One form holding more items than the whole first block was sized for. A slice has to be
    /// contiguous, so the block that takes it must be at least as large as the form itself -- the
    /// ceiling on block size is not allowed to win that argument. Without that the harvest writes
    /// past the end of the block.
    /// </summary>
    [Fact]
    public void AFormWithMoreItemsThanTheBlockEstimate_GetsOneContiguousSlice()
    {
        // 20 000 items out of 40 007 characters: two characters per item, against the twelve the
        // first block is sized for.
        var text = "(root" + string.Concat(Enumerable.Range(0, 20_000).Select(_ => " x")) + ")\n";

        var doc = SDocument.Parse(text);

        Assert.Equal(20_000, doc.Root!.Items.Count);
        Assert.All(doc.Root.Values, v => Assert.Equal("x", v));
        Assert.Equal(text, doc.ToText());
    }

    /// <summary>
    /// A document dense enough to outgrow its first item block. The block is never resized or
    /// copied: the forms already harvested keep reading the block that holds them while later ones
    /// are written into a fresh one, so a growth is exactly where a truncated or relocated slice
    /// would show up -- and it shows up as a wrong VALUE, not as an exception.
    /// </summary>
    [Fact]
    public void ADocumentThatOutgrowsItsFirstItemBlock_StillReadsAndWritesEveryForm()
    {
        var text = "(root" + string.Concat(Enumerable.Range(0, 5_000).Select(i => $"(n{i} {i})")) + ")\n";

        var doc = SDocument.Parse(text);

        Assert.Equal(5_000, doc.Root!.Children.Count);
        var index = 0;
        foreach (var child in doc.Root.Children)
        {
            Assert.Equal($"n{index}", child.Token);
            Assert.Equal(index.ToString(), child.GetValue(0));
            index++;
        }

        Assert.Equal(text, doc.ToText());
    }

    /// <summary>
    /// Density that changes partway through the file, which is the one thing the block estimate
    /// cannot extrapolate: a long comment header carries very few items per character and the dense
    /// body that follows carries many, so this parse spans three blocks rather than one.
    /// </summary>
    [Fact]
    public void ADocumentWhoseDensityChangesPartway_SpansSeveralBlocksAndStillRoundTrips()
    {
        var header = string.Concat(Enumerable.Repeat("# a comment line, forty-six characters, no forms\n", 200));
        var text = header + string.Concat(Enumerable.Range(0, 4_000).Select(i => $"(n{i} {i})")) + "\n";

        var doc = SDocument.Parse(text);

        Assert.Equal(4_000, doc.Forms.Count);
        Assert.Equal(200, doc.Comments.Count());
        Assert.Equal("3999", doc[3_999].GetValue(0));
        Assert.Equal(text, doc.ToText());
    }

    /// <summary>
    /// One parser instance, two documents. The item block is handed to the tree, so unlike the
    /// scratch stack it can never be reused -- a reused one would have the second parse writing into
    /// an array the first document is still reading.
    /// </summary>
    [Fact]
    public void OneParserInstanceParsingTwoDocuments_GivesEachItsOwnItemBlock()
    {
        var parser = new SExpressionParser();
        const string First = "(a (b 1) (c 2))\n";
        const string Second = "(d (e 3))\n";

        var one = parser.ParseAll(First);
        var two = parser.ParseAll(Second);

        Assert.Equal(First, one.ToText());
        Assert.Equal(Second, two.ToText());
        Assert.Equal("1", one.Root!.GetChild("b")!.GetValue(0));
        Assert.Equal("3", two.Root!.GetChild("e")!.GetValue(0));
    }

    /// <summary>
    /// The same retention trap as the scratch stack, one level up. The item block holds every item in
    /// the document just built, and through them the whole tree and the source text it was parsed
    /// from. A parser instance that is KEPT -- which is the only reason to construct one rather than
    /// call <see cref="SDocument.Parse"/> -- must not keep the last document it parsed alive with it.
    /// </summary>
    [Fact]
    public void AParsedTree_IsNotKeptAliveByTheParsersItemBlock()
    {
        var parser = new SExpressionParser();
        var text = "(root " + string.Join(' ', Enumerable.Range(0, 400).Select(i => $"(n{i} {i})")) + ")";
        var weak = ParseAndDropWith(parser, text);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.IsAlive, "the parser instance is still holding the document it parsed");
        GC.KeepAlive(parser);
    }

    /// <summary>Kept out of the test body so no local of the caller's frame can root the tree.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ParseAndDropWith(SExpressionParser parser, string text) =>
        new(parser.ParseAll(text).Root!);
}
