namespace SExpressions.Tests;

/// <summary>
/// #24: what a node that was added, replaced or moved is indented with. It must line up with the
/// file it is joining -- in that file's own unit, tabs or spaces -- and no line of it may sit
/// deeper than the sibling whose indentation it copied.
/// </summary>
/// <remarks>
/// <para>
/// Reported from kicad-ultra against 0.1.1: appending a row to a library table indented with two
/// spaces re-indented the whole table with tabs, and through <see cref="SDocument"/> pushed every
/// row one tab deeper. 0.1.2 fixed the rows nobody touched. What was left, measured on 0.1.3:
/// <list type="bullet">
/// <item>The new row's first line copied its sibling's two spaces, and every line below it was
/// laid out in tabs at the depth of the tree: <c>"  (lib"</c>, <c>"\t\t(name ..."</c>,
/// <c>"\t)"</c>. The closing paren sat a tab in, deeper than the rows around it.</item>
/// <item>A parsed node moved to another depth or another file -- which is what KiCadSharp's
/// <c>AddSymbol</c> does -- kept the indentation of the place it left. A symbol taken out of a
/// schematic's <c>lib_symbols</c> into a library closed one tab deeper than its new
/// siblings.</item>
/// </list>
/// </para>
/// <para>
/// Assertions are on whole texts. Re-parsing proves nothing about layout, which is the property
/// under test.
/// </para>
/// </remarks>
public class IndentationTests
{
    /// <summary>A library table as KiCad 8 and earlier wrote one: two spaces, no space between a row's fields.</summary>
    private const string TwoSpaceTable =
        "(sym_lib_table\n"
        + "  (version 7)\n"
        + "  (lib (name \"4xxx\")(type \"KiCad\")(uri \"${KICAD7_SYMBOL_DIR}/4xxx.kicad_sym\")(options \"\")(descr \"4xxx series symbols\"))\n"
        + "  (lib (name \"Local\")(type \"KiCad\")(uri \"${KIPRJMOD}/Local.kicad_sym\")(options \"\")(descr \"\"))\n"
        + ")\n";

    /// <summary>The same table as KiCad 9 and 10 write it: tabs, one row per line.</summary>
    private const string TabTable =
        "(sym_lib_table\n"
        + "\t(version 7)\n"
        + "\t(lib (name \"4xxx\") (type \"KiCad\") (uri \"${KICAD10_SYMBOL_DIR}/4xxx.kicad_sym\") (options \"\") (descr \"4xxx series symbols\"))\n"
        + "\t(lib (name \"Local\") (type \"KiCad\") (uri \"${KIPRJMOD}/Local.kicad_sym\") (options \"\") (descr \"\"))\n"
        + ")\n";

    /// <summary>A symbol library in the KiCad 6 and 7 format: two spaces, short forms kept on their parent's line.</summary>
    private const string TwoSpaceLibrary =
        "(kicad_symbol_lib (version 20220914) (generator kicad_symbol_editor)\n"
        + "  (symbol \"R\" (in_bom yes) (on_board yes)\n"
        + "    (property \"Reference\" \"R\" (at 2.032 0 90)\n"
        + "      (effects (font (size 1.27 1.27)))\n"
        + "    )\n"
        + "    (symbol \"R_1_1\"\n"
        + "      (pin passive line (at 0 3.81 270) (length 1.27)\n"
        + "        (name \"~\" (effects (font (size 1.27 1.27))))\n"
        + "        (number \"1\" (effects (font (size 1.27 1.27))))\n"
        + "      )\n"
        + "    )\n"
        + "  )\n"
        + ")\n";

    /// <summary>A symbol library in the KiCad 10 format.</summary>
    private const string TabLibrary =
        "(kicad_symbol_lib\n"
        + "\t(version 20241209)\n"
        + "\t(generator \"kicad_symbol_editor\")\n"
        + "\t(symbol \"R\"\n"
        + "\t\t(in_bom yes)\n"
        + "\t\t(property \"Reference\" \"R\"\n"
        + "\t\t\t(at 2.032 0 90)\n"
        + "\t\t)\n"
        + "\t)\n"
        + ")\n";

