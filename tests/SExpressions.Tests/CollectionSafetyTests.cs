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

    // ------------------------------------------------ #27: an inserted item claims no source slot

    [Fact]
    public void ItemsInsert_OfAnAtomParsedFromAnotherDocument_WritesThatAtom()
    {
        // The atom's slot pointed at offset 5 of ITS document. Kept, the writer copied offset 5 of
        // this one -- a space -- and Q vanished from the file while the tree still held it.
        var other = SDocument.Parse("(zzz Q)");
        var document = SDocument.Parse("(root (a 1) (b 2))\n");

        document.Root!.Items.Insert(0, other.Root!.Items[0]);

        Assert.Equal("Q", document.Root.GetValue(0));
        Assert.Equal("(root Q (a 1) (b 2))\n", document.ToText());
        Assert.Equal("Q", SDocument.Parse(document.ToText()).Root!.GetValue(0));
    }

    [Fact]
    public void ItemsSet_OfAnAtomParsedFromAnotherDocument_OverAnInsertedItem_WritesThatAtom()
    {
        // The indexer keeps the slot of the item it replaces; one inserted earlier has none, and
        // then the incoming item's own slot, from the other document, was stored instead.
        var other = SDocument.Parse("(zzz Q)");
        var document = SDocument.Parse("(root (a 1) (b 2))\n");
        var items = document.Root!.Items;
        items.Insert(0, SItem.CreateAtom("new"));

        items[0] = other.Root!.Items[0];

        Assert.Equal("(root Q (a 1) (b 2))\n", document.ToText());
    }

    [Fact]
    public void ItemsInsert_OfAnItemFromElsewhere_ChangesOnlyTheBytesItAdds()
    {
        // When the foreign slot did not fit between this form's own, the splice gave up and the
        // whole form was re-laid out canonically: every separator in it rewritten.
        const string Source = "(root   (a 1)\t(b 2))\n";
        var other = SDocument.Parse("(zz \"hello world\")");
        var document = SDocument.Parse(Source);

        document.Root!.Items.Insert(1, other.Root!.Items[0]);

        Assert.Equal("(root   (a 1) \"hello world\"\t(b 2))\n", document.ToText());
    }

    [Fact]
    public void ItemsInsert_OfAnAtomThisFormAlreadyHolds_KeepsTheRestOfItsLayout()
    {
        // The copy claimed the same slot as the original, the slots were out of order, and the form
        // fell back to canonical layout: "(at 0 0 0)".
        var document = SDocument.Parse("(at 0   0)\n");
        var items = document.Root!.Items;

        items.Insert(0, items[1]);

        Assert.Equal("(at 0 0   0)\n", document.ToText());
    }
}
