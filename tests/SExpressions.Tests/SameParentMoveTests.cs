namespace SExpressions.Tests;

/// <summary>
/// Adding a form to the list it is already in (#25).
/// </summary>
/// <remarks>
/// <para>
/// A form has one parent, so adding one that another form holds moves it. Adding one to the form
/// that already holds it threw <see cref="ArgumentOutOfRangeException"/> in 0.1.3: the index was
/// checked, then the form was taken out of that same list, and an append was one past the end by the
/// time it was copied in. Downstream, kicad-sharp's <c>lib.AddSymbol(lib.Symbols[0])</c> hit it.
/// </para>
/// <para>
/// The contract pinned here: the form MOVES. An insert index is where it ends up, read in the list
/// without it (<c>ObservableCollection&lt;T&gt;.Move</c>), with <c>Count</c> still meaning the end.
/// A move to where it already is changes nothing. Any other move is written exactly as taking the
/// form out to another parent and inserting it back would be, byte for byte, so a move within one
/// form and a move between two cannot drift apart.
/// </para>
/// </remarks>
public class SameParentMoveTests
{
    private static readonly Dictionary<string, (string Text, bool TopLevel)> Layouts = new()
    {
        ["multi-line"] = ("(root\n\t(a 1)\n\t(b 2)\n\t(c 3)\n\t(d 4)\n)\n", false),
        ["one-line"] = ("(root (a 1) (b 2) (c 3) (d 4))\n", false),
        ["blank lines"] = ("(root\n\n\t(a 1)\n\n\t(b 2)\n\n\t(c 3)\n\n\t(d 4)\n\n)\n", false),

        // Atoms and comments between the children, so an item index is not a child index, a
        // comment sits in front of the first child and one after the last, and b carries a
        // comment inside it.
        ["mixed"] = ("(root x y\n\t# about a\n\t(a 1)\n\t(b\n\t\t# inside b\n\t\t2)\n\tz\n\t(c 3)\n\t(d 4)\n\t# trailing\n)\n", false),

        // The document's own list, laid out like a .kicad_dru: forms at column 0 with comments
        // between them.
        ["top level"] = ("(version 1)\n\n# one\n(rule \"one\" (condition \"A\"))\n\n# two\n(rule \"two\" (condition \"B\"))\n(rule \"three\" (condition \"C\"))\n", true),
    };

    public static TheoryData<string> LayoutNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Layouts.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    // ------------------------------------------------------------------------- the report

    [Fact]
    public void AddChild_OfAChildTheFormAlreadyHolds_MovesItToTheEnd_InsteadOfThrowing()
    {
        // The shape of kicad-sharp's lib.AddSymbol(lib.Symbols[0]).
        var document = SDocument.Parse("(lib\n\t(symbol \"A\")\n\t(symbol \"B\")\n\t(symbol \"C\")\n)\n");
        var root = document.Root!;
        var first = root.Children[0];

        root.AddChild(first);

        Assert.Equal(["B", "C", "A"], root.Children.Select(c => c.GetValue(0)));
        Assert.Same(root, first.Parent);
        Assert.Equal(3, root.Children.Count);
        Assert.Equal("(lib\n\t(symbol \"B\")\n\t(symbol \"C\")\n\t(symbol \"A\")\n)\n", document.ToText());
    }

    [Fact]
    public void InsertAtCount_OfAChildTheFormAlreadyHolds_MovesItToTheEnd_LikeAdd()
    {
        var document = SDocument.Parse(Layouts["multi-line"].Text);
        var children = document.Root!.Children;
        var a = children[0];

        children.Insert(children.Count, a);

        Assert.Equal(["b", "c", "d", "a"], children.Select(c => c.Token));
        Assert.Equal("(root\n\t(b 2)\n\t(c 3)\n\t(d 4)\n\t(a 1)\n)\n", document.ToText());
    }

    // ------------------------------------------------------------ every index, every entry point