    // ------------------------------------------------------------- the issue, exactly as reported

    [Fact]
    public void AppendingARow_ToATwoSpaceTable_ThroughTheDocument_IndentsItWithTwoSpaces()
    {
        var document = SDocument.Parse(TwoSpaceTable);
        document.Root!.AddChild(Row("Imported"));

        Assert.Equal(BeforeClose(TwoSpaceTable, RowText("  ", "  ", "Imported") + "\n"), document.ToText());
    }

    [Fact]
    public void AppendingARow_ToATwoSpaceTable_ThroughTheRootForm_IndentsItWithTwoSpaces()
    {
        var root = SDocument.Parse(TwoSpaceTable).Root!;
        root.AddChild(Row("Imported"));

        // The same text as through the document, less the file's trailing newline.
        Assert.Equal(BeforeClose(TwoSpaceTable, RowText("  ", "  ", "Imported") + "\n").TrimEnd('\n'), root.ToText());
    }

    [Fact]
    public void AppendingARow_ToAKiCad10Table_IndentsItWithTabs()
    {
        var document = SDocument.Parse(TabTable);
        document.Root!.AddChild(Row("Imported"));

        Assert.Equal(BeforeClose(TabTable, RowText("\t", "\t", "Imported") + "\n"), document.ToText());
    }

    // ------------------------------------------------------------ first, last, replaced, removed

    [Fact]
    public void InsertingAtTheFirstPosition_TakesTheIndentationOfTheChildItGoesInFrontOf()
    {
        var document = SDocument.Parse(TwoSpaceTable);
        document.Root!.Children.Insert(0, Row("First"));

        Assert.Equal(
            "(sym_lib_table\n" + RowText("  ", "  ", "First") + "\n" + TwoSpaceTable["(sym_lib_table\n".Length..],
            document.ToText());
    }

    [Fact]
    public void ReplacingTheLastChild_LaysTheNewOneOutFromTheSlotItTook()
    {
        var document = SDocument.Parse(TwoSpaceTable);
        var children = document.Root!.Children;
        children[children.Count - 1] = Row("Replaced");

        var expected = TwoSpaceTable.Replace(
            "  (lib (name \"Local\")(type \"KiCad\")(uri \"${KIPRJMOD}/Local.kicad_sym\")(options \"\")(descr \"\"))",
            RowText("  ", "  ", "Replaced"),
            StringComparison.Ordinal);
        Assert.Equal(expected, document.ToText());
    }

    [Fact]
    public void RemovingTheFirstAndTheLastChild_LeavesEveryOtherLineAsItWas()
    {
        var document = SDocument.Parse(TwoSpaceTable);
        var children = document.Root!.Children;
        children.RemoveAt(children.Count - 1);
        children.RemoveAt(0);

        Assert.Equal(
            "(sym_lib_table\n"
            + "  (lib (name \"4xxx\")(type \"KiCad\")(uri \"${KICAD7_SYMBOL_DIR}/4xxx.kicad_sym\")(options \"\")(descr \"4xxx series symbols\"))\n"
            + ")\n",
            document.ToText());
    }

    // ---------------------------------------------------------------------------------- nested

    [Fact]
    public void AddingANodeTwoLevelsDown_InATwoSpaceLibrary_IndentsEveryLineOfItWithTwoSpaces()
    {
        var document = SDocument.Parse(TwoSpaceLibrary);
        var symbol = document.Root!.GetChild("symbol")!;
        symbol.Children.Insert(symbol.Children.IndexOf(symbol.GetChild("property")!) + 1, Property("MPN", "X-1"));

        var expected = TwoSpaceLibrary.Replace(
            "    (symbol \"R_1_1\"\n",
            "    (property \"MPN\" \"X-1\"\n"
            + "      (at 0 0 0)\n"
            + "      (effects\n"
            + "        (font\n"
            + "          (size 1.27 1.27)\n"
            + "        )\n"
            + "        (hide yes)\n"
            + "      )\n"
            + "    )\n"
            + "    (symbol \"R_1_1\"\n",
            StringComparison.Ordinal);
        Assert.Equal(expected, document.ToText());
    }

