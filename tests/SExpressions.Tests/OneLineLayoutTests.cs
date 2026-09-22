namespace SExpressions.Tests;

/// <summary>
/// #36: the layout of a form built in memory and added to a parsed file whose siblings are written
/// on one line each, as KiCad writes every row of a library table.
/// </summary>
/// <remarks>
/// <para>
/// MEASURED on a9111fd: appending a row to the global <c>sym-lib-table</c> of a KiCad 10 user
/// configuration, whose one row is
/// <c>"\t(lib (name \"KiCad\") (type \"Table\") (uri ...) (options \"\") (descr ...))"</c>,
/// laid the new row out one child per line: <c>"\t(lib\n\t\t(name \"Imported\")\n ... \n\t)"</c>.
/// The indentation was right (#24); the shape was the canonical formatter's, not the file's.
/// </para>
/// <para>
/// Now a new form is laid out like its nearest sibling with the same token that is still where the
/// parser put it. When that sibling stands on one line, so does the new form, with the sibling's
/// separators between its items -- a space in a KiCad 10 table, nothing at all in one written by
/// KiCad 7 or 8 -- and single spaces inside any form nested in it, which is how KiCad writes a
/// form on one line at every version. When that sibling spans lines, or there is none, the layout
/// is what it was: one child per line, in the file's unit. Assertions are on whole texts.
/// </para>
/// </remarks>
public class OneLineLayoutTests
{
    /// <summary>The global sym-lib-table of a KiCad 10 user configuration, as KiCad 10.0.6 writes it.</summary>
    private const string KiCad10Table =
        "(sym_lib_table\n"
        + "\t(version 7)\n"
        + "\t(lib (name \"KiCad\") (type \"Table\") (uri \"${KICAD10_TEMPLATE_DIR}/sym-lib-table\") (options \"\") (descr \"KiCad Default Libraries\"))\n"
        + ")\n";

    /// <summary>A library table as KiCad 8 and earlier wrote one: two spaces, no space between a row's fields.</summary>
    private const string KiCad7Table =
        "(sym_lib_table\n"
        + "  (version 7)\n"
        + "  (lib (name \"4xxx\")(type \"KiCad\")(uri \"${KICAD7_SYMBOL_DIR}/4xxx.kicad_sym\")(options \"\")(descr \"4xxx series symbols\"))\n"
        + "  (lib (name \"Local\")(type \"KiCad\")(uri \"${KIPRJMOD}/Local.kicad_sym\")(options \"\")(descr \"\"))\n"
        + ")\n";

    private const string KiCad10Row = "\t(lib (name \"Imported\") (type \"KiCad\") (uri \"${KIPRJMOD}/Imported.kicad_sym\") (options \"\") (descr \"\"))";

    private const string KiCad7Row = "  (lib (name \"Imported\")(type \"KiCad\")(uri \"${KIPRJMOD}/Imported.kicad_sym\")(options \"\")(descr \"\"))";

    // ------------------------------------------------------------- the issue, exactly as reported

    [Fact]
    public void AppendingARow_ToAKiCad10Table_WritesItOnOneLine_LikeTheRowAboveIt()
    {
        var document = SDocument.Parse(KiCad10Table);
        document.Root!.AddChild(Row("Imported"));

        Assert.Equal(BeforeClose(KiCad10Table, KiCad10Row + "\n"), document.ToText());
    }

    [Fact]
    public void AppendingARow_ToAKiCad7Table_WritesItOnOneLine_WithNothingBetweenItsFields()
    {
        var document = SDocument.Parse(KiCad7Table);
        document.Root!.AddChild(Row("Imported"));

        Assert.Equal(BeforeClose(KiCad7Table, KiCad7Row + "\n"), document.ToText());
    }

    [Fact]
    public void AppendingARow_ThroughTheRootForm_WritesItOnOneLine()
    {
        var root = SDocument.Parse(KiCad10Table).Root!;
        root.AddChild(Row("Imported"));

        Assert.Equal(BeforeClose(KiCad10Table, KiCad10Row + "\n").TrimEnd('\n'), root.ToText());
    }

    [Fact]
    public void TheNewRow_ReadsBackAsTheRowThatWasAdded()
    {
        var document = SDocument.Parse(KiCad7Table);
        document.Root!.AddChild(Row("Imported"));

        var rows = SDocument.Parse(document.ToText()).Root!.GetChildren("lib").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Imported", rows[2].GetChildValue("name"));
        Assert.Equal("${KIPRJMOD}/Imported.kicad_sym", rows[2].GetChildValue("uri"));
        Assert.Equal(string.Empty, rows[2].GetChildValue("descr"));
    }