    /// <summary>
    /// Every child moved to every index from 0 to Count through <see cref="SChildCollection.Insert"/>:
    /// the front, the middle, the end, its own index and the ones either side of it.
    /// </summary>
    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void ChildrenInsert_MovesToTheIndexReadWithoutTheChild_AndWritesLikeAMoveFromAnotherForm(string layout)
    {
        var (text, topLevel) = Layouts[layout];
        var count = Children(SDocument.Parse(text), topLevel).Count;

        for (var from = 0; from < count; from++)
        {
            for (var index = 0; index <= count; index++)
            {
                var document = SDocument.Parse(text);
                var children = Children(document, topLevel);
                var before = children.ToArray();
                var node = before[from];

                children.Insert(index, node);

                var landed = Math.Min(index, count - 1);
                var context = $"{layout}: Insert({index}, child {from})";
                Assert.True(Moved(before, from, landed).SequenceEqual(children), context);
                Assert.Equal(count, children.Count);
                AssertStillOwned(document, topLevel, node);

                if (landed == from)
                {
                    Assert.Equal(text, document.ToText());
                    Assert.False(document.IsModified, context);
                    continue;
                }

                // The reference: the same child taken out to another form, then inserted at the
                // index it is to end up at, in the list that no longer holds it.
                var reference = SDocument.Parse(text);
                var referenceChildren = Children(reference, topLevel);
                var referenceNode = referenceChildren[from];
                new SExpression("elsewhere").AddChild(referenceNode);
                referenceChildren.Insert(landed, referenceNode);

                Assert.Equal(reference.ToText(), document.ToText());
                AssertReparsesAs(document, topLevel, children.ToArray());
            }
        }
    }

    /// <summary>
    /// The same, at the level of <see cref="SExpression.Items"/>, where atoms and comments count as
    /// positions too. The item handed in is the parsed one, still carrying its slot in the file.
    /// </summary>
    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void ItemsInsert_MovesToTheIndexReadWithoutTheItem_AndWritesLikeAMoveFromAnotherForm(string layout)
    {
        var (text, topLevel) = Layouts[layout];
        var count = Items(SDocument.Parse(text), topLevel).Count;

        for (var from = 0; from < count; from++)
        {
            if (Items(SDocument.Parse(text), topLevel)[from].Kind != SItemKind.Expression)
            {
                continue;
            }

            for (var index = 0; index <= count; index++)
            {
                var document = SDocument.Parse(text);
                var items = Items(document, topLevel);
                var before = items.AsSpan().ToArray();
                var node = before[from].Expression!;

                items.Insert(index, before[from]);

                var landed = Math.Min(index, count - 1);
                var context = $"{layout}: Items.Insert({index}, item {from})";
                Assert.True(Moved(Payloads(before), from, landed).SequenceEqual(Payloads(items.AsSpan().ToArray())), context);
                AssertStillOwned(document, topLevel, node);

                if (landed == from)
                {
                    Assert.Equal(text, document.ToText());
                    Assert.False(document.IsModified, context);
                    continue;
                }

                var reference = SDocument.Parse(text);
                var referenceItems = Items(reference, topLevel);
                var referenceNode = referenceItems[from].Expression!;
                new SExpression("elsewhere").AddChild(referenceNode);
                referenceItems.Insert(landed, SItem.CreateExpression(referenceNode));

                Assert.Equal(reference.ToText(), document.ToText());
            }
        }
    }