    [Fact]
    public void EditsAtSeveralDepthsAtOnce_EachFollowTheLinesAroundThem()
    {
        var document = SDocument.Parse(TwoSpaceLibrary);
        var symbol = document.Root!.GetChild("symbol")!;

        // One level down, at the end; four levels down, at the end of a pin; and one atom-only
        // form beside a font that sits on its parent's line.
        symbol.AddChild(Property("MPN", "X-1"));
        var pin = symbol.GetChild("symbol")!.GetChild("pin")!;
        pin.CreateChild("extra").CreateChild("at", "0", "0");
        symbol.GetChild("property")!.GetChild("effects")!.CreateChild("justify", "left");

        Assert.Equal(
            "(kicad_symbol_lib (version 20220914) (generator kicad_symbol_editor)\n"
            + "  (symbol \"R\" (in_bom yes) (on_board yes)\n"
            + "    (property \"Reference\" \"R\" (at 2.032 0 90)\n"
            + "      (effects (font (size 1.27 1.27)) (justify left))\n"
            + "    )\n"
            + "    (symbol \"R_1_1\"\n"
            + "      (pin passive line (at 0 3.81 270) (length 1.27)\n"
            + "        (name \"~\" (effects (font (size 1.27 1.27))))\n"
            + "        (number \"1\" (effects (font (size 1.27 1.27))))\n"
            + "        (extra\n"
            + "          (at 0 0)\n"
            + "        )\n"
            + "      )\n"
            + "    )\n"
            + "    (property \"MPN\" \"X-1\"\n"
            + "      (at 0 0 0)\n"
            + "      (effects\n"
            + "        (font\n"
            + "          (size 1.27 1.27)\n"
            + "        )\n"
            + "        (hide yes)\n"
            + "      )\n"
            + "    )\n"
            + "  )\n"
            + ")\n",
            document.ToText());
    }

    [Fact]
    public void AddingANode_ToAFileWhoseIndentationDoesNotMatchItsDepth_LinesUpWithTheFile()
    {
        // (inner sits at the margin and its children two tabs in: the depth counter would say one
        // and zero. Every line of the new node is laid out from the sibling it copied.
        const string Shallow = "(root\n(inner\n\t\t(a 1)\n\t)\n)\n";
        var document = SDocument.Parse(Shallow);
        document.Root!.GetChild("inner")!.CreateChild("b").CreateChild("c", "2");

        Assert.Equal("(root\n(inner\n\t\t(a 1)\n\t\t(b\n\t\t\t(c 2)\n\t\t)\n\t)\n)\n", document.ToText());
    }

    [Fact]
    public void AddingANode_ToATwoSpaceFileWhoseIndentationDoesNotMatchItsDepth_LinesUpWithTheFile()
    {
        const string Shallow = "(root\n(inner\n    (a 1)\n  )\n)\n";
        var document = SDocument.Parse(Shallow);
        document.Root!.GetChild("inner")!.CreateChild("b").CreateChild("c", "2");

        Assert.Equal("(root\n(inner\n    (a 1)\n    (b\n      (c 2)\n    )\n  )\n)\n", document.ToText());
    }

    [Fact]
    public void AComment_ThatForcesALineBreak_IndentsTheNextLineLikeTheFile()
    {
        var document = SDocument.Parse("(root\n  (a 1)\n)\n");
        document.Root!.GetChild("a")!.AddComment(" note");

        // The comment runs to the end of its line, so (a's closing paren has to move to the next
        // one -- where the file puts (a itself, not a tab in.
        Assert.Equal("(root\n  (a 1 # note\n  )\n)\n", document.ToText());
    }

    // ------------------------------------------------------------------------------ moved nodes

