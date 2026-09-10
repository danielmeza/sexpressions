using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SExpressions
{
    /// <summary>
    /// Knobs for <see cref="SExpressionParser"/>.
    /// </summary>
    public sealed class SExpressionParserOptions
    {
        /// <summary>
        /// The character that starts a line comment, or <see langword="null"/> to disable comments.
        /// </summary>
        /// <remarks>
        /// KiCad's own writers never emit a comment, but a hand-written <c>.kicad_dru</c> is mostly
        /// comments and they carry the justification for every rule. Measured over a 40-file KiCad
        /// 10.0.6 corpus (schematics, boards, symbol library, worksheet): zero bare atoms begin with
        /// <c>#</c>, so treating it as a comment is safe. Set this to <see langword="null"/> for a
        /// dialect where it is not.
        /// </remarks>
        public char? CommentPrefix { get; set; } = '#';

        /// <summary>Maximum nesting depth before the parser gives up. Guards against hostile input.</summary>
        public int MaxDepth { get; set; } = 256;

        /// <summary>
        /// Keep a reference to the source text so an unmodified tree can be written back byte for
        /// byte. Turn off to drop the reference when only the values matter.
        /// </summary>
        public bool TrackSource { get; set; } = true;

        /// <summary>De-duplicate short atoms and tokens while parsing. Roughly halves allocations on KiCad files.</summary>
        public bool PoolStrings { get; set; } = true;
    }

    /// <summary>
    /// Parser for S-expression text. Instances are cheap but not thread-safe; give each thread its
    /// own -- and note that instances on one thread are <em>not</em> isolated from each other, because
    /// the working buffers belong to the thread rather than to the instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two working buffers -- the item scratch stack and the atom cache -- are held per thread,
    /// so <c>new SExpressionParser().ParseAll(text)</c> in a loop pays for them once instead of once
    /// per call. Nothing observable is shared: the scratch stack is taken for the duration of a parse
    /// and wiped when it ends, and the atom cache only ever hands back a string equal to the one just
    /// scanned. Two parsers on one thread will, however, hand out the <em>same string instance</em>
    /// for equal atoms, and one will pay for a buffer the other grew.
    /// </para>
    /// <para>
    /// The atom cache deliberately keeps up to <see cref="CacheSize"/> strings of at most
    /// <see cref="PoolMaxLength"/> characters alive -- a few hundred KB per thread at the very worst,
    /// which is what makes it a cache rather than a table. That is per thread, not per process: with
    /// <see cref="ParseAllAsync"/> the continuation can land on any pool thread, so over a long run
    /// every thread that completes a parse holds one. Call <see cref="ClearThreadBuffers"/> to give
    /// it back.
    /// </para>
    /// </remarks>
    public sealed class SExpressionParser
    {
        private const int PoolMaxLength = 32;
        private const int CacheSize = 4096;
        private const int ScratchSize = 512;

        /// <summary>
        /// Largest scratch stack handed back to the thread. A bigger one is used for the parse that
        /// needed it and then dropped, so one pathological document cannot pin an oversized buffer
        /// on a thread for the rest of the process.
        /// </summary>
        /// <remarks>
        /// MEASURED over a 76-file KiCad 10.0.6 corpus -- schematics, boards, the symbol library, the
        /// worksheet and the design rules: the deepest scratch high-water mark is 307 items, in a
        /// 170 KB schematic. <see cref="ScratchSize"/> is never reached on real input, so growth is
        /// already the exceptional path and this cap only bounds how far it can be remembered.
        /// </remarks>
        private const int MaxRetainedScratch = ScratchSize * 4;

        /// <summary>Smallest item block worth allocating.</summary>
        private const int MinBlock = 8;

        /// <summary>
        /// Characters of source per item, used to size a document's first item block.
        /// </summary>
        /// <remarks>
        /// MEASURED over a 65-file KiCad 10.0.6 corpus (203 875 items): 9.4-11.0 characters per item
        /// for schematics, boards and the symbol library, 6.7 for the worksheet and 37.8-40.5 for the
        /// comment-heavy design rules. This divisor is deliberately LOWER than any of them -- it
        /// under-estimates every real file on purpose. An over-estimate is slack that is never
        /// reclaimed, while an under-estimate costs one extra block whose size is extrapolated from
        /// what the document has actually produced so far, which is accurate because density is
        /// near-uniform within a file. Sized at 9 instead, the corpus carried 24.9% aggregate slack;
        /// at 12 it carries none.
        /// </remarks>
        private const int CharsPerItem = 12;

        /// <summary>Everything that ends a bare atom. Vectorised by <see cref="SearchValues"/>.</summary>
        private static readonly SearchValues<char> Delimiters = SearchValues.Create(" \t\r\n()");

        private static readonly SearchValues<char> Whitespace = SearchValues.Create(" \t\r\n");

        private static readonly SearchValues<char> QuoteOrEscape = SearchValues.Create("\"\\");

        private static readonly SearchValues<char> LineBreak = SearchValues.Create("\r\n");

        private readonly SExpressionParserOptions _options;

        // Both working buffers live on the thread, not on the instance. Every entry point this
        // library documents -- SDocument.Parse, ParseFile, ParseAllFile, Parse -- builds a parser,
        // parses once and drops it, so an instance-scoped buffer is allocated and thrown away on
        // every single parse and the amortisation it was written for never happens. MEASURED on a
        // 142 KB schematic: the two of them are 41 KB of the 960 KB a parse allocated, and
        // `ResetPool` alone was 7.6% of all allocation in a `dotnet-trace --profile gc-verbose`.
        [ThreadStatic]
        private static string[]? t_cache;

        [ThreadStatic]
        private static SItem[]? t_scratch;

        // A fixed-size, collision-tolerant string cache. KiCad text repeats itself hard -- tokens,
        // "yes"/"no", layer names, and even coordinates -- so this removes most atom allocations for
        // one array probe and one comparison, which a Dictionary lookup could not match.
        private string[]? _cache;

        // One growable stack for the whole parse. A form's items are pushed here and copied into the
        // document's item block when the form closes.
        private SItem[] _scratch = Array.Empty<SItem>();
        private int _scratchTop;

        // The document's item block. Forms close in strict post-order, so copying each closing form's
        // items to the end of one block gives every form a contiguous slice of it and replaces one
        // array per form -- 7 071 of them on a 142 KB schematic -- with one array per document.
        //
        // This block is handed to the tree, so unlike _scratch it can never be pooled, retained on
        // the thread or reused: the next parse would be writing into an array the last document is
        // still reading. It is cleared from the instance in ReleaseBuffers for the same reason.
        private SItem[] _block = Array.Empty<SItem>();
        private int _blockTop;
        private int _emitted;
        private int _textLength;

        /// <summary>Creates a parser with default options.</summary>
        public SExpressionParser()
            : this(null)
        {
        }

        /// <summary>Creates a parser with explicit options.</summary>
        /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
        public SExpressionParser(SExpressionParserOptions? options) => _options = options ?? new SExpressionParserOptions();

        /// <summary>Gets the options in force.</summary>
        public SExpressionParserOptions Options => _options;

        /// <summary>
        /// Parse a string containing S-expressions and return the root expression.
        /// </summary>
        /// <param name="text">The text to parse.</param>
        /// <returns>The first top-level expression.</returns>
        /// <remarks>
        /// A file may hold more than one top-level form -- a <c>.kicad_dru</c> always does. Use
        /// <see cref="ParseAll(string)"/> to get all of them; this returns only the first.
        /// </remarks>
        public SExpression Parse(string text) =>
            ParseAll(text).Root ?? throw new SExpressionFormatException("Expected '(' at the start of S-expression");

        /// <summary>
        /// Parse a file containing S-expressions and return the root expression.
        /// </summary>
        /// <param name="filePath">Path to the file to parse.</param>
        /// <returns>The first top-level expression.</returns>
        public SExpression ParseFile(string filePath) => Parse(File.ReadAllText(filePath));

        /// <summary>
        /// Parses every top-level form in <paramref name="text"/>, keeping the comments and the
        /// whitespace between them.
        /// </summary>
        /// <param name="text">The text to parse.</param>
        /// <returns>The document.</returns>
        public SDocument ParseAll(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            AcquireBuffers();
            try
            {
                var container = new SExpression(string.Empty);
                container.MarkAsDocumentContainer();

                var source = _options.TrackSource && text.Length <= SItem.MaxSourceLength ? text : null;
                _scratchTop = 0;
                _textLength = text.Length;
                var pos = 0;
                var start = _scratchTop;
                ReadItems(text, ref pos, 0, topLevel: true, container);
                if (pos < text.Length)
                {
                    throw new SExpressionFormatException("Unbalanced ')'", text, pos);
                }

                Harvest(start, text.Length, out var block, out var offset, out var count);
                container.SetParsed(block, offset, count, source, 0, text.Length);
                return new SDocument(container);
            }
            finally
            {
                ReleaseBuffers();
            }
        }

        /// <summary>Parses every top-level form in a file.</summary>
        /// <param name="filePath">Path to the file to parse.</param>
        /// <returns>The document.</returns>
        public SDocument ParseAllFile(string filePath) => ParseAll(File.ReadAllText(filePath));

        /// <summary>Parses every top-level form in a file, reading it asynchronously.</summary>
        /// <param name="filePath">Path to the file to parse.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The document.</returns>
        /// <remarks>
        /// The I/O is async; the parse itself is not incremental on purpose. The tree is several
        /// times the size of the text it came from, so streaming saves nothing, and holding the
        /// source is what lets an unmodified document be written back byte for byte.
        /// </remarks>
        public async Task<SDocument> ParseAllFileAsync(string filePath, CancellationToken cancellationToken = default) =>
            ParseAll(await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false));

        /// <summary>Parses every top-level form read from <paramref name="reader"/>.</summary>
        /// <param name="reader">The reader to drain.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The document.</returns>
        public async Task<SDocument> ParseAllAsync(TextReader reader, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);
            return ParseAll(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }

        // -------------------------------------------------------------------------------- scanner

        private void ReadItems(string src, ref int pos, int depth, bool topLevel, SExpression owner)
        {
            var s = src.AsSpan();
            var hasComments = _options.CommentPrefix.HasValue;
            var commentChar = _options.CommentPrefix ?? '\0';

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length)
                {
                    if (topLevel)
                    {
                        return;
                    }

                    throw new SExpressionFormatException("Unexpected end of input; expected ')'", src, pos);
                }

                var c = s[pos];
                if (c == ')')
                {
                    return;
                }

                if (c == '(')
                {
                    var formStart = pos;
                    var child = ReadForm(src, ref pos, depth + 1);

                    // Parented here rather than in Harvest: this is the one place that knows both
                    // the child and the form it belongs to, and doing it now leaves Harvest with
                    // nothing to inspect, so it can bulk-copy instead of walking item by item.
                    child.SetParsedParent(owner);
                    Push(SItem.ParsedExpression(child, formStart, pos - formStart));
                    continue;
                }

                if (hasComments && c == commentChar)
                {
                    var commentStart = pos;
                    var lineEnd = s[pos..].IndexOfAny(LineBreak);
                    var end = lineEnd < 0 ? s.Length : pos + lineEnd;
                    Push(SItem.ParsedComment(s.Slice(commentStart + 1, end - commentStart - 1).ToString(), commentStart, end - commentStart));
                    pos = end;
                    continue;
                }

                var atomStart = pos;
                if (c == '"')
                {
                    Push(SItem.ParsedAtom(ReadQuoted(src, ref pos), SQuoteStyle.Quoted, atomStart, pos - atomStart));
                }
                else
                {
                    Push(SItem.ParsedAtom(ReadBare(src, ref pos), SQuoteStyle.Bare, atomStart, pos - atomStart));
                }
            }
        }

        private SExpression ReadForm(string src, ref int pos, int depth)
        {
            if (depth > _options.MaxDepth)
            {
                throw new SExpressionFormatException($"Nesting deeper than {_options.MaxDepth}", src, pos);
            }

            var s = src.AsSpan();
            var start = pos;
            pos++;
            SkipWhitespace(s, ref pos);

            var expression = new SExpression(ReadBare(src, ref pos));
            var scratchBase = _scratchTop;
            ReadItems(src, ref pos, depth, topLevel: false, expression);

            if (pos >= s.Length || s[pos] != ')')
            {
                throw new SExpressionFormatException("Expected ')'", src, pos);
            }

            pos++;
            Harvest(scratchBase, pos, out var block, out var offset, out var count);
            expression.SetParsed(block, offset, count, _options.TrackSource && src.Length <= SItem.MaxSourceLength ? src : null, start, pos - start);
            return expression;
        }

        private string ReadBare(string src, ref int pos)
        {
            var s = src.AsSpan();
            var start = pos;
            var rel = s[pos..].IndexOfAny(Delimiters);
            pos = rel < 0 ? s.Length : pos + rel;
            return Intern(s.Slice(start, pos - start));
        }

        private string ReadQuoted(string src, ref int pos)
        {
            var s = src.AsSpan();
            var contentStart = pos + 1;
            var rel = s[contentStart..].IndexOfAny(QuoteOrEscape);
            if (rel < 0)
            {
                throw new SExpressionFormatException("Unterminated quoted string", src, pos);
            }

            var i = contentStart + rel;
            if (s[i] == '"')
            {
                var text = Intern(s.Slice(contentStart, i - contentStart));
                pos = i + 1;
                return text;
            }

            return ReadQuotedEscaped(src, ref pos);
        }

        private static string ReadQuotedEscaped(string src, ref int pos)
        {
            var s = src.AsSpan();
            var start = pos;
            var i = pos + 1;
            var sb = new StringBuilder(32);

            while (i < s.Length)
            {
                var c = s[i];
                if (c == '"')
                {
                    pos = i + 1;
                    return sb.ToString();
                }

                if (c == '\\' && i + 1 < s.Length)
                {
                    var next = s[i + 1];

                    // Only the two escapes KiCad actually emits are decoded, so encoding them again
                    // is an exact inverse. Anything else keeps its backslash and stays as written.
                    if (next is '\\' or '"')
                    {
                        sb.Append(next);
                        i += 2;
                        continue;
                    }

                    sb.Append(c).Append(next);
                    i += 2;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            throw new SExpressionFormatException("Unterminated quoted string", src, start);
        }

        private void Push(in SItem item)
        {
            if (_scratchTop == _scratch.Length)
            {
                Array.Resize(ref _scratch, _scratch.Length * 2);
            }

            _scratch[_scratchTop++] = item;
        }

        /// <summary>
        /// Copies everything pushed since <paramref name="scratchBase"/> to the end of the document's
        /// item block, pops the scratch stack back, and reports the slice the closing form owns.
        /// </summary>
        /// <remarks>
        /// Nested forms are already parented by <see cref="ReadItems"/>, so the copy has no
        /// per-item work left: the old loop paid a type check (<c>_payload as SExpression</c>) on
        /// every atom in the document just to find the forms among them.
        /// <para>
        /// MEASURED: the loop below beats <c>Span.CopyTo</c> here -- 453.30 us against 470.87 us on
        /// the 142 KB schematic. A form holds 2.1 items on average, and at that size the setup
        /// <c>Buffer.Memmove</c> does costs more than the copy it saves.
        /// </para>
        /// </remarks>
        /// <param name="scratchBase">Where this form's items start on the scratch stack.</param>
        /// <param name="pos">How far into the source the parse has read. Used to size a new block.</param>
        /// <param name="block">The block the slice lives in.</param>
        /// <param name="offset">Where the slice starts in <paramref name="block"/>.</param>
        /// <param name="count">How long the slice is.</param>
        private void Harvest(int scratchBase, int pos, out SItem[] block, out int offset, out int count)
        {
            count = _scratchTop - scratchBase;
            if (count == 0)
            {
                block = Array.Empty<SItem>();
                offset = 0;
                return;
            }

            if (_blockTop + count > _block.Length)
            {
                GrowBlock(count, pos);
            }

            block = _block;
            offset = _blockTop;
            var scratch = _scratch;
            for (var i = 0; i < count; i++)
            {
                block[offset + i] = scratch[scratchBase + i];
            }

            _blockTop = offset + count;
            _emitted += count;
            _scratchTop = scratchBase;
        }

        /// <summary>
        /// Starts a new item block, big enough for the form that did not fit.
        /// </summary>
        /// <remarks>
        /// The old block is NOT copied or resized. Every form harvested into it already holds a
        /// reference to it and reads its own slice out of it, so the correct move is to leave it
        /// alone and continue in a fresh one; the only cost of a growth is the tail of the old block
        /// -- at most <paramref name="count"/> - 1 slots -- which nothing will ever use.
        /// <para>
        /// The size comes from what the document has produced so far rather than from doubling: at
        /// the point the first block fills, <c>_emitted</c> items have come out of <c>pos</c>
        /// characters, and extrapolating that to the whole text lands within a few percent because
        /// item density inside one file barely varies. Doubling instead would overshoot by the
        /// fraction of the file already read -- 60% or more of the block, on a file whose first
        /// estimate was only slightly short.
        /// </para>
        /// </remarks>
        private void GrowBlock(int count, int pos)
        {
            long wanted;
            if (_emitted == 0 || pos <= 0)
            {
                wanted = _textLength / CharsPerItem;
            }
            else
            {
                var projected = (long)_emitted * _textLength / pos - _emitted;
                wanted = projected + (projected >> 3);
            }

            // count last, and after the ceiling: a single form bigger than MaxBlock still has to fit
            // in one contiguous slice, so it is the floor the cap cannot argue with.
            var size = Math.Max(Math.Clamp(wanted, MinBlock, MaxBlock), count);
            _block = new SItem[size];
            _blockTop = 0;
        }

        /// <summary>
        /// Ceiling on one item block, so a wrong extrapolation cannot ask for an absurd array. A
        /// document needing more than this simply gets more blocks.
        /// </summary>
        private const int MaxBlock = 1 << 24;

        private static void SkipWhitespace(ReadOnlySpan<char> s, ref int pos)
        {
            if (pos >= s.Length)
            {
                return;
            }

            var rel = s[pos..].IndexOfAnyExcept(Whitespace);
            pos = rel < 0 ? s.Length : pos + rel;
        }

        // ----------------------------------------------------------------------------- string pool

        /// <summary>
        /// Takes this thread's working buffers for the duration of one parse.
        /// </summary>
        /// <remarks>
        /// The scratch stack is <em>taken</em> -- the thread-static slot is nulled -- so a reentrant
        /// parse on the same thread gets its own buffer instead of corrupting this one's stack.
        /// The atom cache is not taken: it is content-addressed, so sharing it with a nested parse
        /// can only produce more hits, never a wrong answer.
        /// </remarks>
        private void AcquireBuffers()
        {
            _scratch = t_scratch ?? new SItem[ScratchSize];
            t_scratch = null;

            // Entries survive between parses: a stale entry is still a valid string, and the
            // comparison in Intern is what decides a hit, so there is nothing to invalidate.
            _cache = _options.PoolStrings ? t_cache ??= new string[CacheSize] : null;

            // Always a fresh block. It belongs to the document that comes out of this parse.
            _block = Array.Empty<SItem>();
            _blockTop = 0;
            _emitted = 0;
        }

        /// <summary>
        /// Returns the scratch stack to the thread, wiped.
        /// </summary>
        /// <remarks>
        /// MEASURED TRAP, and the reason this clears the <em>whole</em> array rather than the part
        /// the parse used: every <see cref="SItem"/> left behind holds a reference to the document
        /// just built, and through it the entire tree and the source text it was parsed from. A
        /// thread that parses one large file and then goes idle would keep it all alive. Clearing
        /// only <c>[0, _scratchTop)</c> would wipe nothing at all -- by the time the parse finishes,
        /// <c>_scratchTop</c> is back to zero and the region that retains is the one above it.
        /// The parser tests cover this with a weak reference.
        /// </remarks>
        private void ReleaseBuffers()
        {
            var scratch = _scratch;
            Array.Clear(scratch);
            _scratch = Array.Empty<SItem>();
            _scratchTop = 0;

            // The same retention trap as the scratch stack, one level up: the block holds every item
            // in the document just built, so an instance that kept the reference would keep that
            // whole tree and its source text alive until it was parsed over or collected. Dropping
            // the reference is enough here -- the block is never reused, so there is nothing to wipe.
            _block = Array.Empty<SItem>();
            _blockTop = 0;
            _emitted = 0;

            // Keep whichever buffer is larger, up to the cap: Push may have grown this one past the
            // thread's, and remembering that is what stops the next parse re-growing it. Past the cap
            // the oversized array is dropped instead -- one deep document should not pin a buffer on
            // this thread for the life of the process.
            if (scratch.Length <= MaxRetainedScratch && (t_scratch is null || t_scratch.Length < scratch.Length))
            {
                t_scratch = scratch;
            }
        }

        /// <summary>
        /// Releases the parsing buffers the calling thread is holding: the atom cache and the item
        /// scratch stack.
        /// </summary>
        /// <remarks>
        /// This is an optimisation to undo, not state to reset -- parsing after it is correct, it
        /// just pays to rebuild the buffers. It exists because they are held per thread and nothing
        /// else hands them back. <see cref="ParseAllAsync"/> and
        /// <see cref="ParseAllFileAsync(string, CancellationToken)"/> resume on a pool thread, so a
        /// long-running process can end up holding one atom cache on every thread that has ever
        /// completed a parse. Affects only the calling thread.
        /// </remarks>
        public static void ClearThreadBuffers()
        {
            t_cache = null;
            t_scratch = null;
        }

        private string Intern(ReadOnlySpan<char> span)
        {
            if (span.IsEmpty)
            {
                return string.Empty;
            }

            var cache = _cache;
            if (cache is null || span.Length > PoolMaxLength)
            {
                return span.ToString();
            }

            var slot = (int)(Hash(span) & (CacheSize - 1));
            var hit = cache[slot];
            if (hit is not null && span.SequenceEqual(hit))
            {
                return hit;
            }

            var text = span.ToString();
            cache[slot] = text;
            return text;
        }

        /// <summary>
        /// Length plus three sampled characters. A weak hash is fine here: a collision only costs one
        /// failed comparison and one extra string, never a wrong answer, and this runs once per atom.
        /// </summary>
        private static uint Hash(ReadOnlySpan<char> span)
        {
            var h = ((uint)span.Length * 2654435761u) ^ span[0];
            h = (h << 5) ^ span[^1];
            if (span.Length > 2)
            {
                h ^= (uint)span[span.Length >> 1] << 11;
            }

            return h ^ (h >> 13);
        }
    }
}