    /// <summary>
    /// Every way to append: each moves the child after every item the form holds, which for the
    /// last child of a form with a trailing comment means past that comment, as it would for a new
    /// form. Already the last item: nothing changes.
    /// </summary>
    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void EveryAdd_MovesTheChildAfterEveryItem_AndWritesLikeAMoveFromAnotherForm(string layout)
    {
        var (text, topLevel) = Layouts[layout];
        var appends = new (string Name, Action<SDocument, SExpression> Add)[]
        {
            ("AddChild", (d, node) => Append(d, topLevel, node)),
            ("Children.Add", (d, node) => Children(d, topLevel).Add(node)),
            ("Children.AddRange", (d, node) => Children(d, topLevel).AddRange([node])),
            ("Items.Add", (d, node) =>
            {
                // The parsed item itself, still carrying its slot in the file.
                var items = Items(d, topLevel);
                items.Add(items[items.IndexOf(SItem.CreateExpression(node))]);
            }),
            ("SDocument.Add", (d, node) => d.Add(node)),
        };

        var count = Children(SDocument.Parse(text), topLevel).Count;
        foreach (var (name, add) in appends)
        {
            // The document's container is not public, so a top-level AddChild is SDocument.Add.
            if ((name == "SDocument.Add" && !topLevel) || (name == "AddChild" && topLevel))
            {
                continue;
            }

            for (var from = 0; from < count; from++)
            {
                var document = SDocument.Parse(text);
                var items = Items(document, topLevel);
                var node = Children(document, topLevel)[from];
                var itemsBefore = items.AsSpan().ToArray();
                var fromItem = items.IndexOf(SItem.CreateExpression(node));

                add(document, node);

                var context = $"{layout}: {name}(child {from})";
                Assert.True(Moved(Payloads(itemsBefore), fromItem, itemsBefore.Length - 1).SequenceEqual(Payloads(items.AsSpan().ToArray())), context);
                AssertStillOwned(document, topLevel, node);

                if (fromItem == itemsBefore.Length - 1)
                {
                    Assert.Equal(text, document.ToText());
                    Assert.False(document.IsModified, context);
                    continue;
                }

                var reference = SDocument.Parse(text);
                var referenceNode = Children(reference, topLevel)[from];
                new SExpression("elsewhere").AddChild(referenceNode);
                Append(reference, topLevel, referenceNode);

                Assert.Equal(reference.ToText(), document.ToText());
            }
        }
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void AnIndexOutOfRangeForAnyItem_StillThrows_AndChangesNothing(string layout)
    {
        var (text, topLevel) = Layouts[layout];
        var document = SDocument.Parse(text);
        var items = Items(document, topLevel);
        var node = Children(document, topLevel)[0];
        var item = items[items.IndexOf(SItem.CreateExpression(node))];

        Assert.Throws<ArgumentOutOfRangeException>(() => items.Insert(items.Count + 1, item));
        Assert.Throws<ArgumentOutOfRangeException>(() => items.Insert(-1, item));

        AssertStillOwned(document, topLevel, node);
        Assert.Equal(text, document.ToText());
        Assert.False(document.IsModified);
    }

    // ---------------------------------------------------------------------------- the indexer

    /// <summary>
    /// Setting a child in place of another: the one replaced is the one at the index when the
    /// setter runs, it leaves the tree, and the moved child closes the gap it leaves behind. Before
    /// the fix, <c>this[1] = this[0]</c> on <c>[a, b, c, d]</c> overwrote <c>c</c> and kept
    /// <c>b</c> in the list with no parent, and <c>this[3] = this[0]</c> ran off the end.
    /// </summary>
    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void ChildrenSet_WithAChildOfTheSameForm_ReplacesTheChildAtThatIndex(string layout)
    {
        var (text, topLevel) = Layouts[layout];
        var count = Children(SDocument.Parse(text), topLevel).Count;

        for (var from = 0; from < count; from++)
        {
            for (var index = 0; index < count; index++)
            {
                var document = SDocument.Parse(text);
                var children = Children(document, topLevel);
                var before = children.ToArray();
                var node = before[from];
                var replaced = before[index];

                children[index] = node;

                var context = $"{layout}: this[{index}] = child {from}";
                if (index == from)
                {
                    Assert.Equal(text, document.ToText());
                    Assert.False(document.IsModified, context);
                    continue;
                }

                var expected = before.Where(c => !ReferenceEquals(c, node)).Select(c => ReferenceEquals(c, replaced) ? node : c).ToArray();
                Assert.True(expected.SequenceEqual(children), context);
                Assert.Same(node, children[from < index ? index - 1 : index]);
                AssertStillOwned(document, topLevel, node);
                Assert.Null(replaced.Parent);
                Assert.DoesNotContain(replaced, children);

                // The reference: the child taken out to another form first, which shifts the one
                // being replaced down by one when the child sat in front of it.
                var reference = SDocument.Parse(text);
                var referenceChildren = Children(reference, topLevel);
                var referenceNode = referenceChildren[from];
                new SExpression("elsewhere").AddChild(referenceNode);
                referenceChildren[from < index ? index - 1 : index] = referenceNode;

                Assert.Equal(reference.ToText(), document.ToText());
                AssertReparsesAs(document, topLevel, expected);
            }
        }
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void ItemsSet_WithAChildOfTheSameForm_ReplacesTheItemAtThatIndex(string layout)
    {
        var (text, topLevel) = Layouts[layout];
        var count = Items(SDocument.Parse(text), topLevel).Count;

        for (var from = 0; from < count; from++)
        {
            if (Items(SDocument.Parse(text), topLevel)[from].Kind != SItemKind.Expression)
            {
                continue;
            }

            for (var index = 0; index < count; index++)
            {
                var document = SDocument.Parse(text);
                var items = Items(document, topLevel);
                var before = items.AsSpan().ToArray();
                var node = before[from].Expression!;

                items[index] = before[from];

                var context = $"{layout}: Items[{index}] = item {from}";
                if (index == from)
                {
                    Assert.Equal(text, document.ToText());
                    Assert.False(document.IsModified, context);
                    continue;
                }

                var expected = Payloads(before).Where((_, i) => i != from).ToList();
                expected[from < index ? index - 1 : index] = node;
                Assert.True(expected.SequenceEqual(Payloads(items.AsSpan().ToArray())), context);
                AssertStillOwned(document, topLevel, node);
                if (before[index].Expression is { } replaced)
                {
                    Assert.Null(replaced.Parent);
                }

                var reference = SDocument.Parse(text);
                var referenceItems = Items(reference, topLevel);
                var referenceNode = referenceItems[from].Expression!;
                new SExpression("elsewhere").AddChild(referenceNode);
                referenceItems[from < index ? index - 1 : index] = SItem.CreateExpression(referenceNode);

                Assert.Equal(reference.ToText(), document.ToText());
            }
        }
    }

    // ------------------------------------------------------------------------- what travels

    [Fact]
    public void AMovedChild_TakesItsOwnText_CommentsInsideIt_Along()
    {
        // b's inner comment is part of b and moves with it, byte for byte. The separator in front
        // of b's old place goes with it; the one where it lands is copied from the child it now
        // precedes.
        var document = SDocument.Parse(Layouts["mixed"].Text);
        var root = document.Root!;
        var b = root.GetChild("b")!;

        root.Children.Insert(2, b);

        Assert.Equal("(root x y\n\t# about a\n\t(a 1)\n\tz\n\t(c 3)\n\t(b\n\t\t# inside b\n\t\t2)\n\t(d 4)\n\t# trailing\n)\n", document.ToText());
        Assert.False(b.IsModified);
    }

    [Fact]
    public void AMovedChild_LeavesTheCommentInFrontOfItBehind()
    {
        // "# about a" is an item of root, not a part of a, so it stays where it was, exactly as it
        // does when a moves to another form. Moving a comment is a move of its own.
        var document = SDocument.Parse(Layouts["mixed"].Text);
        var root = document.Root!;

        root.Children.Insert(2, root.GetChild("a")!);

        Assert.Equal("(root x y\n\t# about a\n\t(b\n\t\t# inside b\n\t\t2)\n\tz\n\t(c 3)\n\t(a 1)\n\t(d 4)\n\t# trailing\n)\n", document.ToText());
    }

    [Fact]
    public void ChildrenInsertAtZero_LandsInFrontOfTheFirstChild_NotOfTheItemsBeforeIt()
    {
        // Where Children.Insert(0, x) puts x, whether x is new or already here: just in front of
        // the first child, after the atoms and the comment that come before it.
        var document = SDocument.Parse(Layouts["mixed"].Text);
        var root = document.Root!;

        root.Children.Insert(0, root.GetChild("c")!);

        Assert.Equal("(root x y\n\t# about a\n\t(c 3)\n\t(a 1)\n\t(b\n\t\t# inside b\n\t\t2)\n\tz\n\t(d 4)\n\t# trailing\n)\n", document.ToText());
    }

    [Fact]
    public void AMoveToTheSamePlace_ThroughAnyEntryPoint_LeavesTheFileUnmodified()
    {
        var text = Layouts["mixed"].Text;
        var document = SDocument.Parse(text);
        var root = document.Root!;
        var children = root.Children;
        var items = root.Items;

        for (var i = 0; i < children.Count; i++)
        {
            children.Insert(i, children[i]);
            children[i] = children[i];
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Kind == SItemKind.Expression)
            {
                items.Insert(i, items[i]);
                items[i] = items[i];
            }
        }

        // d is the last child but not the last item; Insert(Count) leaves it there.
        children.Insert(children.Count, children[^1]);

        Assert.False(document.IsModified);
        Assert.Equal(text, document.ToText());
    }

    // ----------------------------------------------------------------------- saved and reloaded

    [Fact]
    public void ASameParentMove_SavesAndReloads_WithOnlyTheMovedLinesChanged()
    {
        const string Source = "(kicad_symbol_lib\n\t(version 20241209)\n\t(generator \"test\")\n\t(symbol \"A\"\n\t\t(pin_names (offset 0))\n\t)\n\t(symbol \"B\"\n\t\t(in_bom yes)\n\t)\n\t(symbol \"C\"\n\t\t(on_board yes)\n\t)\n)\n";
        var srcPath = Path.Combine(Path.GetTempPath(), $"sexpr-move-{Guid.NewGuid():N}.kicad_sym");
        var dstPath = Path.Combine(Path.GetTempPath(), $"sexpr-move-out-{Guid.NewGuid():N}.kicad_sym");
        try
        {
            File.WriteAllText(srcPath, Source);
            var document = SDocument.Load(srcPath);
            var root = document.Root!;
            var symbols = root.GetChildren("symbol").ToArray();

            root.Children.Insert(2, symbols[2]);    // C to the front of the symbols
            root.AddChild(symbols[0]);              // A to the end: the call the issue reports
            document.Save(dstPath);

            const string Expected = "(kicad_symbol_lib\n\t(version 20241209)\n\t(generator \"test\")\n\t(symbol \"C\"\n\t\t(on_board yes)\n\t)\n\t(symbol \"B\"\n\t\t(in_bom yes)\n\t)\n\t(symbol \"A\"\n\t\t(pin_names (offset 0))\n\t)\n)\n";
            Assert.Equal(Expected, File.ReadAllText(dstPath));

            var reloaded = SDocument.Load(dstPath);
            Assert.Equal(["C", "B", "A"], reloaded.Root!.GetChildren("symbol").Select(s => s.GetValue(0)));
            Assert.Equal(Expected, reloaded.ToText());

            // Each symbol's own bytes arrived unchanged.
            foreach (var symbol in symbols)
            {
                Assert.Contains(symbol.SourceSpan.ToString(), Expected, StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(srcPath);
            File.Delete(dstPath);
        }
    }

    [Fact]
    public void ASameParentMoveToTheSamePlace_SavesByteForByte()
    {
        var text = Layouts["top level"].Text;
        var srcPath = Path.Combine(Path.GetTempPath(), $"sexpr-nomove-{Guid.NewGuid():N}.kicad_dru");
        var dstPath = Path.Combine(Path.GetTempPath(), $"sexpr-nomove-out-{Guid.NewGuid():N}.kicad_dru");
        try
        {
            File.WriteAllText(srcPath, text);
            var document = SDocument.Load(srcPath);

            document.Add(document.Forms[^1]);
            document.Forms.Insert(1, document.Forms[1]);
            document.Save(dstPath);

            Assert.Equal(File.ReadAllBytes(srcPath), File.ReadAllBytes(dstPath));
        }
        finally
        {
            File.Delete(srcPath);
            File.Delete(dstPath);
        }
    }

    /// <summary>
    /// A real KiCad 10 symbol library, the kind the downstream report came from: every symbol
    /// moved within it, and the result checked twice. Its own bytes: each symbol's text comes back
    /// verbatim. KiCad: it sorts symbols by name when it writes a library, so
    /// <c>kicad-cli sym upgrade</c> of the moved library and of the original must come out
    /// identical if KiCad read the same symbols out of both.
    /// </summary>
    [Fact]
    public void MovingSymbolsWithinARealLibrary_KeepsEverySymbolsBytes_AndKiCadReadsTheSameLibrary()
    {
        if (Corpus.Missing)
        {
            return;
        }

        const string Relative = "libs/orbion.kicad_sym";
        var src = Corpus.Read(Relative);
        var document = SDocument.Parse(src);
        var root = document.Root!;
        var symbols = root.GetChildren("symbol").ToArray();
        Assert.True(symbols.Length > 10, "expected a well-populated library");
        var texts = symbols.Select(s => s.SourceSpan.ToString()).ToArray();

        // Reverse the symbols one move at a time, then send the first to the end the way the
        // downstream report did.
        var firstSymbol = root.Children.IndexOf(symbols[0]);
        foreach (var symbol in symbols)
        {
            root.Children.Insert(firstSymbol, symbol);
        }

        root.AddChild(root.Children[firstSymbol]);

        var moved = document.ToText();
        var reloaded = SDocument.Parse(moved).Root!.GetChildren("symbol").Select(s => s.SourceSpan.ToString()).ToArray();
        Assert.Equal([.. Enumerable.Reverse(texts).Skip(1), texts[^1]], reloaded);

        // Every symbol here sits behind the same "\n\t", so the moves relocate bytes and add none.
        Assert.Equal(src.Length, moved.Length);

        if (Corpus.KiCadCli is null)
        {
            return;
        }

        var baseline = Corpus.Stage(Relative, src);
        var candidate = Corpus.Stage(Relative, moved);
        try
        {
            Assert.Equal(Upgraded(baseline.File), Upgraded(candidate.File));
        }
        finally
        {
            Directory.Delete(baseline.Dir, recursive: true);
            Directory.Delete(candidate.Dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------- helpers

    private static void Append(SDocument document, bool topLevel, SExpression node)
    {
        if (topLevel)
        {
            document.Add(node);
        }
        else
        {
            document.Root!.AddChild(node);
        }
    }

    private static SChildCollection Children(SDocument document, bool topLevel) =>
        topLevel ? document.Forms : document.Root!.Children;

    private static SItemList Items(SDocument document, bool topLevel) =>
        topLevel ? document.Items : document.Root!.Items;

    /// <summary><paramref name="list"/> with the element at <paramref name="from"/> moved to <paramref name="to"/>, read without it.</summary>
    private static List<T> Moved<T>(IReadOnlyList<T> list, int from, int to)
    {
        var result = list.Where((_, i) => i != from).ToList();
        result.Insert(to, list[from]);
        return result;
    }

    /// <summary>What each item holds: the form itself for a child, the text for an atom or a comment.</summary>
    private static object[] Payloads(SItem[] items) =>
        [.. items.Select(i => i.Expression ?? (object)$"{i.Kind}:{i.Text}")];

    private static void AssertStillOwned(SDocument document, bool topLevel, SExpression node)
    {
        Assert.Contains(node, Children(document, topLevel));
        Assert.Equal(1, Children(document, topLevel).Count(c => ReferenceEquals(c, node)));
        if (topLevel)
        {
            Assert.Null(node.Parent);
        }
        else
        {
            Assert.Same(document.Root, node.Parent);
        }
    }

    private static void AssertReparsesAs(SDocument document, bool topLevel, IEnumerable<SExpression> expected)
    {
        var reparsed = SDocument.Parse(document.ToText());
        Assert.Equal(expected.Select(c => c.ToText()), Children(reparsed, topLevel).Select(c => c.ToText()));
    }

    private static string Upgraded(string library)
    {
        var outFile = Path.Combine(Path.GetDirectoryName(library)!, "upgraded.kicad_sym");
        var r = Corpus.RunCli("sym", "upgrade", "--force", "-o", outFile, library);
        Assert.True(File.Exists(outFile), $"kicad-cli did not load the symbol library (exit {r.ExitCode}): {r.All}");
        return File.ReadAllText(outFile);
    }
}