    [Fact]
    public void MovingASymbol_OutOfASchematicsLibSymbols_IntoALibrary_ReIndentsItOneLevelOut()
    {
        const string Schematic =
            "(kicad_sch\n"
            + "\t(version 20250114)\n"
            + "\t(generator \"eeschema\")\n"
            + "\t(lib_symbols\n"
            + "\t\t(symbol \"Device:C\"\n"
            + "\t\t\t(in_bom yes)\n"
            + "\t\t\t(property \"Reference\" \"C\"\n"
            + "\t\t\t\t(at 0.635 2.54 0)\n"
            + "\t\t\t)\n"
            + "\t\t)\n"
            + "\t)\n"
            + ")\n";
        var symbol = SDocument.Parse(Schematic).Find("kicad_sch/lib_symbols/symbol")!;
        var library = SDocument.Parse(TabLibrary);

        library.Root!.AddChild(symbol);

        Assert.Equal(
            BeforeClose(
                TabLibrary,
                "\t(symbol \"Device:C\"\n"
                + "\t\t(in_bom yes)\n"
                + "\t\t(property \"Reference\" \"C\"\n"
                + "\t\t\t(at 0.635 2.54 0)\n"
                + "\t\t)\n"
                + "\t)\n"),
            library.ToText());
    }

    [Fact]
    public void MovingASymbol_FromATwoSpaceLibrary_IntoATabOne_ReIndentsItsLines_AndNotItsAtoms()
    {
        // The Note value spans two lines, and its second line starts with four spaces. That is the
        // atom's text, not indentation: re-indenting it would change the value.
        const string Old =
            "(kicad_symbol_lib (version 20220914) (generator kicad_symbol_editor)\n"
            + "  (symbol \"N\" (in_bom yes)\n"
            + "    (property \"Note\" \"line one\n    line two\" (at 0 0 0)\n"
            + "      (effects (font (size 1.27 1.27)) hide)\n"
            + "    )\n"
            + "  )\n"
            + ")\n";
        var symbol = SDocument.Parse(Old).Root!.GetChild("symbol")!;
        var library = SDocument.Parse(TabLibrary);

        library.Root!.AddChild(symbol);

        var text = library.ToText();
        Assert.Equal(
            BeforeClose(
                TabLibrary,
                "\t(symbol \"N\" (in_bom yes)\n"
                + "\t\t(property \"Note\" \"line one\n    line two\" (at 0 0 0)\n"
                + "\t\t\t(effects (font (size 1.27 1.27)) hide)\n"
                + "\t\t)\n"
                + "\t)\n"),
            text);
        Assert.Equal("line one\n    line two", SDocument.Parse(text).Root!.GetChildren("symbol").Last().GetChild("property")!.GetValue(1));
    }

    [Fact]
    public void MovingASymbol_BetweenTwoLibrariesOfTheSameStyle_KeepsItsBytes()
    {
        // What KiCadSharp's AddSymbol does most: from one KiCad 10 library into another. Its lines
        // already sit where they belong, so nothing of it may change.
        var source = SDocument.Parse(TabLibrary);
        var library = SDocument.Parse(TabLibrary);

        library.Root!.AddChild(source.Root!.GetChild("symbol")!);

        var symbolText = TabLibrary[TabLibrary.IndexOf("\t(symbol", StringComparison.Ordinal)..^")\n".Length];
        Assert.Equal(BeforeClose(TabLibrary, symbolText), library.ToText());
    }

    [Fact]
    public void MovingAndEditingASymbol_ReIndentsItAndKeepsTheEdit()
    {
        var symbol = SDocument.Parse(TwoSpaceLibrary).Root!.GetChild("symbol")!;
        var library = SDocument.Parse(TabLibrary);

        library.Root!.AddChild(symbol);
        symbol.GetChild("property")!.SetValue(1, "RR", SQuoteStyle.Quoted);
        symbol.GetChild("symbol")!.GetChild("pin")!.CreateChild("extra").CreateChild("at", "0", "0");

        Assert.Equal(
            BeforeClose(
                TabLibrary,
                "\t(symbol \"R\" (in_bom yes) (on_board yes)\n"
                + "\t\t(property \"Reference\" \"RR\" (at 2.032 0 90)\n"
                + "\t\t\t(effects (font (size 1.27 1.27)))\n"
                + "\t\t)\n"
                + "\t\t(symbol \"R_1_1\"\n"
                + "\t\t\t(pin passive line (at 0 3.81 270) (length 1.27)\n"
                + "\t\t\t\t(name \"~\" (effects (font (size 1.27 1.27))))\n"
                + "\t\t\t\t(number \"1\" (effects (font (size 1.27 1.27))))\n"
                + "\t\t\t\t(extra\n"
                + "\t\t\t\t\t(at 0 0)\n"
                + "\t\t\t\t)\n"
                + "\t\t\t)\n"
                + "\t\t)\n"
                + "\t)\n"),
            library.ToText());
    }

