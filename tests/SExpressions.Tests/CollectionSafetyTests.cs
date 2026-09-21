namespace SExpressions.Tests;

/// <summary>
/// The contracts of the mutable views -- <see cref="SExpression.Items"/>, <see cref="SExpression.Children"/>,
/// <see cref="SExpression.Values"/> -- and of the walks over a form, where they used to corrupt the
/// tree, lose data on save, or never return (#26 to #30).
/// </summary>
/// <remarks>
/// Every test here failed on 0c28600 / 95311b7. None of them calls <c>ToText</c> on a tree that
/// the old code would have left cyclic: that overflows the stack and takes the test host with it.
/// </remarks>
public class CollectionSafetyTests
{
    private static readonly SExpressionWriterOptions Canonical = new() { Format = SExpressionFormat.Canonical };

    // ---------------------------------------------------------------- #26: the Items indexer

    [Fact]
    public void ItemsSet_AtANegativeIndex_Throws_AndLeavesTheFormBeforeItAlone()
    {
        // A parsed form's items are a slice of one block shared with its whole document. Unchecked,
        // b.Items[-1] wrote into the slot in front of b's slice: a's value.
        const string Source = "(root (a 1) (b 2))\n";
        var document = SDocument.Parse(Source);
        var a = document.Root!.Children[0];
        var items = document.Root.Children[1].Items;

        Assert.Throws<ArgumentOutOfRangeException>(() => items[-1] = SItem.CreateAtom("X"));

        Assert.Equal("1", a.GetValue(0));
        Assert.False(document.IsModified);
        Assert.Equal(Source, document.ToText());
        Assert.Equal(SDocument.Parse(Source).ToText(Canonical), document.ToText(Canonical));
    }

    [Fact]
    public void ItemsSet_AtCount_Throws_AndChangesNothing()
    {
        const string Source = "(root (a 1) (b 2))\n";
        var document = SDocument.Parse(Source);
        var items = document.Root!.Children[0].Items;

        Assert.Throws<ArgumentOutOfRangeException>(() => items[items.Count] = SItem.CreateAtom("X"));

        Assert.False(document.IsModified);
        Assert.Equal(SDocument.Parse(Source).ToText(Canonical), document.ToText(Canonical));
    }
}
