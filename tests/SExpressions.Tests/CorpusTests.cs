namespace SExpressions.Tests;

/// <summary>
/// The corpus harness itself: which files it hands to the round-trip tests (#32).
/// </summary>
public class CorpusTests
{
    public static TheoryData<string> BrokenOnPurpose()
    {
        var d = new TheoryData<string>();
        if (!Corpus.Missing)
        {
            foreach (var f in Corpus.BrokenOnPurpose)
            {
                d.Add(f);
            }
        }

        if (d.Count == 0)
        {
            d.Add("<no corpus>");
        }

        return d;
    }

    /// <summary>
    /// A file the corpus cut short on purpose is not skipped silently: it is taken out of the
    /// round-trip tests by name, and here both parsers must refuse it, having run out of input
    /// rather than tripped over anything in it. When the fixture changes -- fixed, moved, or cut
    /// somewhere else -- this fails and the list has to be looked at again.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrokenOnPurpose))]
    public void AFileCutShortOnPurpose_IsRefusedByTheParserAndTheReader_AndRoundTrippedByNothing(string relative)
    {
        if (relative == "<no corpus>")
        {
            return;
        }

        var path = Path.Combine(Corpus.RequireRoot(), relative);
        Assert.True(File.Exists(path), $"{relative} is in Corpus.BrokenOnPurpose but not in the corpus; take it off the list.");
        var text = File.ReadAllText(path);

        var tree = Assert.Throws<SExpressionFormatException>(() => SDocument.Parse(text));
        Assert.StartsWith("Unexpected end of input", tree.Message, StringComparison.Ordinal);
        Assert.Equal(text.Length, tree.Position);

        var reader = Assert.Throws<SExpressionFormatException>(() =>
        {
            var r = new SExpressionReader(text);
            while (r.Read())
            {
            }
        });
        Assert.StartsWith("Unexpected end of input", reader.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(relative, Corpus.All());
    }
}
