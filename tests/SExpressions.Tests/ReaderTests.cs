using System.Text;

namespace SExpressions.Tests;

/// <summary>
/// The streaming reader is an ADDITIONAL API over the same text the tree parser reads, so the thing
/// worth testing is not that it produces plausible tokens but that it produces the SAME document:
/// every test here that can be is written as an equivalence against <see cref="SDocument"/>.
/// </summary>
public class ReaderTests
{
    [Fact]
    public void Read_ReportsTokensInSourceOrderWithDepth()
    {
        var reader = new SExpressionReader("(a 1 (b \"x\") # note\n)");

        var seen = new List<string>();
        while (reader.Read())
        {
            seen.Add($"{reader.TokenType}:{reader.Depth}:{reader.GetString()}");
        }

        Assert.Equal(
            new[]
            {
                "StartForm:1:a",
                "Atom:1:1",
                "StartForm:2:b",
                "Atom:2:x",
                "EndForm:1:",
                "Comment:1: note",
                "EndForm:0:",
            },
            seen);
    }

    /// <summary>
    /// The equivalence that matters: for every file in the corpus the reader must yield exactly the
    /// atoms, tokens and comments the tree holds, in the same order. A reader that split the text
    /// even slightly differently would let a consumer inspect a file with one API and edit it with
    /// the other while seeing two different documents.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Reader_AndTree_AgreeOnEveryTokenOfEveryCorpusFile(string relative)
    {
        if (Corpus.Missing)
        {
            return;
        }

        var text = Corpus.Read(relative);
        SDocument document;
        try
        {
            document = SDocument.Parse(text);
        }
        catch (SExpressionFormatException)
        {
            return; // a deliberately unparsable fixture; the reader is not being asked to differ
        }

        var fromTree = new List<string>();
        Flatten(document.Items, fromTree);

        var fromReader = new List<string>();
        var reader = new SExpressionReader(text);
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case SExpressionTokenType.StartForm:
                    fromReader.Add("(" + reader.GetString());
                    break;
                case SExpressionTokenType.EndForm:
                    fromReader.Add(")");
                    break;
                case SExpressionTokenType.Atom:
                    fromReader.Add("a" + reader.GetString());
                    break;
                case SExpressionTokenType.Comment:
                    fromReader.Add("#" + reader.GetString());
                    break;
            }
        }

        Assert.Equal(fromTree, fromReader);
    }

    /// <summary>
    /// The two sides of the reader-versus-tree benchmark must extract the SAME records, or the ratio
    /// it reports is a comparison of two different workloads. This runs both halves of it over the
    /// corpus and asserts they agree.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Reader_AndTree_ExtractTheSameRecords(string relative)
    {
        if (Corpus.Missing)
        {
            return;
        }

        var text = Corpus.Read(relative);
        SDocument document;
        try
        {
            document = SDocument.Parse(text);
        }
        catch (SExpressionFormatException)
        {
            return;
        }

        var fromTree = document
            .SelectMany(form => form.Descendants("property"))
            .Select(p => (Name: p.GetValue(0) ?? string.Empty, Value: p.GetValue(1) ?? string.Empty))
            .ToList();

        var fromReader = new List<(string Name, string Value)>();
        var reader = new SExpressionReader(text);
        while (reader.Read())
        {
            if (reader.TokenType != SExpressionTokenType.StartForm || !reader.ValueEquals("property"))
            {
                continue;
            }

            var name = reader.Read() && reader.TokenType == SExpressionTokenType.Atom ? reader.GetString() : string.Empty;
            var value = reader.Read() && reader.TokenType == SExpressionTokenType.Atom ? reader.GetString() : string.Empty;
            fromReader.Add((name, value));
        }

        Assert.Equal(fromTree, fromReader);

        var symbolsFromTree = document.Sum(form => form.Descendants("symbol").Count());
        var symbolsFromReader = 0;
        var counter = new SExpressionReader(text);
        while (counter.Read())
        {
            if (counter.TokenType == SExpressionTokenType.StartForm && counter.ValueEquals("symbol"))
            {
                symbolsFromReader++;
            }
        }

        Assert.Equal(symbolsFromTree, symbolsFromReader);
    }

    /// <summary>
    /// Every corpus file, with a placeholder row when the corpus is absent.
    /// </summary>
    /// <remarks>
    /// MEASURED, by running the suite against a corpus path that does not exist: a theory that yields
    /// NO cases fails with "No data found" rather than skipping, so a corpus-less run -- which is what
    /// CI does -- turns these into two red tests. One placeholder row, which the body returns on,
    /// is how <c>FidelityTests.AllFiles</c> already handles it.
    /// </remarks>
    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Corpus.All())
        {
            data.Add(f);
        }

        if (data.Count == 0)
        {
            data.Add("<no corpus>");
        }

        return data;
    }

    private static void Flatten(SItemList items, List<string> into)
    {
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case SItemKind.Expression:
                    into.Add("(" + item.Expression!.Token);
                    Flatten(item.Expression.Items, into);
                    into.Add(")");
                    break;
                case SItemKind.Atom:
                    into.Add("a" + item.Text);
                    break;
                case SItemKind.Comment:
                    into.Add("#" + item.Text);
                    break;
            }
        }
    }

    [Fact]
    public void ValueSpan_KeepsEscapesAndGetStringDecodesThem()
    {
        var reader = new SExpressionReader("(a \"say \\\"hi\\\"\")");
        Assert.True(reader.Read());
        Assert.True(reader.Read());

        Assert.Equal(SExpressionTokenType.Atom, reader.TokenType);
        Assert.True(reader.ValueIsEscaped);
        Assert.Equal("say \\\"hi\\\"", reader.ValueSpan.ToString());
        Assert.Equal("say \"hi\"", reader.GetString());
        Assert.Equal(reader.GetString(), SExpression.Parse("(a \"say \\\"hi\\\"\")").GetValue(0));
    }

    [Fact]
    public void ValueEquals_ComparesThroughEscapesWithoutAllocating()
    {
        var reader = new SExpressionReader("(a \"say \\\"hi\\\"\" plain)");
        reader.Read();
        reader.Read();

        Assert.True(reader.ValueEquals("say \"hi\""));
        Assert.False(reader.ValueEquals("say \\\"hi\\\""));
        Assert.False(reader.ValueEquals("say \"hi"));

        reader.Read();
        Assert.True(reader.ValueEquals("plain"));
        Assert.False(reader.ValueEquals("plai"));
    }

    [Fact]
    public void SkipForm_LandsOnTheMatchingEndForm()
    {
        var reader = new SExpressionReader("(root (skip (deep 1) 2) (keep 3))");
        reader.Read();                                   // (root
        reader.Read();                                   // (skip

        Assert.True(reader.SkipForm());
        Assert.Equal(SExpressionTokenType.EndForm, reader.TokenType);
        Assert.Equal(1, reader.Depth);

        Assert.True(reader.Read());
        Assert.Equal(SExpressionTokenType.StartForm, reader.TokenType);
        Assert.True(reader.ValueEquals("keep"));
    }

    [Fact]
    public void TryReadChild_FindsADirectChildAndIgnoresGrandchildrenOfTheSameName()
    {
        var reader = new SExpressionReader("(root (other (uuid \"wrong\")) (uuid \"right\"))");
        reader.Read();

        Assert.True(reader.TryReadChild("uuid"));
        Assert.True(reader.Read());
        Assert.Equal("right", reader.GetString());
    }

    [Fact]
    public void TryReadChild_StopsAtTheEndOfTheEnclosingForm()
    {
        var reader = new SExpressionReader("(root (a 1)) (sibling (missing 2))");
        reader.Read();

        Assert.False(reader.TryReadChild("missing"));
        Assert.Equal(SExpressionTokenType.EndForm, reader.TokenType);
        Assert.Equal(0, reader.Depth);
    }

    [Theory]
    [InlineData("(a", "Unexpected end of input")]
    [InlineData("(a))", "Unbalanced")]
    [InlineData("(a \"x)", "Unterminated")]
    public void MalformedInput_Throws(string text, string expected)
    {
        var ex = Assert.Throws<SExpressionFormatException>(() =>
        {
            var reader = new SExpressionReader(text);
            while (reader.Read())
            {
            }
        });

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValue_ReadsNumbersAndKiCadBooleans()
    {
        var reader = new SExpressionReader("(a 1.27 42 yes no maybe)");
        reader.Read();

        reader.Read();
        Assert.True(reader.TryGetValue<double>(out var d));
        Assert.Equal(1.27, d, 6);

        reader.Read();
        Assert.True(reader.TryGetValue<int>(out var i));
        Assert.Equal(42, i);

        reader.Read();
        Assert.True(reader.TryGetValue<bool>(out var yes));
        Assert.True(yes);

        reader.Read();
        Assert.True(reader.TryGetValue<bool>(out var no));
        Assert.False(no);

        reader.Read();
        Assert.False(reader.TryGetValue<bool>(out _));
    }

    /// <summary>
    /// The claim the reader exists for, asserted rather than described: reading a whole document and
    /// keeping nothing allocates nothing that scales with the document.
    /// </summary>
    [Fact]
    public void ReadingADocumentAndKeepingNothing_AllocatesNothing()
    {
        var text = "(root " + string.Join(' ', Enumerable.Range(0, 5_000).Select(i => $"(n{i} {i} \"v{i}\")")) + ")";

        Count(text); // warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        var forms = Count(text);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(5_001, forms);
        Assert.Equal(0, allocated);
    }

    private static int Count(string text)
    {
        var reader = new SExpressionReader(text);
        var forms = 0;
        while (reader.Read())
        {
            if (reader.TokenType == SExpressionTokenType.StartForm)
            {
                forms++;
            }
        }

        return forms;
    }
}