    // ---------------------------------------------------------- which sibling is the template

    [Fact]
    public void InsertingARowFirst_LaysItOutLikeTheRowItGoesInFrontOf()
    {
        var document = SDocument.Parse(KiCad7Table);
        document.Root!.Children.Insert(0, Row("Imported"));

        Assert.Equal("(sym_lib_table\n" + KiCad7Row + "\n" + KiCad7Table["(sym_lib_table\n".Length..], document.ToText());
    }

    [Fact]
    public void ReplacingARow_LaysTheNewOneOutLikeTheRowsAroundIt()
    {
        var document = SDocument.Parse(KiCad7Table);
        var children = document.Root!.Children;
        children[children.Count - 1] = Row("Imported");

        var expected = KiCad7Table.Replace(
            "  (lib (name \"Local\")(type \"KiCad\")(uri \"${KIPRJMOD}/Local.kicad_sym\")(options \"\")(descr \"\"))",
            KiCad7Row,
            StringComparison.Ordinal);
        Assert.Equal(expected, document.ToText());
    }

    [Fact]
    public void ARowBesideAnEditedRow_IsStillLaidOutLikeIt()
    {
        // Editing a row's uri marks the row and the table modified. The row is still where the
        // parser put it, on one line, and that is what counts.
        var document = SDocument.Parse(KiCad10Table);
        document.Root!.GetChild("lib")!.GetChild("uri")!.SetValue(0, "${KICAD10_TEMPLATE_DIR}/other", SQuoteStyle.Quoted);
        document.Root.AddChild(Row("Imported"));

        Assert.Equal(
            BeforeClose(KiCad10Table.Replace("${KICAD10_TEMPLATE_DIR}/sym-lib-table", "${KICAD10_TEMPLATE_DIR}/other", StringComparison.Ordinal), KiCad10Row + "\n"),
            document.ToText());
    }

    [Fact]
    public void AFormWithNoSiblingOfItsToken_IsStillLaidOutOneChildPerLine_InTheFilesUnit()
    {
        // The #24 layout, unchanged: nothing in the file says how a (lib is shaped.
        var document = SDocument.Parse("(sym_lib_table\n  (version 7)\n)\n");
        document.Root!.AddChild(Row("Imported"));

        Assert.Equal(
            "(sym_lib_table\n"
            + "  (version 7)\n"
            + "  (lib\n"
            + "    (name \"Imported\")\n"
            + "    (type \"KiCad\")\n"
            + "    (uri \"${KIPRJMOD}/Imported.kicad_sym\")\n"
            + "    (options \"\")\n"
            + "    (descr \"\")\n"
            + "  )\n"
            + ")\n",
            document.ToText());
    }

    [Fact]
    public void AFormWhoseSiblingSpansLines_IsLaidOutOneChildPerLine()
    {
        // What KiCad 10 does to nearly every form in a schematic.
        var document = SDocument.Parse("(root\n\t(item\n\t\t(x 1)\n\t)\n)\n");
        document.Root!.CreateChild("item").CreateChild("x", "2");

        Assert.Equal("(root\n\t(item\n\t\t(x 1)\n\t)\n\t(item\n\t\t(x 2)\n\t)\n)\n", document.ToText());
    }

    [Fact]
    public void TheNearestSiblingOfTheSameToken_Decides()
    {
        // The following sibling first, because that is the slot the new form takes; then the one
        // behind it. Here the row in front spans lines and the one behind is on one line.
        var document = SDocument.Parse("(root\n\t(item (x 1))\n\t(item\n\t\t(x 2)\n\t)\n)\n");
        var root = document.Root!;
        var last = new SExpression("item");
        last.CreateChild("x", "3");
        root.AddChild(last);
        var first = new SExpression("item");
        first.CreateChild("x", "0");
        root.Children.Insert(0, first);

        Assert.Equal("(root\n\t(item (x 0))\n\t(item (x 1))\n\t(item\n\t\t(x 2)\n\t)\n\t(item\n\t\t(x 3)\n\t)\n)\n", document.ToText());
    }

    [Fact]
    public void ASiblingWithAnotherToken_IsNotATemplate()
    {
        var document = SDocument.Parse("(root\n\t(other (x 1))\n)\n");
        document.Root!.CreateChild("item").CreateChild("x", "2");

        Assert.Equal("(root\n\t(other (x 1))\n\t(item\n\t\t(x 2)\n\t)\n)\n", document.ToText());
    }

