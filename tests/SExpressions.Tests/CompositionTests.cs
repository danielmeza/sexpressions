namespace SExpressions.Tests;

/// <summary>
/// Composition: what has to hold when a node is ADDED to (or removed from) a parsed file rather
/// than edited in place.
/// </summary>
/// <remarks>
/// <para>
/// The library's reason to exist is that a save keeps the original file and changes only what was
/// meant to change. That held for edits and broke for composition. Measured against SExpressions
/// 0.1.1 on <c>templates/orbion-rs485-bridge/orbion-rs485-bridge.kicad_sch</c> (170 001 bytes),
/// appending one <c>(wire ...)</c> to the root emitted <b>+466 bytes and rewrote 305 lines nobody
/// had touched</b> -- every child of the root came back one tab deeper and the root's closing paren
/// gained an indent. A value edit on the same file was +/-6 bytes on one line.
/// </para>
/// <para>
/// The assertions here are on BYTES, deliberately. Re-parsing the output and comparing it to a
/// parse of the output proves nothing about layout, which is the entire property under test.
/// </para>
/// </remarks>
public class CompositionTests
{
    private const string MultiLine = "(root\n\t(a 1)\n\t(b 2)\n\t(c 3)\n)\n";
    private const string OneLine = "(root (a 1) (b 2) (c 3))\n";

    // ------------------------------------------------------- the reviewer's eight-line repro

    [Fact]
    public void Append_ToAMultiLineForm_TouchesNothingButTheNewNode()
    {
        var document = SDocument.Parse(MultiLine);
        document.Root!.CreateChild("d", "4");

        var after = document.ToText();

        // Every other byte of the file, in its original order, is still there...
        Assert.Equal(7, AssertSingleInsertion(MultiLine, after).Length);

        // ...and the node landed where the file already put its children.
        Assert.Equal("(root\n\t(a 1)\n\t(b 2)\n\t(c 3)\n\t(d 4)\n)\n", after);
    }

