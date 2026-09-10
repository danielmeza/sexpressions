using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SExpressions
{
    /// <summary>
    /// A node in an S-expression tree: a token followed by an ordered sequence of atoms, nested
    /// forms and comments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The model is deliberately schema-free. Nothing here knows what a KiCad token means, which is
    /// why a file written by a newer KiCad still round-trips through it unchanged.
    /// </para>
    /// <para>
    /// <see cref="Items"/> is the single source of truth and holds everything in source order;
    /// <see cref="Values"/> and <see cref="Children"/> are live views over it, so a form written as
    /// <c>(a 1 (b) 2)</c> is written back as <c>(a 1 (b) 2)</c> and not as <c>(a 1 2 (b))</c>.
    /// </para>
    /// </remarks>
    public sealed class SExpression
    {
        [Flags]
        private enum Flag : byte
        {
            None = 0,
            Dirty = 1,

            /// <summary>The <c>(token</c> that opens this form in the source no longer matches.</summary>
            HeaderInvalid = 2,
            DocumentContainer = 4,

            /// <summary>Items have been inserted or removed, so the source no longer lists the same ones.</summary>
            ItemsChanged = 8,
        }

        // A parsed form holds a SLICE -- _offset and _count -- of one contiguous block shared with
        // every other form in its document, rather than an array of its own. MEASURED over 93 762
        // forms in a 65-file KiCad corpus: 48.8% of forms hold exactly ONE item, so the 24-byte array
        // header usually cost more than the 16-byte item it carried, and the headers alone were 18%
        // of everything a 142 KB parse allocated. A form that is mutated copies its slice out into an
        // array it owns; see InsertItem.
        private SItem[] _items = Array.Empty<SItem>();
        private int _offset;
        private int _count;
        private string _token;
        private SExpression? _parent;
        private Flag _flags;

        internal string? Source;
        internal int SourceStart = -1;
        internal int SourceLength;

        /// <summary>
        /// Creates a new S-expression with the specified token name.
        /// </summary>
        /// <param name="token">The token or name of this expression.</param>
        public SExpression(string token)
        {
            ArgumentNullException.ThrowIfNull(token);
            _token = token;
        }

        /// <summary>
        /// Creates a new S-expression with the specified token name and values.
        /// </summary>
        /// <param name="token">The token or name of this expression.</param>
        /// <param name="values">Initial values for this expression.</param>
        public SExpression(string token, params string[] values)
            : this(token)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values.Length == 0)
            {
                return;
            }

            _items = new SItem[values.Length];
            _count = values.Length;
            for (var i = 0; i < values.Length; i++)
            {
                _items[i] = SItem.CreateAtom(values[i]);
            }
        }

        /// <summary>
        /// Gets or sets the token or name of this S-expression.
        /// </summary>
        public string Token
        {
            get => _token;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (string.Equals(_token, value, StringComparison.Ordinal))
                {
                    return;
                }

                _token = value;
                _flags |= Flag.HeaderInvalid;
                MarkDirty();
            }
        }

        /// <summary>
        /// Gets the form that contains this one, or <see langword="null"/> for a root.
        /// </summary>
        public SExpression? Parent => _parent is null || _parent.IsDocumentContainer ? null : _parent;

        /// <summary>
        /// Gets every element of this form in source order: atoms, nested forms and comments.
        /// </summary>
        public SItemList Items => new(this);

        /// <summary>
        /// Gets the atoms of this expression as a live, mutable list of strings.
        /// </summary>
        /// <remarks>
        /// Adding through this view inserts after the last existing atom, which keeps the familiar
        /// "values, then children" shape for forms that have it.
        /// </remarks>
        public SValueCollection Values => new(this);

        /// <summary>
        /// Gets the nested forms of this expression as a live, mutable list.
        /// </summary>
        public SChildCollection Children => new(this);

        /// <summary>
        /// Gets the comments attached inside this form, in order, without their leading <c>#</c>.
        /// </summary>
        public IEnumerable<string> Comments
        {
            get
            {
                // Indexed rather than a foreach over ItemsSpan: this is an iterator, and the span
                // would have to live across a yield.
                for (var i = 0; i < _count; i++)
                {
                    var item = ItemAt(i);
                    if (item.Kind == SItemKind.Comment)
                    {
                        yield return item.Text ?? string.Empty;
                    }
                }
            }
        }

        /// <summary>
        /// True when this form (or anything under it) has been changed since it was parsed, or when
        /// it was never parsed at all. A writer reproduces an unmodified form byte for byte.
        /// </summary>
        public bool IsModified => (_flags & Flag.Dirty) != 0 || Source is null;

        /// <summary>True when this form was produced by a parser and still knows its source text.</summary>
        public bool HasSource => Source is not null && SourceStart >= 0;

        /// <summary>
        /// Gets the exact text this form was parsed from, or an empty span when it was not parsed.
        /// </summary>
        public ReadOnlySpan<char> SourceSpan => Source is null || SourceStart < 0 ? default : Source.AsSpan(SourceStart, SourceLength);

        /// <summary>
        /// This form's items. A parsed form holds a slice of a block shared with the rest of its
        /// document, so this is a view over that block and never an array this form owns alone --
        /// which is why it is a span: nothing can store it, hand it out, or mistake its length for
        /// the block's.
        /// </summary>
        internal ReadOnlySpan<SItem> ItemsSpan => _items.AsSpan(_offset, _count);

        /// <summary>How many items this form holds.</summary>
        internal int ItemCount => _count;

        /// <summary>
        /// One item by index. Exists for the iterator methods, where a <c>ref struct</c> local cannot
        /// live across a <c>yield</c>.
        /// </summary>
        internal SItem ItemAt(int index) => _items[_offset + index];

        /// <summary>True when the source text of this form's <c>(token</c> is stale and cannot be copied.</summary>
        internal bool HeaderInvalid => (_flags & Flag.HeaderInvalid) != 0;

        /// <summary>
        /// True when items have been inserted or removed since the parse. The gaps the source
        /// records still describe the items that stayed, so the writer can splice them; it is only
        /// the gap around a new item that has to be synthesised.
        /// </summary>
        internal bool ItemsChanged => (_flags & Flag.ItemsChanged) != 0;

        internal bool IsDocumentContainer => (_flags & Flag.DocumentContainer) != 0;

        internal void MarkAsDocumentContainer() => _flags |= Flag.DocumentContainer;

        // ------------------------------------------------------------------- parsing entry points

        /// <summary>Parses the first top-level form in <paramref name="text"/>.</summary>
        /// <param name="text">S-expression text.</param>
        /// <returns>The first top-level form.</returns>
        public static SExpression Parse(string text) => new SExpressionParser().Parse(text);

        /// <summary>Parses the first top-level form in a file.</summary>
        /// <param name="path">Path to the file.</param>
        /// <returns>The first top-level form.</returns>
        public static SExpression Load(string path) => new SExpressionParser().ParseFile(path);

        // ----------------------------------------------------------------------------- navigation

        /// <summary>
        /// Gets the first child form with the given token, or <see langword="null"/>.
        /// </summary>
        /// <param name="token">The token to look for.</param>
        public SExpression? this[string token] => GetChild(token);

        /// <summary>
        /// Gets the first child S-expression with the specified token.
        /// </summary>
        /// <param name="token">The token to search for.</param>
        /// <returns>The first child matching the token, or null if not found.</returns>
        public SExpression? GetChild(string token)
        {
            foreach (var item in ItemsSpan)
            {
                if (item.Kind == SItemKind.Expression && string.Equals(item.Expression!.Token, token, StringComparison.Ordinal))
                {
                    return item.Expression;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets all child S-expressions with the specified token.
        /// </summary>
        /// <param name="token">The token to search for.</param>
        /// <returns>An enumerable of matching child expressions.</returns>
        public IEnumerable<SExpression> GetChildren(string token)
        {
            for (var i = 0; i < _count; i++)
            {
                var item = ItemAt(i);
                if (item.Kind == SItemKind.Expression && string.Equals(item.Expression!.Token, token, StringComparison.Ordinal))
                {
                    yield return item.Expression;
                }
            }
        }

        /// <summary>
        /// Walks a <c>/</c>-separated token path from this form and returns the first match.
        /// <c>*</c> matches any token.
        /// </summary>
        /// <param name="path">For example <c>lib_symbols/symbol/property</c>.</param>
        /// <returns>The first matching form, or <see langword="null"/>.</returns>
        public SExpression? Find(string path) => FindAll(path).FirstOrDefault();

        /// <summary>
        /// Walks a <c>/</c>-separated token path from this form and returns every match, in document order.
        /// <c>*</c> matches any token.
        /// </summary>
        /// <param name="path">For example <c>lib_symbols/symbol/property</c>.</param>
        /// <returns>Every matching form.</returns>
        public IEnumerable<SExpression> FindAll(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return Enumerable.Empty<SExpression>();
            }

            IEnumerable<SExpression> current = new[] { this };
            foreach (var segment in segments)
            {
                var seg = segment;
                current = current.SelectMany(e => seg == "*" ? e.Children.AsEnumerable() : e.GetChildren(seg));
            }

            return current;
        }

        /// <summary>
        /// Enumerates every form below this one, depth first, in document order.
        /// </summary>
        /// <param name="token">When given, only forms with this token are returned.</param>
        /// <returns>The matching descendants.</returns>
        public IEnumerable<SExpression> Descendants(string? token = null)
        {
            for (var i = 0; i < _count; i++)
            {
                var item = ItemAt(i);
                if (item.Kind != SItemKind.Expression)
                {
                    continue;
                }

                var child = item.Expression!;
                if (token is null || string.Equals(child.Token, token, StringComparison.Ordinal))
                {
                    yield return child;
                }

                foreach (var d in child.Descendants(token))
                {
                    yield return d;
                }
            }
        }

        // -------------------------------------------------------------------------- reading values

        /// <summary>
        /// Gets the value at the specified index.
        /// </summary>
        /// <param name="index">The index of the value to get.</param>
        /// <returns>The value at the specified index, or null if the index is out of range.</returns>
        public string? GetValue(int index)
        {
            if (index < 0)
            {
                return null;
            }

            var seen = 0;
            foreach (var item in ItemsSpan)
            {
                if (item.Kind == SItemKind.Atom && seen++ == index)
                {
                    return item.Text;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the first value as a string.
        /// </summary>
        /// <returns>The first value, or null if there are no values.</returns>
        public string? GetValueAsString() => GetValue(0);

        /// <summary>
        /// Gets the value at the specified index as a double, using the invariant culture.
        /// </summary>
        /// <param name="index">The index of the value to get.</param>
        /// <returns>The value as a double, or 0 if the value cannot be parsed.</returns>
        public double GetValueAsDouble(int index = 0) =>
            double.TryParse(GetValue(index), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

        /// <summary>
        /// Gets the value at the specified index as an integer, using the invariant culture.
        /// </summary>
        /// <param name="index">The index of the value to get.</param>
        /// <returns>The value as an integer, or 0 if the value cannot be parsed.</returns>
        public int GetValueAsInt(int index = 0) =>
            int.TryParse(GetValue(index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

        /// <summary>
        /// Gets the value at the specified index as a boolean.
        /// </summary>
        /// <param name="index">The index of the value to get.</param>
        /// <returns>The value as a boolean, or false if the value cannot be parsed.</returns>
        public bool GetValueAsBool(int index = 0)
        {
            var value = GetValue(index);
            return value is not null
                && (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "1", StringComparison.Ordinal));
        }

        /// <summary>
        /// Tries to read the value at <paramref name="index"/> as <typeparamref name="T"/>, invariant culture.
        /// </summary>
        /// <typeparam name="T">Any parsable type, e.g. <see cref="double"/> or <see cref="int"/>.</typeparam>
        /// <param name="index">The value index.</param>
        /// <param name="value">The parsed value.</param>
        /// <returns>True when the value exists and parsed.</returns>
        public bool TryGetValue<T>(int index, out T value)
            where T : IParsable<T>
        {
            var raw = GetValue(index);
            if (raw is null)
            {
                value = default!;
                return false;
            }

            // bool.TryParse only accepts "True"/"False", but KiCad writes "yes"/"no". Match the
            // same set GetValueAsBool already accepts, in both directions, instead of deferring to
            // T.TryParse for this one type.
            if (typeof(T) == typeof(bool))
            {
                var parsedBool = TryParseKiCadBool(raw, out var boolValue);
                value = parsedBool ? (T)(object)boolValue : default!;
                return parsedBool;
            }

            if (T.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// Parses the same boolean spellings <see cref="GetValueAsBool"/> reads: <c>yes</c>/<c>true</c>/<c>1</c>
        /// as true, <c>no</c>/<c>false</c>/<c>0</c> as false (words case-insensitive), anything else unparsed.
        /// </summary>
        private static bool TryParseKiCadBool(string raw, out bool value)
        {
            if (string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "1", StringComparison.Ordinal))
            {
                value = true;
                return true;
            }

            if (string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "0", StringComparison.Ordinal))
            {
                value = false;
                return true;
            }

            value = false;
            return false;
        }

        /// <summary>
        /// Reads the value at <paramref name="index"/> as <typeparamref name="T"/>, or returns <paramref name="fallback"/>.
        /// </summary>
        /// <typeparam name="T">Any parsable type.</typeparam>
        /// <param name="index">The value index.</param>
        /// <param name="fallback">Returned when the value is missing or unparsable.</param>
        /// <returns>The parsed value or the fallback.</returns>
        public T GetValue<T>(int index, T fallback)
            where T : IParsable<T> => TryGetValue<T>(index, out var v) ? v : fallback;

        /// <summary>
        /// Reads the value at <paramref name="index"/> as <typeparamref name="T"/> or throws.
        /// </summary>
        /// <typeparam name="T">Any parsable type.</typeparam>
        /// <param name="index">The value index.</param>
        /// <returns>The parsed value.</returns>
        /// <exception cref="FormatException">The value is missing or does not parse.</exception>
        public T GetRequiredValue<T>(int index = 0)
            where T : IParsable<T> => TryGetValue<T>(index, out var v)
                ? v
                : throw new SExpressionFormatException($"({_token}) has no value at index {index} parsable as {typeof(T).Name}.");

        /// <summary>
        /// Reads a value out of a child form in one call: <c>node.GetChildValue("uuid")</c>.
        /// </summary>
        /// <param name="token">The child token.</param>
        /// <param name="index">The value index inside the child.</param>
        /// <returns>The value, or <see langword="null"/> when the child or value is missing.</returns>
        public string? GetChildValue(string token, int index = 0) => GetChild(token)?.GetValue(index);

        // -------------------------------------------------------------------------- writing values

        /// <summary>
        /// Replaces the value at <paramref name="index"/>, appending atoms when the index is past the end.
        /// </summary>
        /// <param name="index">The value index.</param>
        /// <param name="value">The new text.</param>
        /// <param name="quote">How to write it; <see cref="SQuoteStyle.Auto"/> keeps the style the atom already had.</param>
        /// <returns>This expression, for chaining.</returns>
        public SExpression SetValue(int index, string value, SQuoteStyle quote = SQuoteStyle.Auto)
        {
            ArgumentNullException.ThrowIfNull(value);
            ArgumentOutOfRangeException.ThrowIfNegative(index);

            var seen = 0;
            var items = ItemsSpan;
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].Kind != SItemKind.Atom)
                {
                    continue;
                }

                if (seen++ == index)
                {
                    var style = quote == SQuoteStyle.Auto ? items[i].QuoteStyle : quote;
                    ReplaceItem(i, SItem.CreateAtom(value, style));
                    return this;
                }
            }

            for (var i = seen; i < index; i++)
            {
                AppendValue(string.Empty, SQuoteStyle.Auto);
            }

            AppendValue(value, quote);
            return this;
        }

        /// <summary>
        /// Sets a value on a child form, creating the child when it is missing.
        /// </summary>
        /// <param name="token">The child token.</param>
        /// <param name="value">The new text.</param>
        /// <param name="quote">How to write it.</param>
        /// <returns>The child form.</returns>
        public SExpression SetChildValue(string token, string value, SQuoteStyle quote = SQuoteStyle.Auto)
        {
            var child = GetChild(token);
            if (child is null)
            {
                child = new SExpression(token);
                AddChild(child);
            }

            child.SetValue(0, value, quote);
            return child;
        }

        /// <summary>
        /// Writes a formattable value at <paramref name="index"/> using the invariant culture.
        /// </summary>
        /// <typeparam name="T">Any formattable type, e.g. <see cref="double"/>.</typeparam>
        /// <param name="index">The value index.</param>
        /// <param name="value">The value to write.</param>
        /// <param name="format">An optional format string.</param>
        /// <returns>This expression, for chaining.</returns>
        public SExpression SetValue<T>(int index, T value, string? format = null)
            where T : IFormattable
        {
            ArgumentNullException.ThrowIfNull(value);
            return SetValue(index, value.ToString(format, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Appends one atom to this form, after the last atom it already has.
        /// </summary>
        /// <param name="value">The atom text.</param>
        /// <param name="quote">How to write it.</param>
        /// <returns>This expression, for chaining.</returns>
        public SExpression AddValue(string value, SQuoteStyle quote = SQuoteStyle.Auto)
        {
            AppendValue(value, quote);
            return this;
        }

        /// <summary>
        /// Appends several atoms to this form.
        /// </summary>
        /// <param name="values">The atom texts.</param>
        /// <returns>This expression, for chaining.</returns>
        public SExpression AddValues(params string[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            foreach (var v in values)
            {
                AppendValue(v, SQuoteStyle.Auto);
            }

            return this;
        }

        /// <summary>
        /// Appends a line comment inside this form.
        /// </summary>
        /// <param name="text">The comment text, without the leading <c>#</c>.</param>
        /// <returns>This expression, for chaining.</returns>
        public SExpression AddComment(string text)
        {
            InsertItem(_count, SItem.CreateComment(text));
            return this;
        }

        /// <summary>
        /// Adds a child S-expression to this expression.
        /// </summary>
        /// <param name="child">The child expression to add.</param>
        public void AddChild(SExpression child)
        {
            ArgumentNullException.ThrowIfNull(child);
            InsertItem(_count, SItem.CreateExpression(child));
        }

        /// <summary>
        /// Creates and adds a new child S-expression with the specified token and values.
        /// </summary>
        /// <param name="token">The token for the new child expression.</param>
        /// <param name="values">Values for the new child expression.</param>
        /// <returns>The newly created child expression.</returns>
        public SExpression CreateChild(string token, params string[] values)
        {
            var child = new SExpression(token, values);
            AddChild(child);
            return child;
        }

        /// <summary>
        /// Removes the first child form with the given token.
        /// </summary>
        /// <param name="token">The token to remove.</param>
        /// <returns>True when a child was removed.</returns>
        public bool RemoveChild(string token)
        {
            var items = ItemsSpan;
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].Kind == SItemKind.Expression && string.Equals(items[i].Expression!.Token, token, StringComparison.Ordinal))
                {
                    RemoveItemAt(i);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Removes every child form with the given token.
        /// </summary>
        /// <param name="token">The token to remove.</param>
        /// <returns>How many were removed.</returns>
        public int RemoveChildren(string token)
        {
            var removed = 0;
            for (var i = _count - 1; i >= 0; i--)
            {
                var item = ItemAt(i);
                if (item.Kind == SItemKind.Expression && string.Equals(item.Expression!.Token, token, StringComparison.Ordinal))
                {
                    RemoveItemAt(i);
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// Deep-copies this form. The copy keeps its link to the original source text, so an
        /// untouched copy still writes out byte for byte.
        /// </summary>
        /// <returns>The copy, with no parent.</returns>
        public SExpression Clone()
        {
            // A clone owns its items outright rather than slicing a block: a clone has no document
            // to share one with, and the copy is what makes it independent of the tree it came from.
            var copy = new SExpression(_token)
            {
                Source = Source,
                SourceStart = SourceStart,
                SourceLength = SourceLength,
                _flags = _flags & ~Flag.DocumentContainer,
                _items = _count == 0 ? Array.Empty<SItem>() : new SItem[_count],
                _count = _count,
            };

            for (var i = 0; i < _count; i++)
            {
                var item = ItemAt(i);
                if (item.Kind == SItemKind.Expression)
                {
                    var childCopy = item.Expression!.Clone();
                    childCopy._parent = copy;
                    copy._items[i] = SItem.ParsedExpression(childCopy, item.SourceStart, item.SourceLength);
                }
                else
                {
                    copy._items[i] = item;
                }
            }

            if (IsDocumentContainer)
            {
                copy._flags |= Flag.DocumentContainer;
            }

            return copy;
        }

        /// <summary>
        /// Renders this form to text. A parsed, unmodified form comes back exactly as it was written.
        /// </summary>
        /// <returns>The S-expression text.</returns>
        public string ToText() => new SExpressionWriter().Write(this);

        /// <summary>
        /// Renders this form to text with explicit options.
        /// </summary>
        /// <param name="options">Writer options.</param>
        /// <returns>The S-expression text.</returns>
        public string ToText(SExpressionWriterOptions options) => new SExpressionWriter(options).Write(this);

        /// <summary>
        /// Returns a short debugging representation of this S-expression.
        /// </summary>
        /// <returns>The token and its values.</returns>
        public override string ToString() => $"({_token} {string.Join(" ", Values)})";

        // ------------------------------------------------------------------------- item plumbing

        internal SExpression? GetFirstChild()
        {
            foreach (var item in ItemsSpan)
            {
                if (item.Kind == SItemKind.Expression)
                {
                    return item.Expression;
                }
            }

            return null;
        }

        internal int CountOf(SItemKind kind)
        {
            var n = 0;
            foreach (var item in ItemsSpan)
            {
                if (item.Kind == kind)
                {
                    n++;
                }
            }

            return n;
        }

        internal void AppendValue(string value, SQuoteStyle quote)
        {
            ArgumentNullException.ThrowIfNull(value);
            var at = 0;
            var items = ItemsSpan;
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].Kind == SItemKind.Atom)
                {
                    at = i + 1;
                }
            }

            InsertItem(at, SItem.CreateAtom(value, quote));
        }

        /// <summary>
        /// Inserts an item, copying this form out of any block it was sharing.
        /// </summary>
        /// <remarks>
        /// A parsed form's slice sits inside a block whose neighbouring slots belong to other forms,
        /// so growing in place is not an option: the copy into an exactly-sized array this form owns
        /// alone is what makes the insert legal, and it is the same copy this did before slices
        /// existed. From here on the form is detached from the block -- it keeps no claim on it, and
        /// the block keeps none on the form.
        /// </remarks>
        internal void InsertItem(int index, SItem item)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _count);
            Attach(item);

            var current = ItemsSpan;
            var next = new SItem[_count + 1];
            current[..index].CopyTo(next);
            next[index] = item;
            current[index..].CopyTo(next.AsSpan(index + 1));
            _items = next;
            _offset = 0;
            _count = next.Length;

            _flags |= Flag.ItemsChanged;
            MarkDirty();
        }

        internal void RemoveItemAt(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            var current = ItemsSpan;
            Detach(current[index]);

            var next = _count == 1 ? Array.Empty<SItem>() : new SItem[_count - 1];
            current[..index].CopyTo(next);
            current[(index + 1)..].CopyTo(next.AsSpan(index));
            _items = next;
            _offset = 0;
            _count = next.Length;

            _flags |= Flag.ItemsChanged;
            MarkDirty();
        }

        internal void ReplaceItem(int index, SItem item)
        {
            var old = _items[_offset + index];
            if (old.RawValid && old == item)
            {
                return;
            }

            Detach(old);
            Attach(item);

            // Written straight into the block: the slot belongs to this form and to no other, so a
            // replace needs no copy out of it. Keep the old item's slot so the writer can still
            // splice the whitespace around it and change nothing but this one atom.
            _items[_offset + index] = old.IsFromSource ? item.InSlotOf(old) : item;
            MarkDirty();
        }

        internal void ClearItems()
        {
            foreach (var item in ItemsSpan)
            {
                Detach(item);
            }

            _items = Array.Empty<SItem>();
            _offset = 0;
            _count = 0;
            _flags |= Flag.ItemsChanged;
            MarkDirty();
        }

        /// <summary>
        /// Installs the item slice and source span the parser produced. No dirty marking.
        /// </summary>
        /// <param name="block">The document's item block. Shared with every other parsed form in it.</param>
        /// <param name="offset">Where this form's items start in <paramref name="block"/>.</param>
        /// <param name="count">How many items this form holds.</param>
        /// <param name="source">The text this form was parsed from.</param>
        /// <param name="start">Where this form starts in that text.</param>
        /// <param name="length">How long this form is in that text.</param>
        internal void SetParsed(SItem[] block, int offset, int count, string? source, int start, int length)
        {
            _items = block;
            _offset = offset;
            _count = count;
            Source = source;
            SourceStart = start;
            SourceLength = length;
        }

        /// <summary>Parent link set by the parser as it harvests a form's items.</summary>
        internal void SetParsedParent(SExpression parent) => _parent = parent;

        private void Attach(SItem item)
        {
            if (item.Kind == SItemKind.Expression)
            {
                var child = item.Expression!;
                child._parent?.DetachChildReference(child);
                child._parent = this;
            }
        }

        private void Detach(SItem item)
        {
            if (item.Kind == SItemKind.Expression && ReferenceEquals(item.Expression!._parent, this))
            {
                item.Expression._parent = null;
            }
        }

        private void DetachChildReference(SExpression child)
        {
            var items = ItemsSpan;
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].Kind == SItemKind.Expression && ReferenceEquals(items[i].Expression, child))
                {
                    RemoveItemAt(i);
                    return;
                }
            }
        }

        private void MarkDirty()
        {
            var node = this;
            while (node is not null && (node._flags & Flag.Dirty) == 0)
            {
                node._flags |= Flag.Dirty;
                node = node._parent;
            }
        }
    }
}