    [Fact]
    public void ANewSibling_IsNotATemplate_ForTheNextNewForm()
    {
        // Two rows appended to a table with none: neither is where a parser put it, so the
        // second is laid out like the first only in that both get the #24 layout.
        var document = SDocument.Parse("(root\n\t(version 7)\n)\n");
        document.Root!.CreateChild("item").CreateChild("x", "1");
        document.Root.CreateChild("item").CreateChild("x", "2");

        Assert.Equal("(root\n\t(version 7)\n\t(item\n\t\t(x 1)\n\t)\n\t(item\n\t\t(x 2)\n\t)\n)\n", document.ToText());
    }

    // ------------------------------------------------------------------- the shape of the row

    [Fact]
    public void AFormWithAtomsAndNestedForms_TakesTheSiblingsSeparators_AndSingleSpacesInside()
    {
        // A KiCad 7 symbol whose property sits on one line. The new property's own items follow
        // the sibling: a space each. Inside (effects, which the sibling has nothing to say about,
        // single spaces.
        const string Library =
            "(kicad_symbol_lib (version 20220914)\n"
            + "  (symbol \"R\"\n"
            + "    (property \"Reference\" \"R\" (at 2.032 0 90))\n"
            + "  )\n"
            + ")\n";
        var document = SDocument.Parse(Library);
        var property = new SExpression("property");
        property.AddValue("MPN", SQuoteStyle.Quoted);
        property.AddValue("X-1", SQuoteStyle.Quoted);
        property.CreateChild("at", "0", "0", "0");
        property.CreateChild("effects").CreateChild("font").CreateChild("size", "1.27", "1.27");
        document.Root!.GetChild("symbol")!.AddChild(property);

        Assert.Equal(
            "(kicad_symbol_lib (version 20220914)\n"
            + "  (symbol \"R\"\n"
            + "    (property \"Reference\" \"R\" (at 2.032 0 90))\n"
            + "    (property \"MPN\" \"X-1\" (at 0 0 0) (effects (font (size 1.27 1.27))))\n"
            + "  )\n"
            + ")\n",
            document.ToText());
    }

    [Fact]
    public void AFormWithMoreItemsThanItsSibling_RepeatsTheSiblingsLastSeparator()
    {
        var document = SDocument.Parse("(root\n\t(item (a 1)(b 2))\n)\n");
        var item = document.Root!.CreateChild("item");
        item.CreateChild("a", "3");
        item.CreateChild("b", "4");
        item.CreateChild("c", "5");

        Assert.Equal("(root\n\t(item (a 1)(b 2))\n\t(item (a 3)(b 4)(c 5))\n)\n", document.ToText());
    }

    [Fact]
    public void TwoAtoms_NeverFuse_WhateverTheSiblingHasBetweenItsItems()
    {
        // The sibling writes nothing between its forms. Two atoms with nothing between them would
        // read back as one, so an atom always gets at least a space.
        var document = SDocument.Parse("(root\n\t(item (a 1)(b 2))\n)\n");
        document.Root!.CreateChild("item", "x", "y");

        Assert.Equal("(root\n\t(item (a 1)(b 2))\n\t(item x y)\n)\n", document.ToText());
    }

    [Fact]
    public void AFormHoldingAComment_CannotStandOnOneLine_AndIsLaidOutOneChildPerLine()
    {
        var document = SDocument.Parse(KiCad10Table);
        var row = Row("Imported");
        row.AddComment(" note");
        document.Root!.AddChild(row);

        Assert.Equal(
            BeforeClose(
                KiCad10Table,
                "\t(lib\n"
                + "\t\t(name \"Imported\")\n"
                + "\t\t(type \"KiCad\")\n"
                + "\t\t(uri \"${KIPRJMOD}/Imported.kicad_sym\")\n"
                + "\t\t(options \"\")\n"
                + "\t\t(descr \"\")\n"
                + "\t\t# note\n"
                + "\t)\n"),
            document.ToText());
    }

    [Fact]
    public void AFormHoldingAParsedOneLineForm_CopiesThatFormsBytes()
    {
        var document = SDocument.Parse(KiCad7Table);
        var row = new SExpression("lib");
        row.AddChild(document.Root!.GetChild("lib")!.GetChild("name")!.Clone());
        row.CreateChild("type").AddValue("KiCad", SQuoteStyle.Quoted);
        document.Root.AddChild(row);

        Assert.Equal(BeforeClose(KiCad7Table, "  (lib (name \"4xxx\")(type \"KiCad\"))\n"), document.ToText());
    }