    [Fact]
    public void MovingASymbolWhoseFirstLineIsMisindented_LinesItUpByItsClosingParen()
    {
        // A generator that pasted this symbol in wrote its first line one tab too shallow; the rest
        // of it, closer included, is at the depth it really has. The corpus has such schematics.
        const string Pasted =
            "(kicad_sch\n"
            + "\t(lib_symbols\n"
            + "\t(symbol \"X\"\n"
            + "\t\t\t(in_bom yes)\n"
            + "\t\t\t(property \"Reference\" \"U\"\n"
            + "\t\t\t\t(at 0 0 0)\n"
            + "\t\t\t)\n"
            + "\t\t)\n"
            + "\t)\n"
            + ")\n";
        var symbol = SDocument.Parse(Pasted).Find("kicad_sch/lib_symbols/symbol")!;
        var library = SDocument.Parse(TabLibrary);

        library.Root!.AddChild(symbol);

        Assert.Equal(
            BeforeClose(
                TabLibrary,
                "\t(symbol \"X\"\n"
                + "\t\t(in_bom yes)\n"
                + "\t\t(property \"Reference\" \"U\"\n"
                + "\t\t\t(at 0 0 0)\n"
                + "\t\t)\n"
                + "\t)\n"),
            library.ToText());
    }

    [Fact]
    public void AFormBuiltInMemory_HoldingAParsedOne_IsIndentedLikeTheParsedOne()
    {
        var root = new SExpression("root");
        root.AddChild(SExpression.Parse("(a\n  (b 1)\n)"));

        Assert.Equal("(root\n  (a\n    (b 1)\n  )\n)\n", root.ToText());
    }

    // --------------------------------------------------------------------- the Indent option

    [Fact]
    public void TheIndentOption_DoesNotOverrideTheIndentationTheFileAlreadyHas()
    {
        var document = SDocument.Parse(TabTable);
        document.Root!.AddChild(Row("Imported"));

        var text = document.ToText(new SExpressionWriterOptions { Indent = "    " });

        Assert.Equal(BeforeClose(TabTable, RowText("\t", "\t", "Imported") + "\n"), text);
    }

    [Fact]
    public void TheIndentOption_StillLaysOutATreeBuiltInMemory()
    {
        var root = new SExpression("root");
        root.CreateChild("a").CreateChild("b", "1");

        Assert.Equal("(root\n  (a\n    (b 1)\n  )\n)\n", root.ToText(new SExpressionWriterOptions { Indent = "  " }));
    }

