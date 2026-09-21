using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SExpressions
{
    /// <summary>
    /// How <see cref="SExpressionWriter"/> lays text out.
    /// </summary>
    public enum SExpressionFormat
    {
        /// <summary>
        /// Reproduce the original text wherever it is still known, and format only what changed.
        /// This is the default and the only mode that can be byte-identical.
        /// </summary>
        Auto = 0,

        /// <summary>Same as <see cref="Auto"/>; spelled out for callers who want to be explicit.</summary>
        Preserve = 1,

        /// <summary>Always re-format from the tree, ignoring where it came from.</summary>
        Canonical = 2,
    }

    /// <summary>
    /// Knobs for <see cref="SExpressionWriter"/>. They only affect text the writer has to format
    /// itself; text it can reproduce from the source is reproduced exactly.
    /// </summary>
    public sealed class SExpressionWriterOptions
    {
        /// <summary>How much of the original layout to keep. Defaults to <see cref="SExpressionFormat.Auto"/>.</summary>
        public SExpressionFormat Format { get; set; } = SExpressionFormat.Auto;

        /// <summary>
        /// One level of indentation. Defaults to a tab, which is what KiCad writes.
        /// </summary>
        /// <remarks>
        /// <see cref="SExpressionFormat.Canonical"/> always uses it. The format-preserving modes use
        /// it only when the text being written shows no indentation of its own to follow: a node
        /// added to a parsed file is indented the way that file already is, two spaces or a tab.
        /// </remarks>
        public string Indent { get; set; } = "\t";

        /// <summary>The line separator. Defaults to <c>\n</c>, which is what KiCad writes on every platform.</summary>
        public string NewLine { get; set; } = "\n";

        /// <summary>The character used to introduce a comment.</summary>
        public char CommentPrefix { get; set; } = '#';
    }

    /// <summary>
    /// Writer for S-expression text.
    /// </summary>
    /// <remarks>
    /// A tree that came from a parser and has not been touched is written back byte for byte: the
    /// writer copies the source span instead of re-formatting. Edit one atom and only that atom's
    /// text changes -- the whitespace around it, its siblings and every untouched subtree are still
    /// copied verbatim. A tree built in memory has no source, so it is formatted from scratch.
    /// </remarks>
    public sealed class SExpressionWriter
    {
        /// <summary>Characters that stop an atom being written bare.</summary>
        private static readonly SearchValues<char> MustQuote = SearchValues.Create(" \t\r\n\f\v()\"\\");

        private static readonly SearchValues<char> MustEscape = SearchValues.Create("\"\\");

        private readonly SExpressionWriterOptions _options;

        /// <summary>Creates a writer with default options.</summary>
        public SExpressionWriter()
            : this(null)
        {
        }

        /// <summary>Creates a writer with explicit options.</summary>
        /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
        public SExpressionWriter(SExpressionWriterOptions? options) => _options = options ?? new SExpressionWriterOptions();

        /// <summary>Gets the options in force.</summary>
        public SExpressionWriterOptions Options => _options;

        /// <summary>
        /// Convert an S-expression to a string.
        /// </summary>
        /// <param name="expression">The expression to convert.</param>
        /// <param name="indentLevel">The starting indentation level (0 by default).</param>
        /// <returns>The formatted S-expression string.</returns>
        public string Write(SExpression expression, int indentLevel = 0)
        {
            ArgumentNullException.ThrowIfNull(expression);
            var sb = new StringBuilder(Capacity(expression));
            var context = new WriteContext(sb, LayoutOf(expression), _options.Indent);
            AppendIndent(sb, indentLevel);

            // A parsed form is laid out from the line it stands on in its own text, so that the lines
            // it copies from that text and the lines it has to format agree with each other.
            var indent = UsesSource(expression)
                ? Indent.Copy(expression.Source!, LineIndentOf(expression.Source!, expression.SourceStart))
                : Indent.OfLevels(indentLevel);
            var verbatim = AppendNode(context, expression, indent, Rebase.Identity, leadingIndent: false);
            if (!verbatim)
            {
                AppendNewLine(sb);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Convert a whole document -- every top-level form, with the comments and whitespace between
        /// them -- to a string. An unmodified parsed document comes back byte for byte.
        /// </summary>
        /// <param name="document">The document to convert.</param>
        /// <returns>The file text.</returns>
        public string Write(SDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            var container = document.Container;
            if (CanCopyVerbatim(container))
            {
                return container.Source!.Substring(container.SourceStart, container.SourceLength);
            }

            var sb = new StringBuilder(Capacity(container));
            var context = new WriteContext(sb, LayoutOf(container), _options.Indent);

            // The container is a synthetic node holding the top-level forms, not a form itself, so it
            // occupies no indent level: its children sit at the margin. Splicing it one level in put
            // everything the writer had to re-format one indent deeper than the text around it --
            // invisible until a caller adds a node, and then wrong on every line of it.
            if (!(CanSplice(container) && TrySplice(context, container, Indent.Margin, Rebase.Identity)))
            {
                sb.Length = 0;
                AppendDocumentCanonical(context, container);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Write an S-expression to a file, always leaving a trailing newline.
        /// </summary>
        /// <param name="expression">The expression to write.</param>
        /// <param name="filePath">The path of the file to write to.</param>
        public void WriteToFile(SExpression expression, string filePath)
        {
            var content = Write(expression);
            if (!content.EndsWith('\n'))
            {
                content += _options.NewLine;
            }

            File.WriteAllText(filePath, content);
        }

        /// <summary>Write a document to a file. Re-emits a leading UTF-8 BOM when <see cref="SDocument.HasByteOrderMark"/> is set.</summary>
        /// <param name="document">The document to write.</param>
        /// <param name="filePath">The path of the file to write to.</param>
        public void WriteToFile(SDocument document, string filePath)
        {
            ArgumentNullException.ThrowIfNull(document);
            var content = Write(document);
            if (document.HasByteOrderMark)
            {
                File.WriteAllText(filePath, content, SDocument.Utf8WithBom);
            }
            else
            {
                File.WriteAllText(filePath, content);
            }
        }

        /// <summary>Write a document to a file asynchronously. Re-emits a leading UTF-8 BOM when <see cref="SDocument.HasByteOrderMark"/> is set.</summary>
        /// <param name="document">The document to write.</param>
        /// <param name="filePath">The path of the file to write to.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>A task that completes when the file is written.</returns>
        public Task WriteToFileAsync(SDocument document, string filePath, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);
            var content = Write(document);
            return document.HasByteOrderMark
                ? File.WriteAllTextAsync(filePath, content, SDocument.Utf8WithBom, cancellationToken)
                : File.WriteAllTextAsync(filePath, content, cancellationToken);
        }

        /// <summary>Writes an expression to a <see cref="TextWriter"/>.</summary>
        /// <param name="expression">The expression to write.</param>
        /// <param name="writer">The destination.</param>
        public void WriteTo(SExpression expression, TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.Write(Write(expression));
        }

        /// <summary>Writes a document to a <see cref="TextWriter"/>.</summary>
        /// <param name="document">The document to write.</param>
        /// <param name="writer">The destination.</param>
        public void WriteTo(SDocument document, TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.Write(Write(document));
        }

        // ----------------------------------------------------------------------------- rendering

        /// <summary>Appends one node. Returns true when its text came out of the source rather than the formatter.</summary>
        /// <param name="context">The write in progress.</param>
        /// <param name="e">The node.</param>
        /// <param name="indent">Where the line this node stands on starts, in the output.</param>
        /// <param name="rebase">How whitespace copied out of the node's source maps onto where it now stands.</param>
        /// <param name="leadingIndent">Whether to write <paramref name="indent"/> in front of the node.</param>
        private bool AppendNode(WriteContext context, SExpression e, Indent indent, Rebase rebase, bool leadingIndent)
        {
            var sb = context.Builder;
            if (leadingIndent)
            {
                indent.AppendTo(context);
            }

            if (CanCopyVerbatim(e) && rebase.IsIdentity)
            {
                sb.Append(e.Source, e.SourceStart, e.SourceLength);
                return true;
            }

            // An untouched node that now stands somewhere other than where it was parsed -- moved in
            // from another file, or to another depth of this one -- has the right bytes but the
            // indentation of the place it came from, so it is walked like an edited one and every
            // line of it is re-based onto where it is now.
            if (CanSplice(e) || CanCopyVerbatim(e))
            {
                var mark = sb.Length;
                if (TrySplice(context, e, indent, rebase))
                {
                    return true;
                }

                sb.Length = mark;
            }

            AppendCanonical(context, e, indent);
            return false;
        }

        private bool CanCopyVerbatim(SExpression e) =>
            _options.Format != SExpressionFormat.Canonical && e.HasSource && !e.IsModified;

        private bool CanSplice(SExpression e) =>
            _options.Format != SExpressionFormat.Canonical && e.HasSource && !e.HeaderInvalid
            && (e.ItemCount > 0 || e.ItemsChanged);

        /// <summary>True when the writer takes this node's layout from its source rather than formatting it.</summary>
        private bool UsesSource(SExpression e) => _options.Format != SExpressionFormat.Canonical && e.HasSource;

        /// <summary>The tree whose text decides the indentation unit, or null when the options alone do.</summary>
        private SExpression? LayoutOf(SExpression root) => _options.Format == SExpressionFormat.Canonical ? null : root;

        /// <summary>
        /// Rebuilds a changed form out of the source it came from. Each item that still holds its
        /// slot is written behind the whitespace that preceded it in the file, so the only bytes
        /// that move are the ones that were edited. An item with no slot -- one that was just
        /// inserted -- gets a separator synthesised from the ones its neighbours already use, and an
        /// item that was removed takes its own separator with it. Nothing else in the form is
        /// touched: a sibling nobody edited keeps its bytes, its indentation and its line, and the
        /// closing paren keeps its column.
        /// </summary>
        /// <remarks>
        /// Every item is laid out from the line it actually starts on, read off the whitespace in
        /// front of it, and not from how deep it is in the tree: KiCad's own generators write files
        /// whose indentation does not match their nesting, and files indented with spaces. A new
        /// node's first line copies a sibling's indentation, so every line below it has to start
        /// from that same text, in that file's unit, or it lands deeper than the sibling it copied.
        /// </remarks>
        private bool TrySplice(WriteContext context, SExpression e, Indent indent, Rebase rebase)
        {
            var sb = context.Builder;
            var src = e.Source!;
            var start = e.SourceStart;
            var end = start + e.SourceLength;
            var items = e.ItemsSpan;

            // The document container is synthetic: it holds the top-level forms but is not a form
            // itself, so it has neither a "(token" to open it nor a ")" to close it.
            var container = e.IsDocumentContainer;
            var bodyStart = container ? start : HeaderEnd(src, start, end);
            var bodyEnd = container ? end : end - 1;
            if (bodyEnd < bodyStart || (!container && src[bodyEnd] != ')'))
            {
                return false;
            }

            // Every item that still claims a slot has to sit inside the body, in order. One that
            // does not means the tree no longer describes this text, and only a re-format is right.
            var cursor = bodyStart;
            foreach (var item in items)
            {
                if (!item.IsFromSource)
                {
                    continue;
                }

                if (item.SourceStart < cursor || item.SourceStart + item.SourceLength > bodyEnd)
                {
                    return false;
                }

                cursor = item.SourceStart + item.SourceLength;
            }

            sb.Append(src, start, bodyStart - start);

            // Where an item that does not start a line of its own is laid out from: one level in from
            // this form -- or, for the container, which is not a form, the margin itself.
            var inline = container ? indent : indent.Deeper;

            cursor = bodyStart;
            var previousWasComment = false;
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                Indent itemIndent;
                if (item.IsFromSource)
                {
                    // Only the whitespace immediately in front of the item is its separator.
                    // Anything before that is the text of items that have since been removed, and
                    // copying it would put them back.
                    itemIndent = AppendSourceSeparator(context, src, SeparatorStart(src, cursor, item.SourceStart), item.SourceStart, previousWasComment, inline, rebase);
                }
                else
                {
                    itemIndent = AppendSynthesizedSeparator(context, src, items, i, bodyStart, bodyEnd, previousWasComment, inline, rebase);
                }

                if (item.IsFromSource)
                {
                    cursor = item.SourceStart + item.SourceLength;
                }

                if (item.Kind == SItemKind.Expression)
                {
                    var child = item.Expression!;
                    var childRebase = IsInOriginalSlot(item, child, src) ? rebase : RebaseOnto(context, child, itemIndent);
                    AppendNode(context, child, itemIndent, childRebase, leadingIndent: false);
                }
                else if (item.RawValid)
                {
                    sb.Append(src, item.SourceStart, item.SourceLength);
                }
                else
                {
                    AppendLeaf(item, sb);
                }

                previousWasComment = item.Kind == SItemKind.Comment;
            }

            AppendSourceSeparator(context, src, SeparatorStart(src, cursor, bodyEnd), bodyEnd, previousWasComment, indent, rebase);
            if (!container)
            {
                sb.Append(')');
            }

            return true;
        }

        /// <summary>
        /// True when a child form is still the one the parser put in this slot of this text. Its
        /// whitespace then already belongs where it stands; any other child -- inserted, moved in,
        /// or put in the slot of the one it replaced -- has to be re-based onto it.
        /// </summary>
        private static bool IsInOriginalSlot(in SItem item, SExpression child, string src) =>
            item.RawValid && ReferenceEquals(child.Source, src) && child.SourceStart == item.SourceStart;

        /// <summary>
        /// Copies one separator out of the source, unaltered unless a comment forces a line break or
        /// the node is being re-based. Returns where the line the next item stands on starts.
        /// </summary>
        private Indent AppendSourceSeparator(WriteContext context, string src, int from, int to, bool previousWasComment, Indent inline, Rebase rebase)
        {
            var lineBreak = src.AsSpan(from, to - from).LastIndexOf('\n');
            if (lineBreak >= 0)
            {
                return AppendLineBreak(context, src, from, from + lineBreak + 1, to, rebase);
            }

            // A comment runs to the end of its line, so whatever follows one has to start on the
            // next line or it is swallowed into the comment text on re-parse. Only a newly inserted
            // comment can put a sibling in that position; a parsed one already has its line break.
            if (previousWasComment)
            {
                AppendNewLine(context.Builder);
                inline.AppendTo(context);
                return inline;
            }

            context.Builder.Append(src, from, to - from);
            return inline;
        }

        /// <summary>
        /// Writes a separator that breaks the line: everything up to and including its last line
        /// break exactly as the source has it, then the new line's indentation -- copied as it is,
        /// or mapped through <paramref name="rebase"/>. Returns where that line starts.
        /// </summary>
        private static Indent AppendLineBreak(WriteContext context, string src, int from, int lineStart, int to, Rebase rebase)
        {
            context.Builder.Append(src, from, lineStart - from);
            if (rebase.IsIdentity)
            {
                context.Builder.Append(src, lineStart, to - lineStart);
                return Indent.Copy(src, (lineStart, to - lineStart));
            }

            var indent = rebase.Map(context, src.AsSpan(lineStart, to - lineStart));
            indent.AppendTo(context);
            return indent;
        }

        /// <summary>
        /// Writes the whitespace a newly inserted item needs, taken from the separator its
        /// neighbours already use: the one in front of the sibling that will follow it, or -- when
        /// it is being appended -- the one in front of the sibling it follows. Returns where the line
        /// the item stands on starts, so a multi-line new item lines up with the file it is joining
        /// rather than with the writer's own idea of the depth.
        /// </summary>
        private Indent AppendSynthesizedSeparator(
            WriteContext context,
            string src,
            ReadOnlySpan<SItem> items,
            int index,
            int bodyStart,
            int bodyEnd,
            bool previousWasComment,
            Indent inline,
            Rebase rebase)
        {
            var sb = context.Builder;
            var found = TryNeighbourSeparator(src, items, index, bodyStart, out var from, out var to);
            var lineBreak = found ? src.AsSpan(from, to - from).LastIndexOf('\n') : -1;

            // A comment cannot be followed on its own line; see AppendSourceSeparator.
            var needsLine = previousWasComment && lineBreak < 0;

            // A form with nothing in it yet has no separator to copy, so the only hint left is the
            // shape of the form itself: one already broken over lines stays broken, "(foo)" does not.
            if (!found && !needsLine && items[index].Kind != SItemKind.Atom)
            {
                needsLine = src.AsSpan(bodyStart, bodyEnd - bodyStart).IndexOf('\n') >= 0;
            }

            if (needsLine)
            {
                AppendNewLine(sb);
                inline.AppendTo(context);
                return inline;
            }

            if (!found || from == to)
            {
                // Two items with nothing between them fuse into one atom on re-parse. This is the
                // one byte an insert is always allowed: the separator that makes it an item at all.
                sb.Append(' ');
                return inline;
            }

            if (lineBreak < 0)
            {
                sb.Append(src, from, to - from);
                return inline;
            }

            return AppendLineBreak(context, src, from, from + lineBreak + 1, to, rebase);
        }

        /// <summary>
        /// Finds the separator a new item should copy. A sibling of the same kind is preferred --
        /// atoms hug their token and each other, child forms get their own line -- and the sibling
        /// that will FOLLOW the new item is preferred over the one behind it, because that is the
        /// slot the new item is taking and the sibling then keeps its own bytes unchanged.
        /// </summary>
        private static bool TryNeighbourSeparator(string src, ReadOnlySpan<SItem> items, int index, int bodyStart, out int from, out int to)
        {
            var kind = items[index].Kind;
            if (TryNeighbourSeparator(src, items, index, bodyStart, kind, out from, out to))
            {
                return true;
            }

            // An atom with no atom sibling belongs next to whatever it follows, on that line: that
            // is how "(token value ...)" is written wherever this format is used. Falling through to
            // a child form's separator here would push it onto a line of its own.
            return kind != SItemKind.Atom
                && TryNeighbourSeparator(src, items, index, bodyStart, null, out from, out to);
        }

        private static bool TryNeighbourSeparator(string src, ReadOnlySpan<SItem> items, int index, int bodyStart, SItemKind? kind, out int from, out int to)
        {
            for (var i = index + 1; i < items.Length; i++)
            {
                if (Matches(items[i], kind))
                {
                    to = items[i].SourceStart;
                    from = SeparatorStart(src, bodyStart, to);
                    return true;
                }
            }

            for (var i = index - 1; i >= 0; i--)
            {
                if (Matches(items[i], kind))
                {
                    to = items[i].SourceStart;
                    from = SeparatorStart(src, bodyStart, to);
                    return true;
                }
            }

            from = to = 0;
            return false;

            static bool Matches(in SItem item, SItemKind? kind) =>
                item.IsFromSource && (kind is null || item.Kind == kind.Value);
        }

        /// <summary>
        /// How to re-base a node that is being written into a slot it was not parsed into. Identity
        /// when the line it started on in its own text is indented exactly like
        /// <paramref name="to"/>, in the same unit as the text it is joining -- a symbol moved
        /// between two libraries of the same style -- so that it still copies byte for byte.
        /// </summary>
        private Rebase RebaseOnto(WriteContext context, SExpression node, Indent to)
        {
            if (!UsesSource(node))
            {
                // Formatted from the tree: nothing is copied, so there is nothing to map.
                return Rebase.Identity;
            }

            var src = node.Source!;
            var line = BaseIndentOf(node);
            var unit = InferUnit(node);
            if (to.Matches(src.AsSpan(line.Start, line.Length), context)
                && (unit is null || string.Equals(unit, context.Unit, StringComparison.Ordinal)))
            {
                return Rebase.Identity;
            }

            return new Rebase(src, line, unit, to);
        }

        /// <summary>
        /// The unit a tree is indented in, read off its own text: how much further in than its
        /// parent's line a child on a line of its own starts. The shallowest form that breaks a line
        /// decides, and a form built in memory carries no evidence and is looked through. Null when
        /// nothing in the tree is indented relative to its parent.
        /// </summary>
        /// <remarks>
        /// Read off the tree's separators rather than off the raw lines, so a quoted atom that spans
        /// lines -- whose continuation lines are its text, not indentation -- is never mistaken for
        /// layout. It stops at the first form that answers, which in a KiCad file is the root.
        /// </remarks>
        private static string? InferUnit(SExpression node)
        {
            if (node.HasSource && UnitOf(node) is { } unit)
            {
                return unit;
            }

            foreach (var item in node.ItemsSpan)
            {
                if (item.Kind == SItemKind.Expression && InferUnit(item.Expression!) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// The shortest step in from its own line that any child of this form on a line of its own
        /// takes. The shortest, because KiCad 7 wrote some children one step further in than their
        /// siblings, and the step between siblings is the unit.
        /// </summary>
        private static string? UnitOf(SExpression node)
        {
            var src = node.Source!;
            var start = node.SourceStart;
            var end = start + node.SourceLength;
            var bodyStart = node.IsDocumentContainer ? start : HeaderEnd(src, start, end);
            var line = BaseIndentOf(node);
            var own = src.AsSpan(line.Start, line.Length);

            var bestStart = -1;
            var bestLength = int.MaxValue;
            var cursor = bodyStart;
            foreach (var item in node.ItemsSpan)
            {
                if (!item.IsFromSource)
                {
                    continue;
                }

                if (item.SourceStart < cursor || item.SourceStart > end)
                {
                    return null;
                }

                var from = SeparatorStart(src, cursor, item.SourceStart);
                var lineBreak = src.AsSpan(from, item.SourceStart - from).LastIndexOf('\n');
                if (lineBreak >= 0)
                {
                    var indentStart = from + lineBreak + 1;
                    var indent = src.AsSpan(indentStart, item.SourceStart - indentStart);
                    if (indent.Length > own.Length && indent.StartsWith(own) && indent.Length - own.Length < bestLength)
                    {
                        bestStart = indentStart + own.Length;
                        bestLength = indent.Length - own.Length;
                    }
                }

                cursor = item.SourceStart + item.SourceLength;
            }

            return bestStart < 0 ? null : src.Substring(bestStart, bestLength);
        }

        /// <summary>
        /// The indentation a parsed form's own lines are measured from: that of the line its closing
        /// paren stands on, when the paren starts a line, or else that of the line the form starts on.
        /// The closing paren first, because a generator that pastes a block of text in writes the
        /// block's first line wherever its cursor happened to be, while every other line of it -- the
        /// closer included -- keeps the indentation it was written with. The corpus has such files:
        /// a <c>lib_symbols</c> entry whose <c>(symbol</c> line is at one tab, its children at three
        /// and its closer at two.
        /// </summary>
        private static (int Start, int Length) BaseIndentOf(SExpression node)
        {
            var src = node.Source!;
            var start = node.SourceStart;
            var close = start + node.SourceLength - 1;
            if (!node.IsDocumentContainer && close > start && src[close] == ')')
            {
                var from = SeparatorStart(src, start + 1, close);
                var lineBreak = src.AsSpan(from, close - from).LastIndexOf('\n');
                if (lineBreak >= 0)
                {
                    return (from + lineBreak + 1, close - (from + lineBreak + 1));
                }
            }

            return LineIndentOf(src, start);
        }

        /// <summary>The whitespace the line holding <paramref name="position"/> starts with.</summary>
        private static (int Start, int Length) LineIndentOf(string src, int position)
        {
            var lineStart = position == 0 ? 0 : src.LastIndexOf('\n', position - 1) + 1;
            var end = lineStart;
            while (end < position && src[end] is ' ' or '\t')
            {
                end++;
            }

            return (lineStart, end - lineStart);
        }

        /// <summary>True when <paramref name="text"/> is <paramref name="unit"/>, repeated once or more.</summary>
        private static bool IsRepeatOf(ReadOnlySpan<char> text, string unit)
        {
            if (unit.Length == 0 || text.Length % unit.Length != 0)
            {
                return false;
            }

            for (var i = 0; i < text.Length; i += unit.Length)
            {
                if (!text.Slice(i, unit.Length).SequenceEqual(unit))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Everything the parser skips between two items. Deliberately the parser's own set.</summary>
        private static bool IsSeparator(char c) => c is ' ' or '\t' or '\r' or '\n';

        /// <summary>
        /// The index just past the <c>(token</c> that opens a form, read exactly the way the parser
        /// read it, so the header the writer copies is the header the parser produced.
        /// </summary>
        private static int HeaderEnd(string src, int start, int end)
        {
            var i = start + 1;
            while (i < end && IsSeparator(src[i]))
            {
                i++;
            }

            while (i < end && !IsSeparator(src[i]) && src[i] != '(' && src[i] != ')')
            {
                i++;
            }

            return i;
        }

        /// <summary>
        /// Start of the whitespace run ending at <paramref name="limit"/>, never reaching back past
        /// <paramref name="floor"/>.
        /// </summary>
        private static int SeparatorStart(string src, int floor, int limit)
        {
            var i = limit;
            while (i > floor && IsSeparator(src[i - 1]))
            {
                i--;
            }

            return i;
        }

        private void AppendCanonical(WriteContext context, SExpression e, Indent indent)
        {
            var sb = context.Builder;
            sb.Append('(').Append(e.Token);

            var multiline = false;
            foreach (var item in e.ItemsSpan)
            {
                if (item.Kind != SItemKind.Atom)
                {
                    multiline = true;
                    break;
                }
            }

            // Once a comment has been emitted, it runs to the end of its line -- the next item can
            // never follow it with just a space, or it would be swallowed into the comment text on
            // re-parse. It has to start fresh on its own line instead.
            var inner = indent.Deeper;
            var previousWasComment = false;
            foreach (var item in e.ItemsSpan)
            {
                if (item.Kind == SItemKind.Atom)
                {
                    if (previousWasComment)
                    {
                        AppendNewLine(sb);
                        inner.AppendTo(context);
                    }
                    else
                    {
                        sb.Append(' ');
                    }

                    AppendLeaf(item, sb);
                    previousWasComment = false;
                }
                else if (item.Kind == SItemKind.Comment)
                {
                    AppendNewLine(sb);
                    inner.AppendTo(context);
                    AppendLeaf(item, sb);
                    previousWasComment = true;
                }
                else
                {
                    AppendNewLine(sb);
                    var child = item.Expression!;
                    AppendNode(context, child, inner, RebaseOnto(context, child, inner), leadingIndent: true);
                    previousWasComment = false;
                }
            }

            if (multiline)
            {
                AppendNewLine(sb);
                indent.AppendTo(context);
            }

            sb.Append(')');
        }

        private void AppendDocumentCanonical(WriteContext context, SExpression container)
        {
            var sb = context.Builder;
            var first = true;
            foreach (var item in container.ItemsSpan)
            {
                if (!first)
                {
                    AppendNewLine(sb);
                }

                first = false;

                if (item.Kind == SItemKind.Expression)
                {
                    var form = item.Expression!;
                    AppendNode(context, form, Indent.Margin, RebaseOnto(context, form, Indent.Margin), leadingIndent: false);
                }
                else
                {
                    AppendLeaf(item, sb);
                }
            }

            AppendNewLine(sb);
        }

        private void AppendLeaf(in SItem item, StringBuilder sb)
        {
            if (item.Kind == SItemKind.Comment)
            {
                sb.Append(_options.CommentPrefix).Append(item.Text);
                return;
            }

            var text = item.Text ?? string.Empty;
            if (item.QuoteStyle != SQuoteStyle.Quoted && !NeedsQuotes(text, _options.CommentPrefix))
            {
                sb.Append(text);
                return;
            }

            sb.Append('"');
            if (text.AsSpan().IndexOfAny(MustEscape) < 0)
            {
                sb.Append(text);
            }
            else
            {
                foreach (var c in text)
                {
                    if (c is '"' or '\\')
                    {
                        sb.Append('\\');
                    }

                    sb.Append(c);
                }
            }

            sb.Append('"');
        }

        private void AppendIndent(StringBuilder sb, int level)
        {
            if (level <= 0)
            {
                return;
            }

            var indent = _options.Indent;
            if (indent.Length == 1)
            {
                sb.Append(indent[0], level);
                return;
            }

            for (var i = 0; i < level; i++)
            {
                sb.Append(indent);
            }
        }

        private void AppendNewLine(StringBuilder sb)
        {
            var nl = _options.NewLine;
            if (nl.Length == 1)
            {
                sb.Append(nl[0]);
                return;
            }

            sb.Append(nl);
        }

        private static int Capacity(SExpression e) => e.HasSource ? e.SourceLength + 64 : 256;

        /// <summary>
        /// True when the atom could not be read back as written without quotes. This is the only
        /// guess the writer makes, and it is only reached for atoms that were never parsed --
        /// a parsed atom remembers whether it arrived quoted.
        /// </summary>
        private static bool NeedsQuotes(string value, char commentPrefix) =>
            value.Length == 0 || value[0] == commentPrefix || value.AsSpan().ContainsAny(MustQuote);

        /// <summary>
        /// The state of one write: the output, and the indentation unit of the text being written,
        /// worked out once and only if something needs it.
        /// </summary>
        private sealed class WriteContext
        {
            private readonly SExpression? _layout;
            private readonly string _fallbackUnit;
            private string? _unit;

            public WriteContext(StringBuilder builder, SExpression? layout, string fallbackUnit)
            {
                Builder = builder;
                _layout = layout;
                _fallbackUnit = fallbackUnit;
            }

            public StringBuilder Builder { get; }

            /// <summary>
            /// One level of indentation in the text being written: the unit its own lines use, or
            /// <see cref="SExpressionWriterOptions.Indent"/> when it has none to go by.
            /// </summary>
            public string Unit => _unit ??= (_layout is null ? null : InferUnit(_layout)) ?? _fallbackUnit;
        }

        /// <summary>
        /// Where a line starts in the output: whitespace copied out of some text -- a file's own
        /// indentation, which need not be whole units of anything -- then a number of indent units.
        /// Keeping the copied part as a slice of that text is what lets a new node line up with a
        /// file indented in a way the writer would never have chosen, at no allocation.
        /// </summary>
        private readonly struct Indent
        {
            private readonly string? _text;
            private readonly int _start;
            private readonly int _length;
            private readonly int _levels;

            private Indent(string? text, int start, int length, int levels)
            {
                _text = text;
                _start = start;
                _length = length;
                _levels = levels;
            }

            /// <summary>The left margin: no indentation at all.</summary>
            public static Indent Margin => default;

            /// <summary>One level further in.</summary>
            public Indent Deeper => new(_text, _start, _length, _levels + 1);

            private ReadOnlySpan<char> Copied => _text.AsSpan(_start, _length);

            public static Indent OfLevels(int levels) => new(null, 0, 0, Math.Max(levels, 0));

            public static Indent Copy(string text, (int Start, int Length) span) => new(text, span.Start, span.Length, 0);

            public Indent Plus(int levels) => new(_text, _start, _length, _levels + levels);

            /// <summary>This indentation followed by <paramref name="more"/>, which is not whole units.</summary>
            public Indent Then(ReadOnlySpan<char> more, WriteContext context)
            {
                var sb = new StringBuilder(_length + (_levels * context.Unit.Length) + more.Length);
                sb.Append(Copied);
                for (var i = 0; i < _levels; i++)
                {
                    sb.Append(context.Unit);
                }

                var text = sb.Append(more).ToString();
                return new Indent(text, 0, text.Length, 0);
            }

            public void AppendTo(WriteContext context)
            {
                if (_length > 0)
                {
                    context.Builder.Append(_text, _start, _length);
                }

                if (_levels == 0)
                {
                    return;
                }

                var unit = context.Unit;
                if (unit.Length == 1)
                {
                    context.Builder.Append(unit[0], _levels);
                    return;
                }

                for (var i = 0; i < _levels; i++)
                {
                    context.Builder.Append(unit);
                }
            }

            /// <summary>True when this indentation is written exactly as <paramref name="text"/>.</summary>
            public bool Matches(ReadOnlySpan<char> text, WriteContext context)
            {
                if (!text.StartsWith(Copied))
                {
                    return false;
                }

                var rest = text[_length..];
                return _levels == 0
                    ? rest.IsEmpty
                    : rest.Length == _levels * context.Unit.Length && IsRepeatOf(rest, context.Unit);
            }
        }

        /// <summary>
        /// How the whitespace copied out of a node's source maps onto where the node now stands. A
        /// node still in the slot it was parsed into needs none; one that was moved -- from another
        /// file, or to another depth -- carries the indentation of the place it left, and every
        /// line of it is re-based: the line it started on becomes the line it starts on now, and
        /// each further step in, in its old unit, becomes a step in the unit of the text it joined.
        /// Only whitespace at the start of a line is ever touched; atoms are copied as they are.
        /// </summary>
        private readonly struct Rebase
        {
            private readonly string? _source;
            private readonly int _fromStart;
            private readonly int _fromLength;
            private readonly string? _fromUnit;
            private readonly Indent _to;

            public Rebase(string source, (int Start, int Length) from, string? fromUnit, Indent to)
            {
                _source = source;
                _fromStart = from.Start;
                _fromLength = from.Length;
                _fromUnit = fromUnit;
                _to = to;
            }

            public static Rebase Identity => default;

            public bool IsIdentity => _source is null;

            /// <summary>Where a line whose indentation in the node's source was <paramref name="line"/> starts now.</summary>
            public Indent Map(WriteContext context, ReadOnlySpan<char> line)
            {
                var from = _source.AsSpan(_fromStart, _fromLength);
                if (!line.StartsWith(from))
                {
                    // Further out than the line the node itself started on. Nothing of it may sit
                    // further out than that line does now, so that is where it goes.
                    return _to;
                }

                var rest = line[from.Length..];
                if (rest.IsEmpty)
                {
                    return _to;
                }

                return _fromUnit is not null && IsRepeatOf(rest, _fromUnit)
                    ? _to.Plus(rest.Length / _fromUnit.Length)
                    : _to.Then(rest, context);
            }
        }
    }
}