    [Fact]
    public void Append_ToAMultiLineForm_KeepsEverySiblingLineByteIdentical()
    {
        var document = SDocument.Parse(MultiLine);
        document.Root!.CreateChild("d", "4");
        var after = document.ToText();

        // Named individually because "the siblings nobody touched" is the actual complaint: an
        // indent-level regression changes each of these and still passes a re-parse.
        Assert.Contains("\n\t(a 1)\n", after, StringComparison.Ordinal);
        Assert.Contains("\n\t(b 2)\n", after, StringComparison.Ordinal);
        Assert.Contains("\n\t(c 3)\n", after, StringComparison.Ordinal);

        // ...and the container's closing paren keeps its column.
        Assert.EndsWith("\n)\n", after, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ where in the container

    [Fact]
    public void Insert_AtTheStart_PlacesTheNodeAndMovesNothingElse()
    {
        var document = SDocument.Parse(MultiLine);
        document.Root!.Children.Insert(0, new SExpression("d", "4"));

        var after = document.ToText();
        Assert.Equal(7, AssertSingleInsertion(MultiLine, after).Length);
        Assert.Equal("(root\n\t(d 4)\n\t(a 1)\n\t(b 2)\n\t(c 3)\n)\n", after);
    }

    [Fact]
    public void Insert_InTheMiddle_PlacesTheNodeAndMovesNothingElse()
    {
        var document = SDocument.Parse(MultiLine);
        document.Root!.Children.Insert(1, new SExpression("d", "4"));

        var after = document.ToText();
        Assert.Equal(7, AssertSingleInsertion(MultiLine, after).Length);
        Assert.Equal("(root\n\t(a 1)\n\t(d 4)\n\t(b 2)\n\t(c 3)\n)\n", after);
    }

    [Fact]
    public void Append_AtTheEnd_PlacesTheNodeAndMovesNothingElse()
    {
        var document = SDocument.Parse(MultiLine);
        document.Root!.Children.Add(new SExpression("d", "4"));

        var after = document.ToText();
        Assert.Equal(7, AssertSingleInsertion(MultiLine, after).Length);
        Assert.Equal("(root\n\t(a 1)\n\t(b 2)\n\t(c 3)\n\t(d 4)\n)\n", after);
    }

    // -------------------------------------------------------------------- the awkward containers

    [Fact]
    public void Append_ToAnEmptyForm_AddsTheNodeAndOneSpace()
    {
        // There is no separator to copy here, and no gap to reuse. One space is the least the
        // insert can cost, and "(foo\n\t(d 4)\n)" would be a re-layout of a form that had none.
        var document = SDocument.Parse("(foo)\n");
        document.Root!.CreateChild("d", "4");

        Assert.Equal("(foo (d 4))\n", document.ToText());
        Assert.Equal(6, AssertSingleInsertion("(foo)\n", document.ToText()).Length);
    }

    [Fact]
    public void Append_ToAFormWhoseChildrenAreAllOnOneLine_KeepsThemOnThatLine()
    {
        // The trap: copying the *gap* before a sibling rather than its whitespace SEPARATOR gets
        // "(root" copied in again, and re-laying the form out breaks three untouched siblings onto
        // lines of their own. KiCad reads either, but neither is what the file said.
        var document = SDocument.Parse(OneLine);
        document.Root!.CreateChild("d", "4");

        Assert.Equal("(root (a 1) (b 2) (c 3) (d 4))\n", document.ToText());
        Assert.Equal(6, AssertSingleInsertion(OneLine, document.ToText()).Length);
    }

    [Fact]
    public void Insert_IntoAFormWhoseChildrenAreAllOnOneLine_StaysOnThatLine()
    {
        var document = SDocument.Parse(OneLine);
        document.Root!.Children.Insert(1, new SExpression("d", "4"));

        Assert.Equal("(root (a 1) (d 4) (b 2) (c 3))\n", document.ToText());
    }

    [Fact]
    public void Append_KeepsTheBlankLinesBetweenSiblings()
    {
        // A blank line between siblings is layout the source chose and the writer never gets to
        // have an opinion about. A canonical re-layout silently eats every one of them.
        const string Spaced = "(root\n\n\t(a 1)\n\n\t(b 2)\n\n)\n";
        var document = SDocument.Parse(Spaced);
        document.Root!.CreateChild("c", "3");

        Assert.Equal("(root\n\n\t(a 1)\n\n\t(b 2)\n\n\t(c 3)\n\n)\n", document.ToText());
        Assert.Equal(8, AssertSingleInsertion(Spaced, document.ToText()).Length);
    }

    [Fact]
    public void Append_UsesTheFilesIndentation_NotTheTreeDepth()
    {
        // The corpus is full of files whose indentation does not match their nesting -- KiCad's own
        // generators emit them. A new node has to join the file it is going into, so its separator
        // and its inner indent both come off the neighbours, not off the depth counter.
        const string Shallow = "(root\n(inner\n\t\t(a 1)\n\t)\n)\n";
        var document = SDocument.Parse(Shallow);
        document.Root!.GetChild("inner")!.CreateChild("b", "2");

        Assert.Equal("(root\n(inner\n\t\t(a 1)\n\t\t(b 2)\n\t)\n)\n", document.ToText());
        Assert.Equal(8, AssertSingleInsertion(Shallow, document.ToText()).Length);
    }

    [Fact]
    public void AddValue_PutsAnAtomBesideItsToken_NotOnALineOfItsOwn()
    {
        // An atom with no atom sibling still belongs on the token's line: that is how every
        // dialect of this format writes "(token value ...)".
        const string Source = "(root\n\t(a 1)\n)\n";
        var document = SDocument.Parse(Source);
        document.Root!.AddValue("first");

        Assert.Equal("(root first\n\t(a 1)\n)\n", document.ToText());
        Assert.Equal(6, AssertSingleInsertion(Source, document.ToText()).Length);
    }

    [Fact]
    public void AddValue_CopiesTheSeparatorOfTheAtomsAlreadyThere()
    {
        const string Source = "(at 0 0)\n";
        var document = SDocument.Parse(Source);
        document.Root!.AddValue("90");

        Assert.Equal("(at 0 0 90)\n", document.ToText());
    }

    // ------------------------------------------------------------------------ comments

    [Fact]
    public void AddComment_ToAOneLineForm_BreaksTheLineSoNothingIsSwallowed()
    {
        // A comment runs to the end of its line. Splicing one into a single-line form without a
        // line break would swallow the rest of the form -- including its closing paren -- into the
        // comment text, and the file would no longer parse at all. This is the one case where an
        // untouched sibling's leading whitespace has to change to place the new item.
        var document = SDocument.Parse(OneLine);
        document.Root!.Items.Insert(1, SItem.CreateComment(" note"));

        var after = document.ToText();
        var reparsed = SDocument.Parse(after);
        Assert.Equal(3, reparsed.Root!.Children.Count);
        Assert.Single(reparsed.Root.Comments);
        Assert.Equal(" note", reparsed.Root.Comments.First());
    }

    [Fact]
    public void AddComment_ToAMultiLineForm_LandsOnItsOwnLine()
    {
        const string Source = "(root\n\t(a 1)\n\t(b 2)\n)\n";
        var document = SDocument.Parse(Source);
        document.Root!.AddComment(" why");

        Assert.Equal("(root\n\t(a 1)\n\t(b 2)\n\t# why\n)\n", document.ToText());
        Assert.Equal(7, AssertSingleInsertion(Source, document.ToText()).Length);
    }

    // ------------------------------------------------------------------------- removal

    [Fact]
    public void Remove_FromTheMiddle_TakesItsOwnSeparatorWithIt()
    {
        var document = SDocument.Parse(MultiLine);
        Assert.True(document.Root!.RemoveChild("b"));

        Assert.Equal("(root\n\t(a 1)\n\t(c 3)\n)\n", document.ToText());
        Assert.Equal(7, AssertSingleDeletion(MultiLine, document.ToText()).Length);
    }

    [Fact]
    public void Remove_TheFirstChild_LeavesTheHeaderAlone()
    {
        var document = SDocument.Parse(MultiLine);
        Assert.True(document.Root!.RemoveChild("a"));

        Assert.Equal("(root\n\t(b 2)\n\t(c 3)\n)\n", document.ToText());
    }

    [Fact]
    public void Remove_TheLastChild_LeavesTheCloserWhereItWas()
    {
        var document = SDocument.Parse(MultiLine);
        Assert.True(document.Root!.RemoveChild("c"));

        Assert.Equal("(root\n\t(a 1)\n\t(b 2)\n)\n", document.ToText());
    }

    [Fact]
    public void Remove_FromAOneLineForm_KeepsItOnOneLine()
    {
        var document = SDocument.Parse(OneLine);
        Assert.True(document.Root!.RemoveChild("b"));

        Assert.Equal("(root (a 1) (c 3))\n", document.ToText());
    }

    [Fact]
    public void Remove_EverySibling_StillKeepsTheHeaderAndTheCloser()
    {
        var document = SDocument.Parse(MultiLine);
        document.Root!.Children.Clear();

        Assert.Equal("(root\n)\n", document.ToText());
    }

    // -------------------------------------------------------- composing from parsed fragments

    [Fact]
    public void AppendingAParsedFragment_CopiesItsBytesVerbatim()
    {
        // This is what the consuming tool does: parse a fragment written by hand elsewhere and
        // splice it in whole. An untouched fragment still knows its own source, so it arrives byte
        // for byte -- odd spacing, quoting style and all.
        const string Fragment = "(sheet   (at 1 2)\t(size 3 4))";
        var fragment = new SExpressionParser().Parse(Fragment);

        var document = SDocument.Parse(MultiLine);
        document.Root!.AddChild(fragment);

        var after = document.ToText();
        Assert.Equal(Fragment.Length + 2, AssertSingleInsertion(MultiLine, after).Length);
        Assert.Equal("(root\n\t(a 1)\n\t(b 2)\n\t(c 3)\n\t" + Fragment + "\n)\n", after);
    }

    // ----------------------------------------------------------------------- the real corpus

    /// <summary>
    /// The realistic case, on the 170 001-byte schematic the defect was reported against.
    /// </summary>
    /// <remarks>
    /// MEASURED, same file and same insert, three versions of this library:
    /// <list type="bullet">
    /// <item>0.1.1: +466 bytes, 305 untouched lines rewritten, 316 lines emitted.</item>
    /// <item>master before this fix: +150 bytes, 0 untouched lines rewritten, 11 new lines --
    /// correct only by luck, because the root's children happen to sit at the tab depth the
    /// re-formatter would have chosen for them.</item>
    /// <item>after this fix: +150 bytes, 0 untouched lines rewritten, 11 new lines, and the diff
    /// is a single contiguous insertion.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public void AppendingOneNodeToARealSchematic_ChangesOnlyThatNode()
    {
        if (Corpus.Missing)
        {
            return;
        }

        const string Relative = "templates/orbion-rs485-bridge/orbion-rs485-bridge.kicad_sch";
        var src = Corpus.Read(Relative);
        Assert.Equal(170_001, src.Length);

        var document = SDocument.Parse(src);
        var wire = document.Root!.CreateChild("wire");
        var pts = wire.CreateChild("pts");
        pts.CreateChild("xy", "100", "100");
        pts.CreateChild("xy", "120", "100");
        wire.CreateChild("uuid").AddValue("00000000-0000-0000-0000-0000000000ff", SQuoteStyle.Quoted);

        var after = document.ToText();
        var inserted = AssertSingleInsertion(src, after);

        Assert.Equal(7, inserted.Count(c => c == '\n'));
        Assert.Equal(src.Length + inserted.Length, after.Length);
        Assert.Contains("\n\t(wire\n\t\t(pts\n\t\t\t(xy 100 100)\n", after, StringComparison.Ordinal);

        // The line the wire was appended after, and the root's closer, are exactly as they were.
        Assert.Contains("\n\t(sheet_instances\n", after, StringComparison.Ordinal);
        Assert.EndsWith("\n)\n", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// The container that caught the version of this fix that read indentation off the tree instead
    /// of off the file. In this corpus <c>(lib_symbols</c> sits at one tab and its 19
    /// <c>(symbol</c> children also sit at one tab, not the two their nesting implies -- KiCad's
    /// own generator wrote it that way.
    /// </summary>
    /// <remarks>
    /// MEASURED: 0.1.1 and master both re-indent all 19 untouched symbol headers (+47 bytes,
    /// 19 lines rewritten). After this fix: +27 bytes, 0 lines rewritten, 1 line added.
    /// </remarks>
    [Fact]
    public void AppendingToAFormWhoseChildrenAreMisindented_LeavesTheSiblingsWhereTheyAre()
    {
        if (Corpus.Missing)
        {
            return;
        }

        var src = Corpus.Read("templates/orbion-rs485-bridge/orbion-rs485-bridge.kicad_sch");
        var document = SDocument.Parse(src);
        var libSymbols = document.Root!.GetChild("lib_symbols")!;
        Assert.True(libSymbols.Children.Count > 15, "expected a well-populated lib_symbols");

        libSymbols.CreateChild("symbol").AddValue("orbion:NEW", SQuoteStyle.Quoted);

        var after = document.ToText();
        Assert.Equal(23, AssertSingleInsertion(src, after).Length);
        Assert.Contains("\n\t(symbol \"orbion:NEW\")\n", after, StringComparison.Ordinal);

        // Every one of the 19 symbol headers still sits at the single tab the file gave it.
        Assert.Equal(src.Split("\n\t(symbol ").Length + 1, after.Split("\n\t(symbol ").Length);
        Assert.DoesNotContain("\n\t\t(symbol \"orbion:", after, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingOneNodeFromARealSchematic_ChangesOnlyThatNode()
    {
        if (Corpus.Missing)
        {
            return;
        }

        var src = Corpus.Read("templates/orbion-rs485-bridge/orbion-rs485-bridge.kicad_sch");
        var document = SDocument.Parse(src);
        var symbol = document.Root!.FindAll("lib_symbols/symbol").First();
        var removed = symbol.Children.Skip(1).First();
        Assert.True(symbol.Children.Remove(removed));

        var after = document.ToText();
        var deleted = AssertSingleDeletion(src, after);
        Assert.Contains("(pin_numbers", deleted, StringComparison.Ordinal);
        Assert.Equal(src.Length - deleted.Length, after.Length);
    }

    /// <summary>
    /// Reality: a file this library COMPOSED has to load in KiCad, not merely re-parse in .NET.
    /// Two inserts at once, and the netlist KiCad reads out of the result is identical to the one
    /// it reads out of the original -- neither insert changes connectivity, so any difference here
    /// would mean KiCad read the composed file differently.
    /// </summary>
    [Fact]
    public void AComposedSchematic_LoadsInKiCad_AndReadsTheSame()
    {
        if (Corpus.Missing || Corpus.KiCadCli is null)
        {
            return;
        }

        const string Relative = "templates/orbion-rs485-bridge/orbion-rs485-bridge.kicad_sch";
        var src = Corpus.Read(Relative);
        var document = SDocument.Parse(src);

        // (1) a whole graphic item, cloned from one already in the file and given a fresh uuid:
        //     this is composition -- a parsed fragment inserted whole.
        var template = document.Root!.Children.First(c => c.Token == "text");
        var copy = template.Clone();
        copy.SetChildValue("uuid", "d0d0d0d0-0000-4000-8000-00000000c0de", SQuoteStyle.Quoted);
        document.Root.AddChild(copy);

        // (2) an insert into a container whose children are all on ONE line: KiCad writes
        //     "(effects (font (size ...) (thickness ...)) ...)" that way, and this is the case a
        //     naive separator fix gets wrong.
        var font = document.Root.FindAll("text/effects/font").First();
        Assert.True(font.SourceSpan.IndexOf('\n') < 0, "expected a single-line (font ...) form");
        font.CreateChild("italic", "no");

        var composed = document.ToText();
        Assert.Contains("(italic no))", composed, StringComparison.Ordinal);

        var baseline = Corpus.Stage(Relative, src);
        var candidate = Corpus.Stage(Relative, composed);
        try
        {
            var expected = Netlist(baseline.File, baseline.Dir);
            Assert.True(expected.Length > 20_000, $"the baseline netlist is only {expected.Length} chars; this would prove nothing");
            Assert.Equal(expected, Netlist(candidate.File, candidate.Dir));
        }
        finally
        {
            Directory.Delete(baseline.Dir, recursive: true);
            Directory.Delete(candidate.Dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------- helpers

    private static string Netlist(string schematic, string stagingDir)
    {
        var outFile = Path.Combine(Path.GetDirectoryName(schematic)!, "netlist.net");
        var r = Corpus.RunCli("sch", "export", "netlist", "--format", "kicadsexpr", "-o", outFile, schematic);
        Assert.True(File.Exists(outFile), $"KiCad did not load the schematic (exit {r.ExitCode}): {r.All}");
        return Corpus.NormalizeNetlist(File.ReadAllText(outFile), stagingDir);
    }

    /// <summary>
    /// Asserts that <paramref name="after"/> is <paramref name="before"/> with exactly one
    /// contiguous run of bytes inserted into it -- every other byte identical and in its original
    /// order -- and returns that run. This is the property the library exists for, and it is
    /// strictly stronger than anything a re-parse of the output could show.
    /// </summary>
    /// <remarks>
    /// The run itself is only ever identified up to a rotation: inserting "\n\t(d 4)" in front of
    /// "\n\t(a 1)" is byte-for-byte the same file as inserting "d 4)\n\t(" seven bytes later. The
    /// window reported here is the leftmost one; tests that care where the node landed assert on
    /// the whole text.
    /// </remarks>
    private static string AssertSingleInsertion(string before, string after)
    {
        var (prefix, suffix) = CommonEnds(before, after);
        if (prefix + suffix < before.Length || after.Length < before.Length)
        {
            Assert.Fail(Report("insertion", before, after, prefix, suffix));
        }

        return after[(before.Length - suffix)..(after.Length - suffix)];
    }

    /// <summary>The mirror of <see cref="AssertSingleInsertion"/>: one contiguous run gone, nothing else moved.</summary>
    private static string AssertSingleDeletion(string before, string after)
    {
        var (prefix, suffix) = CommonEnds(before, after);
        if (prefix + suffix < after.Length || before.Length < after.Length)
        {
            Assert.Fail(Report("deletion", before, after, prefix, suffix));
        }

        return before[(after.Length - suffix)..(before.Length - suffix)];
    }

    /// <summary>The longest common prefix and the longest common suffix, each measured on its own.</summary>
    private static (int Prefix, int Suffix) CommonEnds(string before, string after)
    {
        var max = Math.Min(before.Length, after.Length);

        var prefix = 0;
        while (prefix < max && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < max && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        return (prefix, suffix);
    }

    private static string Report(string kind, string before, string after, int prefix, int suffix)
    {
        var overlap = Math.Max(0, Math.Min(before.Length, after.Length) - prefix - suffix);
        return $"expected a single {kind}, but {overlap} bytes of the original were rewritten "
            + $"({before.Length} -> {after.Length} bytes).\n  first difference at offset {prefix}"
            + $"\n  was: {Show(Around(before, prefix))}\n  now: {Show(Around(after, prefix))}";
    }

    private static string Around(string s, int at) => s[Math.Max(0, at - 40)..Math.Min(s.Length, at + 120)];

    private static string Show(string s)
    {
        var text = s.Length > 400 ? s[..400] + "\u2026" : s;
        return "\"" + text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
    }
}