    [Fact]
    public void CanonicalFormat_IgnoresTheFilesIndentation_AndUsesTheOption()
    {
        var text = SDocument.Parse(TwoSpaceTable).ToText(new SExpressionWriterOptions { Format = SExpressionFormat.Canonical });

        Assert.StartsWith("(sym_lib_table\n\t(version 7)\n\t(lib\n\t\t(name \"4xxx\")\n", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------- the real corpus

    /// <summary>
    /// Every <c>lib_symbols</c> entry of a real schematic moved into the real library, the way
    /// KiCadSharp's <c>AddSymbol</c> moves them. In this corpus those entries were pasted in by a
    /// generator with their first line one tab too shallow, so this is the hard case, on real
    /// files.
    /// </summary>
    [Fact]
    public void MovingEveryLibSymbolsEntryOfARealSchematic_IntoTheRealLibrary_LinesThemUp_AndKiCadLoadsIt()
    {
        if (Corpus.Missing)
        {
            return;
        }

        const string SchematicPath = "templates/orbion-rs485-bridge/orbion-rs485-bridge.kicad_sch";
        const string LibraryPath = "libs/orbion.kicad_sym";
        var schematic = SDocument.Parse(Corpus.Read(SchematicPath));
        var library = SDocument.Parse(Corpus.Read(LibraryPath));
        library.Root!.RemoveChildren("symbol");

        var moved = schematic.Find("kicad_sch/lib_symbols")!.GetChildren("symbol").ToList();
        Assert.True(moved.Count > 10, "expected a well-populated lib_symbols");
        var names = moved.Select(s => s.GetValue(0)!).ToList();
        var before = moved.Select(s => s.SourceSpan.ToString().Split('\n')).ToList();
        foreach (var symbol in moved)
        {
            library.Root.AddChild(symbol);
        }

        var text = library.ToText();

        // Each symbol keeps every line it had. Its first line now sits one tab in, like every
        // symbol in a library, and every other line -- whose indentation in the schematic was
        // right for depth two -- sits exactly one tab further out than it did.
        var after = SDocument.Parse(text).Root!.GetChildren("symbol").ToList();
        Assert.Equal(names, after.Select(s => s.GetValue(0)!).ToList());
        for (var i = 0; i < after.Count; i++)
        {
            var lines = after[i].SourceSpan.ToString().Split('\n');
            Assert.Equal(before[i].Length, lines.Length);
            Assert.Equal(before[i][0], lines[0]);
            for (var j = 1; j < lines.Length; j++)
            {
                Assert.Equal(before[i][j][1..], lines[j]);
                Assert.StartsWith("\t", lines[j], StringComparison.Ordinal);
            }

            Assert.Equal("\t)", lines[^1]);
        }

        if (Corpus.KiCadCli is null)
        {
            return;
        }

        var staged = Corpus.Stage(LibraryPath, text);
        try
        {
            var upgraded = Path.Combine(staged.Dir, "upgraded.kicad_sym");
            var r = Corpus.RunCli("sym", "upgrade", "--force", "-o", upgraded, staged.File);
            Assert.True(File.Exists(upgraded), $"kicad-cli did not load the library (exit {r.ExitCode}): {r.All}");
            Assert.Equal(names.Count, SDocument.Parse(File.ReadAllText(upgraded)).Root!.GetChildren("symbol").Count());
        }
        finally
        {
            Directory.Delete(staged.Dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------- helpers

    private static SExpression Row(string name)
    {
        var row = new SExpression("lib");
        row.CreateChild("name").AddValue(name, SQuoteStyle.Quoted);
        row.CreateChild("type").AddValue("KiCad", SQuoteStyle.Quoted);
        row.CreateChild("uri").AddValue("${KIPRJMOD}/" + name + ".kicad_sym", SQuoteStyle.Quoted);
        row.CreateChild("options").AddValue(string.Empty, SQuoteStyle.Quoted);
        row.CreateChild("descr").AddValue(string.Empty, SQuoteStyle.Quoted);
        return row;
    }

    /// <summary>A row from <see cref="Row"/> as it must come out at <paramref name="indent"/>, in <paramref name="unit"/>.</summary>
    private static string RowText(string indent, string unit, string name) =>
        $"{indent}(lib\n"
        + $"{indent}{unit}(name \"{name}\")\n"
        + $"{indent}{unit}(type \"KiCad\")\n"
        + $"{indent}{unit}(uri \"${{KIPRJMOD}}/{name}.kicad_sym\")\n"
        + $"{indent}{unit}(options \"\")\n"
        + $"{indent}{unit}(descr \"\")\n"
        + $"{indent})";

    private static SExpression Property(string key, string value)
    {
        var property = new SExpression("property");
        property.AddValue(key, SQuoteStyle.Quoted);
        property.AddValue(value, SQuoteStyle.Quoted);
        property.CreateChild("at", "0", "0", "0");
        var effects = property.CreateChild("effects");
        effects.CreateChild("font").CreateChild("size", "1.27", "1.27");
        effects.CreateChild("hide", "yes");
        return property;
    }

    /// <summary><paramref name="text"/> with <paramref name="insertion"/> in front of its root's closing <c>")\n"</c>.</summary>
    private static string BeforeClose(string text, string insertion)
    {
        Assert.EndsWith("\n)\n", text, StringComparison.Ordinal);
        return text[..^2] + insertion + ")\n";
    }
}
