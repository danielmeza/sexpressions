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

        /// <summary>One level of indentation. Defaults to a tab, which is what KiCad writes.</summary>
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
            var verbatim = AppendNode(expression, sb, indentLevel, leadingIndent: true);
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

            // -1, not 0: the container is a synthetic node holding the top-level forms, not a form
            // itself, so it occupies no indent level and its children are the level-0 forms. Splicing
            // it at 0 put everything the writer had to re-format one indent deeper than the text
            // around it -- invisible until a caller adds a node, and then wrong on every line of it.
            if (!(CanSplice(container) && TrySplice(container, sb, -1)))
            {
                sb.Length = 0;
                AppendDocumentCanonical(container, sb);
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
        private bool AppendNode(SExpression e, StringBuilder sb, int level, bool leadingIndent)
        {
            if (leadingIndent)
            {
                AppendIndent(sb, level);
            }

            if (CanCopyVerbatim(e))
            {
                sb.Append(e.Source, e.SourceStart, e.SourceLength);
                return true;
            }

            if (CanSplice(e))
            {
                var mark = sb.Length;
                if (TrySplice(e, sb, level))
                {
                    return true;
                }

                sb.Length = mark;
            }

            AppendCanonical(e, sb, level);
            return false;
        }

        private bool CanCopyVerbatim(SExpression e) =>
            _options.Format != SExpressionFormat.Canonical && e.HasSource && !e.IsModified;

        private bool CanSplice(SExpression e) =>
            _options.Format != SExpressionFormat.Canonical && e.HasSource && !e.HeaderInvalid
            && (e.ItemCount > 0 || e.ItemsChanged);

        /// <summary>
        /// Rebuilds a changed form out of the source it came from. Each item that still holds its
        /// slot is written behind the whitespace that preceded it in the file, so the only bytes
        /// that move are the ones that were edited. An item with no slot -- one that was just
        /// inserted -- gets a separator synthesised from the ones its neighbours already use, and an
        /// item that was removed takes its own separator with it. Nothing else in the form is
        /// touched: a sibling nobody edited keeps its bytes, its indentation and its line, and the
        /// closing paren keeps its column.
        /// </summary>
        private bool TrySplice(SExpression e, StringBuilder sb, int level)
        {
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

            cursor = bodyStart;
            var previousWasComment = false;
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                int itemLevel;
                if (item.IsFromSource)
                {
                    // Only the whitespace immediately in front of the item is its separator.
                    // Anything before that is the text of items that have since been removed, and
                    // copying it would put them back.
                    AppendSourceSeparator(sb, src, SeparatorStart(src, cursor, item.SourceStart), item.SourceStart, previousWasComment, level + 1);
                    cursor = item.SourceStart + item.SourceLength;
                    itemLevel = level + 1;
                }
                else
                {
                    itemLevel = AppendSynthesizedSeparator(sb, src, items, i, bodyStart, bodyEnd, previousWasComment, level + 1);
                }

                if (item.Kind == SItemKind.Expression)
                {
                    AppendNode(item.Expression!, sb, itemLevel, leadingIndent: false);
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

            AppendSourceSeparator(sb, src, SeparatorStart(src, cursor, bodyEnd), bodyEnd, previousWasComment, level);
            if (!container)
            {
                sb.Append(')');
            }

            return true;
        }

        /// <summary>Copies one separator out of the source, unaltered unless a comment forces a line break.</summary>
        private void AppendSourceSeparator(StringBuilder sb, string src, int from, int to, bool previousWasComment, int level)
        {
            // A comment runs to the end of its line, so whatever follows one has to start on the
            // next line or it is swallowed into the comment text on re-parse. Only a newly inserted
            // comment can put a sibling in that position; a parsed one already has its line break.
            if (previousWasComment && src.AsSpan(from, to - from).IndexOf('\n') < 0)
            {
                AppendNewLine(sb);
                AppendIndent(sb, level);
                return;
            }

            sb.Append(src, from, to - from);
        }

        /// <summary>
        /// Writes the whitespace a newly inserted item needs, taken from the separator its
        /// neighbours already use: the one in front of the sibling that will follow it, or -- when
        /// it is being appended -- the one in front of the sibling it follows. Returns the indent
        /// level that separator lands on, so a multi-line new item lines up with the file it is
        /// joining rather than with the writer's own idea of the depth.
        /// </summary>
        private int AppendSynthesizedSeparator(
            StringBuilder sb,
            string src,
            ReadOnlySpan<SItem> items,
            int index,
            int bodyStart,
            int bodyEnd,
            bool previousWasComment,
            int level)
        {
            var found = TryNeighbourSeparator(src, items, index, bodyStart, out var separator);

            // A comment cannot be followed on its own line; see AppendSourceSeparator.
            var needsLine = previousWasComment && (!found || separator.IndexOf('\n') < 0);

            // A form with nothing in it yet has no separator to copy, so the only hint left is the
            // shape of the form itself: one already broken over lines stays broken, "(foo)" does not.
            if (!found && !needsLine && items[index].Kind != SItemKind.Atom)
            {
                needsLine = src.AsSpan(bodyStart, bodyEnd - bodyStart).IndexOf('\n') >= 0;
            }

            if (needsLine)
            {
                AppendNewLine(sb);
                AppendIndent(sb, level);
                return level;
            }

            if (!found || separator.Length == 0)
            {
                // Two items with nothing between them fuse into one atom on re-parse. This is the
                // one byte an insert is always allowed: the separator that makes it an item at all.
                sb.Append(' ');
                return level;
            }

            sb.Append(separator);
            return IndentLevelOf(separator, level);
        }

        /// <summary>
        /// Finds the separator a new item should copy. A sibling of the same kind is preferred --
        /// atoms hug their token and each other, child forms get their own line -- and the sibling
        /// that will FOLLOW the new item is preferred over the one behind it, because that is the
        /// slot the new item is taking and the sibling then keeps its own bytes unchanged.
        /// </summary>
        private static bool TryNeighbourSeparator(string src, ReadOnlySpan<SItem> items, int index, int bodyStart, out ReadOnlySpan<char> separator)
        {
            var kind = items[index].Kind;
            if (TryNeighbourSeparator(src, items, index, bodyStart, kind, out separator))
            {
                return true;
            }

            // An atom with no atom sibling belongs next to whatever it follows, on that line: that
            // is how "(token value ...)" is written wherever this format is used. Falling through to
            // a child form's separator here would push it onto a line of its own.
            return kind != SItemKind.Atom
                && TryNeighbourSeparator(src, items, index, bodyStart, null, out separator);
        }

        private static bool TryNeighbourSeparator(string src, ReadOnlySpan<SItem> items, int index, int bodyStart, SItemKind? kind, out ReadOnlySpan<char> separator)
        {
            for (var i = index + 1; i < items.Length; i++)
            {
                if (Matches(items[i], kind))
                {
                    separator = Separator(src, bodyStart, items[i].SourceStart);
                    return true;
                }
            }

            for (var i = index - 1; i >= 0; i--)
            {
                if (Matches(items[i], kind))
                {
                    separator = Separator(src, bodyStart, items[i].SourceStart);
                    return true;
                }
            }

            separator = default;
            return false;

            static bool Matches(in SItem item, SItemKind? kind) =>
                item.IsFromSource && (kind is null || item.Kind == kind.Value);

            static ReadOnlySpan<char> Separator(string src, int bodyStart, int at)
            {
                var from = SeparatorStart(src, bodyStart, at);
                return src.AsSpan(from, at - from);
            }
        }

        /// <summary>
        /// How many indent levels a separator ends on, or <paramref name="fallback"/> when its
        /// indentation is not written in the unit this writer uses. Reading it off the file rather
        /// than off the tree is what keeps a new node aligned with a document whose indentation does
        /// not match its nesting -- KiCad's own generators emit several such files.
        /// </summary>
        private int IndentLevelOf(ReadOnlySpan<char> separator, int fallback)
        {
            var lineStart = separator.LastIndexOf('\n');
            if (lineStart < 0)
            {
                return fallback;
            }

            var indent = separator[(lineStart + 1)..];
            var unit = _options.Indent;
            if (unit.Length == 0 || indent.Length % unit.Length != 0)
            {
                return fallback;
            }

            for (var i = 0; i < indent.Length; i += unit.Length)
            {
                if (!indent.Slice(i, unit.Length).SequenceEqual(unit))
                {
                    return fallback;
                }
            }

            return indent.Length / unit.Length;
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

        private void AppendCanonical(SExpression e, StringBuilder sb, int level)
        {
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
            var previousWasComment = false;
            foreach (var item in e.ItemsSpan)
            {
                if (item.Kind == SItemKind.Atom)
                {
                    if (previousWasComment)
                    {
                        AppendNewLine(sb);
                        AppendIndent(sb, level + 1);
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
                    AppendIndent(sb, level + 1);
                    AppendLeaf(item, sb);
                    previousWasComment = true;
                }
                else
                {
                    AppendNewLine(sb);
                    AppendNode(item.Expression!, sb, level + 1, leadingIndent: true);
                    previousWasComment = false;
                }
            }

            if (multiline)
            {
                AppendNewLine(sb);
                AppendIndent(sb, level);
            }

            sb.Append(')');
        }

        private void AppendDocumentCanonical(SExpression container, StringBuilder sb)
        {
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
                    AppendNode(item.Expression!, sb, 0, leadingIndent: false);
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
    }
}
