namespace SExpressions.Tests;

/// <summary>
/// The contracts of the mutable views -- <see cref="SExpression.Items"/>, <see cref="SExpression.Children"/>,
/// <see cref="SExpression.Values"/> -- and of the walks over a form, where they used to corrupt the
/// tree, lose data on save, or never return (#26 to #30).
/// </summary>
/// <remarks>
/// Each fix's tests fail on 95311b7, except the few that pin what already held and must stay so:
/// <c>Insert</c> at <c>Count</c> still appends, <c>Values.Insert(-1)</c> still throws, a descendant
/// may still move up or out, and <c>Descendants</c> keeps its order. None of them calls
/// <c>ToText</c> on a tree the old code would have left cyclic: that overflows the stack and takes
/// the test host with it.
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

    // --------------------------------------------------- #28: an out-of-range Insert throws

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void ChildrenInsert_OutsideZeroToCount_Throws_AndChangesNothing(int index)
    {
        const string Source = "(root\n\t(a 1)\n\t(b 2)\n\t(c 3)\n)\n";
        var document = SDocument.Parse(Source);
        var children = document.Root!.Children;
        var own = children[0];

        Assert.Throws<ArgumentOutOfRangeException>(() => children.Insert(index, new SExpression("n")));
        Assert.Throws<ArgumentOutOfRangeException>(() => children.Insert(index, own));

        Assert.False(document.IsModified);
        Assert.Equal(Source, document.ToText());
    }

    [Fact]
    public void ChildrenInsert_AtCount_StillAppends()
    {
        var document = SDocument.Parse("(root\n\t(a 1)\n\t(b 2)\n)\n");
        var children = document.Root!.Children;

        children.Insert(children.Count, new SExpression("n"));

        Assert.Equal("(root\n\t(a 1)\n\t(b 2)\n\t(n)\n)\n", document.ToText());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void ValuesInsert_OutsideZeroToCount_Throws_AndChangesNothing(int index)
    {
        const string Source = "(at 1 2 (x))\n";
        var document = SDocument.Parse(Source);
        var values = document.Root!.Values;

        Assert.Throws<ArgumentOutOfRangeException>(() => values.Insert(index, "9"));

        Assert.False(document.IsModified);
        Assert.Equal(Source, document.ToText());
    }

    [Fact]
    public void ValuesInsert_AtCount_StillAppendsAfterTheLastAtom()
    {
        var document = SDocument.Parse("(at 1 2 (x))\n");
        var values = document.Root!.Values;

        values.Insert(values.Count, "9");

        Assert.Equal("(at 1 2 9 (x))\n", document.ToText());
    }

    // ------------------------------------ #29: a walk visits what the form held when it started

    [Fact]
    public void ForeachOverChildren_MovingEachToAnotherForm_MovesThemAll()
    {
        var document = SDocument.Parse("(root\n\t(a 1)\n\t(b 2)\n\t(c 3)\n)\n");
        var destination = new SExpression("dst");

        foreach (var child in document.Root!.Children)
        {
            destination.AddChild(child);
        }

        Assert.Equal(["a", "b", "c"], destination.Children.Select(c => c.Token));
        Assert.Empty(document.Root.Children);
    }

    [Fact]
    public void ForeachOverChildren_RemovingEach_RemovesThemAll()
    {
        var document = SDocument.Parse("(root\n\t(a 1)\n\t(b 2)\n\t(c 3)\n)\n");
        var children = document.Root!.Children;

        foreach (var child in children)
        {
            Assert.True(children.Remove(child));
        }

        Assert.Empty(children);
        Assert.Equal("(root\n)\n", document.ToText());
    }

    [Fact]
    public void ForeachOverTheDocument_MovingEachFormToAnother_MovesThemAll()
    {
        var source = SDocument.Parse("(a)\n(b)\n(c)\n");
        var destination = new SDocument();

        foreach (var form in source)
        {
            destination.Add(form);
        }

        Assert.Equal(3, destination.Count);
        Assert.Empty(source);
    }

    [Fact]
    public void ForeachOverItems_InsertingInFrontOfEach_VisitsEachOriginalItemOnce()
    {
        var document = SDocument.Parse("(root 1 (a) # note\n 2)\n");
        var items = document.Root!.Items;
        var visited = new List<string>();

        foreach (var item in items)
        {
            visited.Add(item.ToString());
            items.Insert(0, SItem.CreateAtom("x"));
            Assert.True(visited.Count <= 10, "the walk followed the inserts instead of ending");
        }

        Assert.Equal(["1", "(a )", "# note", "2"], visited);
        Assert.Equal(8, items.Count);
    }

    [Fact]
    public void ForeachOverValues_AppendingDuringTheWalk_Ends_AndAddRangeOfItselfDoubles()
    {
        var document = SDocument.Parse("(at 1 2)\n");
        var values = document.Root!.Values;
        var visited = 0;

        foreach (var value in values)
        {
            values.Add(value + "0");
            Assert.True(++visited <= 10, "the walk followed the appends instead of ending");
        }

        Assert.Equal(2, visited);
        Assert.Equal(["1", "2", "10", "20"], values.ToArray());

        // What never returned: every append made the walk one longer.
        values.AddRange(values);
        Assert.Equal(["1", "2", "10", "20", "1", "2", "10", "20"], values.ToArray());
    }

    [Fact]
    public void GetChildrenDescendantsAndComments_VisitWhatWasThereWhenTheWalkStarted()
    {
        // Siblings side by side, so that a live walk, shifted by one removal, steps over the next.
        var document = SDocument.Parse("(root\n\t# one\n\t# two\n\t(s 1)\n\t(s 2 (s 3))\n\t(t)\n)\n");
        var root = document.Root!;
        var elsewhere = new SExpression("elsewhere");

        var moved = new List<string>();
        foreach (var s in root.GetChildren("s"))
        {
            elsewhere.AddChild(s);
            moved.Add(s.GetValue(0)!);
        }

        Assert.Equal(["1", "2"], moved);
        Assert.Empty(root.GetChildren("s"));

        var comments = new List<string>();
        foreach (var comment in root.Comments)
        {
            root.Items.RemoveAt(root.Items.IndexOf(SItem.CreateComment(comment)));
            comments.Add(comment);
        }

        Assert.Equal([" one", " two"], comments);
        Assert.Empty(root.Comments);

        var descendants = new List<string>();
        foreach (var d in elsewhere.Descendants("s"))
        {
            descendants.Add(d.GetValue(0)!);
            d.Parent!.Children.Remove(d);
        }

        Assert.Equal(["1", "2", "3"], descendants);
    }

    [Fact]
    public void Descendants_VisitsDepthFirst_AFormBeforeWhatItHolds_WithAndWithoutAToken()
    {
        // Descendants walks with one iterator and a stack now, not one iterator per form; the
        // order is pinned against the recursive definition it replaced.
        var document = SDocument.Parse("(r (a (b (c 1) x (d)) (e)) y (f (g (h (i)))) # z\n (j) (k (l) (m (n))))\n");
        var root = document.Root!;

        Assert.Equal(Recursive(root, null).Select(f => f.Token), root.Descendants().Select(f => f.Token));
        Assert.Equal("abcdefghijklmn", string.Concat(root.Descendants().Select(f => f.Token)));
        foreach (var token in new[] { "a", "d", "i", "n", "none" })
        {
            Assert.Equal(Recursive(root, token), root.Descendants(token));
        }

        Assert.Empty(new SExpression("leaf").Descendants());

        static IEnumerable<SExpression> Recursive(SExpression form, string? token)
        {
            foreach (var child in form.Children.ToArray())
            {
                if (token is null || child.Token == token)
                {
                    yield return child;
                }

                foreach (var d in Recursive(child, token))
                {
                    yield return d;
                }
            }
        }
    }

    [Fact]
    public void AWalk_IsFixedWhenItStarts_WhileCountAndTheIndexerStayLive()
    {
        var document = SDocument.Parse("(root (a) (b))\n");
        var children = document.Root!.Children;

        using var walk = children.GetEnumerator();
        children.Add(new SExpression("c"));

        Assert.Equal(3, children.Count);
        Assert.Equal("c", children[2].Token);

        // The walk was fixed when it started, before c; a new one sees c.
        var first = new List<string>();
        while (walk.MoveNext())
        {
            first.Add(walk.Current.Token);
        }

        Assert.Equal(["a", "b"], first);
        Assert.Equal(["a", "b", "c"], children.Select(c => c.Token));
    }

    // ------------------------------------------------------------------ #30: no parent cycles

    public static TheoryData<string> PlacingEntryPoints() =>
    [
        "AddChild", "Children.Add", "Children.AddRange", "Children.Insert", "Children[i] =",
        "Items.Add", "Items.Insert", "Items[i] =",
    ];

    [Theory]
    [MemberData(nameof(PlacingEntryPoints))]
    public void AddingAFormIntoItsOwnDescendant_Throws_AndChangesNothing(string entryPoint)
    {
        // root into a, its child, and root into b, its grandchild. The indexer cases replace the
        // one child a (or b) holds.
        const string Source = "(root\n\t(a\n\t\t(b\n\t\t\t(e 1)\n\t\t)\n\t)\n\t(c 2)\n)\n";
        foreach (var path in new[] { "a", "a/b" })
        {
            var document = SDocument.Parse(Source);
            var root = document.Root!;
            var destination = root.Find(path)!;

            var error = Assert.Throws<InvalidOperationException>(() => Place(entryPoint, destination, root));

            Assert.Contains("(root)", error.Message, StringComparison.Ordinal);
            Assert.Same(root, document.Root);
            Assert.Null(root.Parent);
            Assert.Same(root, root.Find("a")!.Parent);
            Assert.False(document.IsModified);
            Assert.Equal(Source, document.ToText());
        }
    }

    [Theory]
    [MemberData(nameof(PlacingEntryPoints))]
    public void AddingAFormToItself_Throws(string entryPoint)
    {
        var x = new SExpression("x");
        x.AddChild(new SExpression("y"));

        Assert.Throws<InvalidOperationException>(() => Place(entryPoint, x, x));

        Assert.Null(x.Parent);
        Assert.Equal(["y"], x.Children.Select(c => c.Token));
        Assert.Equal("(x\n\t(y)\n)\n", x.ToText());
    }

    [Fact]
    public void AddRange_WithOneFormThatWouldNestItself_MovesNoneOfThem()
    {
        var document = SDocument.Parse("(root\n\t(a\n\t\t(b 1)\n\t)\n\t(c 2)\n)\n");
        var root = document.Root!;
        var b = root.Find("a/b")!;
        var c = root.GetChild("c")!;

        Assert.Throws<InvalidOperationException>(() => b.Children.AddRange([c, root]));

        Assert.Same(root, c.Parent);
        Assert.Empty(b.Children);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void MovingADescendantUpOrOut_IsStillAllowed()
    {
        var document = SDocument.Parse("(root\n\t(a\n\t\t(b\n\t\t\t(d 4)\n\t\t)\n\t)\n)\n");
        var root = document.Root!;
        var b = root.Find("a/b")!;
        var d = b.GetChild("d")!;

        root.AddChild(d);
        Assert.Same(root, d.Parent);

        document.Add(b);
        Assert.Null(b.Parent);
        Assert.Equal(2, document.Count);

        var reparsed = SDocument.Parse(document.ToText());
        Assert.Equal(["root", "b"], reparsed.Forms.Select(f => f.Token));
        Assert.Equal(["a", "d"], reparsed.Root!.Children.Select(c => c.Token));
        Assert.Empty(reparsed.Root.GetChild("a")!.Children);
    }

    // ------------------------------------------------------------------------------- helpers

    private static void Place(string entryPoint, SExpression destination, SExpression form)
    {
        var children = destination.Children;
        var items = destination.Items;
        switch (entryPoint)
        {
            case "AddChild":
                destination.AddChild(form);
                break;
            case "Children.Add":
                children.Add(form);
                break;
            case "Children.AddRange":
                children.AddRange([form]);
                break;
            case "Children.Insert":
                children.Insert(0, form);
                break;
            case "Children[i] =":
                children[0] = form;
                break;
            case "Items.Add":
                items.Add(SItem.CreateExpression(form));
                break;
            case "Items.Insert":
                items.Insert(0, SItem.CreateExpression(form));
                break;
            case "Items[i] =":
                items[0] = SItem.CreateExpression(form);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, null);
        }
    }
}