    [Fact]
    public void AFormHoldingAParsedMultiLineForm_CannotStandOnOneLine_AndIsLaidOutOneChildPerLine()
    {
        var document = SDocument.Parse("(root\n\t(item (a 1))\n\t(big\n\t\t(b 2)\n\t)\n)\n");
        var item = new SExpression("item");
        item.AddChild(document.Root!.GetChild("big")!.Clone());
        document.Root.AddChild(item);

        Assert.Equal("(root\n\t(item (a 1))\n\t(big\n\t\t(b 2)\n\t)\n\t(item\n\t\t(big\n\t\t\t(b 2)\n\t\t)\n\t)\n)\n", document.ToText());
    }

    // ------------------------------------------------------------------------ the top level

    [Fact]
    public void AppendingATopLevelForm_BesideOneLineOnes_WritesItOnOneLine()
    {
        const string Rules = "(version 1)\n(rule \"a\" (constraint clearance (min 0.2mm)))\n";
        var document = SDocument.Parse(Rules);
        var rule = new SExpression("rule");
        rule.AddValue("b", SQuoteStyle.Quoted);
        rule.CreateChild("constraint", "clearance").CreateChild("min", "0.3mm");
        document.Add(rule);

        Assert.Equal(Rules + "(rule \"b\" (constraint clearance (min 0.3mm)))\n", document.ToText());
    }

    // ------------------------------------------------------------------------- the real corpus

    /// <summary>
    /// The project sym-lib-table of a real KiCad 10 template. Its one row is indented with a tab
    /// and has nothing between its fields -- KiCad 10 kept the shape it inherited from the KiCad 7
    /// file the template was made from -- so it is the case neither synthetic table above covers.
    /// </summary>
    [Fact]
    public void AppendingARow_ToARealProjectTable_WritesItLikeTheRowAboveIt_AndChangesNothingElse()
    {
        if (Corpus.Missing)
        {
            return;
        }

        var original = Corpus.Read("templates/orbion-4layer/sym-lib-table");
        Assert.Contains("\n\t(lib (name \"orbion\")(type \"KiCad\")", original, StringComparison.Ordinal);
        var document = SDocument.Parse(original);
        document.Root!.AddChild(Row("Imported"));

        Assert.Equal(BeforeClose(original, "\t(lib (name \"Imported\")(type \"KiCad\")(uri \"${KIPRJMOD}/Imported.kicad_sym\")(options \"\")(descr \"\"))\n"), document.ToText());
    }

    // ------------------------------------------------------------------ what does not change

    [Fact]
    public void CanonicalFormat_StillLaysEveryFormOutOneChildPerLine()
    {
        var document = SDocument.Parse(KiCad10Table);
        document.Root!.AddChild(Row("Imported"));

        var text = document.ToText(new SExpressionWriterOptions { Format = SExpressionFormat.Canonical });

        Assert.EndsWith("\t(lib\n\t\t(name \"Imported\")\n\t\t(type \"KiCad\")\n\t\t(uri \"${KIPRJMOD}/Imported.kicad_sym\")\n\t\t(options \"\")\n\t\t(descr \"\")\n\t)\n)\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATreeBuiltInMemory_IsStillLaidOutOneChildPerLine()
    {
        var root = new SExpression("root");
        root.CreateChild("item").CreateChild("x", "1");
        root.CreateChild("item").CreateChild("x", "2");

        Assert.Equal("(root\n\t(item\n\t\t(x 1)\n\t)\n\t(item\n\t\t(x 2)\n\t)\n)\n", root.ToText());
    }

    [Fact]
    public void AnUntouchedTable_StillComesBackByteForByte()
    {
        Assert.Equal(KiCad10Table, SDocument.Parse(KiCad10Table).ToText());
        Assert.Equal(KiCad7Table, SDocument.Parse(KiCad7Table).ToText());
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

    /// <summary><paramref name="text"/> with <paramref name="insertion"/> in front of its root's closing <c>")\n"</c>.</summary>
    private static string BeforeClose(string text, string insertion)
    {
        Assert.EndsWith("\n)\n", text, StringComparison.Ordinal);
        return text[..^2] + insertion + ")\n";
    }
}
