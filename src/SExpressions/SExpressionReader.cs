using System;
using System.Globalization;

namespace SExpressions
{
    /// <summary>
    /// What an <see cref="SExpressionReader"/> is currently positioned on.
    /// </summary>
    public enum SExpressionTokenType : byte
    {
        /// <summary>Nothing yet: the reader has not been advanced, or the input is exhausted.</summary>
        None = 0,

        /// <summary>
        /// The <c>(</c> and the token that opens a form. <see cref="SExpressionReader.ValueSpan"/> is
        /// the token itself, without the paren.
        /// </summary>
        StartForm = 1,

        /// <summary>The <c>)</c> that closes a form. The value is empty.</summary>
        EndForm = 2,

        /// <summary>A bare or quoted atom. <see cref="SExpressionReader.QuoteStyle"/> says which.</summary>
        Atom = 3,

        /// <summary>A line comment, without its leading prefix.</summary>
        Comment = 4,
    }

    /// <summary>
    /// A forward-only reader over S-expression text that builds no tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is an <em>additional</em> API, not a replacement for <see cref="SExpressionParser"/>. It
    /// exists for the read-only path -- a consumer that walks a document once, copies a handful of
    /// fields into its own records and throws the rest away. For that shape the whole tree is
    /// intermediate garbage, which is exactly what a streaming reader removes. Anything that mutates
    /// a document, or that needs the byte-identical write back, still needs the tree: the writer
    /// reproduces a form by copying the source span the tree remembers.
    /// </para>
    /// <para>
    /// <strong>"Zero allocation" means no allocation proportional to the input</strong>, not literally
    /// none. The reader itself allocates nothing at all -- it is a <c>ref struct</c> over a
    /// <see cref="ReadOnlySpan{T}"/> and every token it reports is a slice of that span. What the
    /// caller then does with a token is the caller's cost: <see cref="GetString"/> allocates one
    /// string, and a consumer that keeps every atom will allocate as much as the tree did. Use
    /// <see cref="ValueEquals(ReadOnlySpan{char})"/> and <see cref="TryGetValue{T}"/> where the value
    /// is only being tested or parsed, and <see cref="GetString"/> only for what is being kept.
    /// </para>
    /// <para>
    /// The reader does not intern. A 2-way atom cache was built, measured and REJECTED in PR #17 --
    /// 909 to 723 strings per parse for 3% more time -- and interning here would cost the same
    /// without a tree to amortise it over.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var reader = new SExpressionReader(text);
    /// while (reader.Read())
    /// {
    ///     if (reader.TokenType == SExpressionTokenType.StartForm &amp;&amp; reader.ValueEquals("property"))
    ///     {
    ///         reader.Read();                      // the property name
    ///         var name = reader.GetString();
    ///         reader.Read();                      // its value
    ///         var value = reader.GetString();
    ///     }
    /// }
    /// </code>
    /// </example>
    public ref struct SExpressionReader
    {
        private readonly ReadOnlySpan<char> _text;
        private readonly char _commentPrefix;
        private readonly bool _hasComments;
        private readonly int _maxDepth;

        private int _pos;
        private int _depth;
        private int _tokenStart;
        private int _valueStart;
        private int _valueLength;
        private SExpressionTokenType _tokenType;
        private SQuoteStyle _quoteStyle;
        private bool _valueIsEscaped;

        /// <summary>Creates a reader over <paramref name="text"/> with the default options.</summary>
        /// <param name="text">The S-expression text.</param>
        public SExpressionReader(ReadOnlySpan<char> text)
            : this(text, null)
        {
        }

        /// <summary>Creates a reader over <paramref name="text"/>.</summary>
        /// <param name="text">The S-expression text.</param>
        /// <param name="options">
        /// Options, or <see langword="null"/> for the defaults. Only <see cref="SExpressionParserOptions.CommentPrefix"/>
        /// and <see cref="SExpressionParserOptions.MaxDepth"/> apply: there is no tree to track a
        /// source span on, and nothing to pool strings into.
        /// </param>
        public SExpressionReader(ReadOnlySpan<char> text, SExpressionParserOptions? options)
        {
            _text = text;
            var o = options ?? SExpressionSyntax.Defaults;
            _hasComments = o.CommentPrefix.HasValue;
            _commentPrefix = o.CommentPrefix ?? '\0';
            _maxDepth = o.MaxDepth;
        }

        /// <summary>Gets what the reader is currently positioned on.</summary>
        public readonly SExpressionTokenType TokenType => _tokenType;

        /// <summary>
        /// Gets how deeply nested the current token is. A top-level form's
        /// <see cref="SExpressionTokenType.StartForm"/> and its matching
        /// <see cref="SExpressionTokenType.EndForm"/> are both depth 1, and everything directly inside
        /// it is depth 2.
        /// </summary>
        public readonly int Depth => _depth;

        /// <summary>
        /// Gets the current token's text: the token name of a form, the content of an atom without its
        /// quotes, or a comment without its prefix. Empty for
        /// <see cref="SExpressionTokenType.EndForm"/>.
        /// </summary>
        /// <remarks>
        /// This is a slice of the source. It is NOT unescaped -- see <see cref="ValueIsEscaped"/> --
        /// so compare with <see cref="ValueEquals(ReadOnlySpan{char})"/> rather than with
        /// <c>SequenceEqual</c> unless you have checked that flag.
        /// </remarks>
        public readonly ReadOnlySpan<char> ValueSpan => _text.Slice(_valueStart, _valueLength);

        /// <summary>Gets how the current atom was written.</summary>
        public readonly SQuoteStyle QuoteStyle => _quoteStyle;

        /// <summary>
        /// True when <see cref="ValueSpan"/> still contains a backslash escape that
        /// <see cref="GetString"/> would decode. False for every bare atom and for the overwhelming
        /// majority of quoted ones.
        /// </summary>
        public readonly bool ValueIsEscaped => _valueIsEscaped;

        /// <summary>Gets where the current token starts in the source, including its <c>(</c>, quote or comment prefix.</summary>
        public readonly int TokenStartIndex => _tokenStart;

        /// <summary>Gets how far the reader has consumed.</summary>
        public readonly int Position => _pos;

        /// <summary>
        /// Advances to the next token.
        /// </summary>
        /// <returns>False at the end of the input.</returns>
        /// <exception cref="SExpressionFormatException">The text is not well formed.</exception>
        public bool Read()
        {
            _valueIsEscaped = false;
            _quoteStyle = SQuoteStyle.Auto;

            SExpressionSyntax.SkipWhitespace(_text, ref _pos);
            if (_pos >= _text.Length)
            {
                if (_depth != 0)
                {
                    throw new SExpressionFormatException("Unexpected end of input; expected ')'");
                }

                _tokenType = SExpressionTokenType.None;
                _valueStart = 0;
                _valueLength = 0;
                return false;
            }

            _tokenStart = _pos;
            var c = _text[_pos];

            if (c == '(')
            {
                _pos++;
                SExpressionSyntax.SkipWhitespace(_text, ref _pos);
                var start = _pos;
                _pos = SExpressionSyntax.EndOfBareAtom(_text, _pos);
                _valueStart = start;
                _valueLength = _pos - start;
                _depth++;
                if (_depth > _maxDepth)
                {
                    throw new SExpressionFormatException($"Nesting deeper than {_maxDepth}");
                }

                _tokenType = SExpressionTokenType.StartForm;
                return true;
            }

            if (c == ')')
            {
                if (_depth == 0)
                {
                    throw new SExpressionFormatException("Unbalanced ')'");
                }

                _pos++;
                _depth--;
                _valueStart = _tokenStart;
                _valueLength = 0;
                _tokenType = SExpressionTokenType.EndForm;
                return true;
            }

            if (_hasComments && c == _commentPrefix)
            {
                var lineEnd = _text[_pos..].IndexOfAny(SExpressionSyntax.LineBreak);
                var end = lineEnd < 0 ? _text.Length : _pos + lineEnd;
                _valueStart = _pos + 1;
                _valueLength = end - _pos - 1;
                _pos = end;
                _tokenType = SExpressionTokenType.Comment;
                return true;
            }

            if (c == '"')
            {
                ReadQuoted();
                _tokenType = SExpressionTokenType.Atom;
                return true;
            }

            _pos = SExpressionSyntax.EndOfBareAtom(_text, _pos);
            _valueStart = _tokenStart;
            _valueLength = _pos - _tokenStart;
            _quoteStyle = SQuoteStyle.Bare;
            _tokenType = SExpressionTokenType.Atom;
            return true;
        }

        /// <summary>
        /// Reads past the rest of the form the reader is inside, leaving it positioned on that form's
        /// <see cref="SExpressionTokenType.EndForm"/>.
        /// </summary>
        /// <remarks>
        /// Call this straight after a <see cref="SExpressionTokenType.StartForm"/> to ignore a subtree
        /// without materialising any of it. This is where a streaming read beats the tree by the
        /// widest margin: the whole subtree costs one scan and no allocation at all.
        /// </remarks>
        /// <returns>False if the input ended first.</returns>
        public bool SkipForm()
        {
            if (_tokenType != SExpressionTokenType.StartForm)
            {
                return false;
            }

            var target = _depth - 1;
            while (Read())
            {
                if (_tokenType == SExpressionTokenType.EndForm && _depth == target)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Advances to the next direct child form of the form the reader is inside whose token is
        /// <paramref name="token"/>, skipping over anything else at that level.
        /// </summary>
        /// <param name="token">The token to look for.</param>
        /// <returns>
        /// True with the reader positioned on that child's <see cref="SExpressionTokenType.StartForm"/>;
        /// false when the enclosing form ends first, with the reader on its
        /// <see cref="SExpressionTokenType.EndForm"/>.
        /// </returns>
        /// <remarks>
        /// This is the whole query layer, and deliberately so: it is what "give me this child" needs,
        /// it composes (call it again for a grandchild), and anything richer would be a tree by
        /// another name.
        /// </remarks>
        public bool TryReadChild(ReadOnlySpan<char> token)
        {
            var enclosing = _depth;
            while (Read())
            {
                if (_tokenType == SExpressionTokenType.EndForm && _depth < enclosing)
                {
                    return false;
                }

                if (_tokenType == SExpressionTokenType.StartForm && _depth == enclosing + 1)
                {
                    if (ValueEquals(token))
                    {
                        return true;
                    }

                    SkipForm();
                }
            }

            return false;
        }

        /// <summary>
        /// Compares <see cref="ValueSpan"/> with <paramref name="other"/>, decoding escapes on the way
        /// if there are any. Allocates nothing either way.
        /// </summary>
        /// <param name="other">The text to compare against.</param>
        /// <returns>True when they are the same text.</returns>
        public readonly bool ValueEquals(ReadOnlySpan<char> other)
        {
            var value = ValueSpan;
            if (!_valueIsEscaped)
            {
                return value.SequenceEqual(other);
            }

            var i = 0;
            var j = 0;
            while (i < value.Length && j < other.Length)
            {
                var c = value[i];
                if (c == '\\' && i + 1 < value.Length && (value[i + 1] == '\\' || value[i + 1] == '"'))
                {
                    c = value[i + 1];
                    i += 2;
                }
                else
                {
                    i++;
                }

                if (c != other[j++])
                {
                    return false;
                }
            }

            return i == value.Length && j == other.Length;
        }

        /// <summary>
        /// Materialises the current value as a <see cref="string"/>, decoding escapes.
        /// </summary>
        /// <returns>The text.</returns>
        /// <remarks>This is the one call here that allocates. Use it for what you keep, not to look.</remarks>
        public readonly string GetString()
        {
            var value = ValueSpan;
            if (!_valueIsEscaped)
            {
                return value.IsEmpty ? string.Empty : new string(value);
            }

            return SExpressionSyntax.Unescape(value);
        }

        /// <summary>
        /// Reads the current value as <typeparamref name="T"/>, invariant culture.
        /// </summary>
        /// <typeparam name="T">Any span-parsable type, e.g. <see cref="double"/> or <see cref="int"/>.</typeparam>
        /// <param name="value">The parsed value.</param>
        /// <returns>True when it parsed.</returns>
        /// <remarks>
        /// An escaped value never parses as a number, so it is rejected rather than decoded: a
        /// backslash cannot appear in any of the numeric formats and decoding one to find that out
        /// would allocate for nothing. <see cref="bool"/> reads the same <c>yes</c>/<c>no</c> spellings
        /// <see cref="SExpression.GetValueAsBool"/> accepts.
        /// </remarks>
        public readonly bool TryGetValue<T>(out T value)
            where T : ISpanParsable<T>
        {
            if (_valueIsEscaped)
            {
                value = default!;
                return false;
            }

            if (typeof(T) == typeof(bool))
            {
                var parsed = SExpressionSyntax.TryParseBool(ValueSpan, out var b);
                value = parsed ? (T)(object)b : default!;
                return parsed;
            }

            if (T.TryParse(ValueSpan, CultureInfo.InvariantCulture, out var result))
            {
                value = result;
                return true;
            }

            value = default!;
            return false;
        }

        private void ReadQuoted()
        {
            var contentStart = _pos + 1;
            var rel = _text[contentStart..].IndexOfAny(SExpressionSyntax.QuoteOrEscape);
            if (rel < 0)
            {
                throw new SExpressionFormatException("Unterminated quoted string");
            }

            var i = contentStart + rel;
            if (_text[i] == '"')
            {
                _valueStart = contentStart;
                _valueLength = i - contentStart;
                _pos = i + 1;
                _quoteStyle = SQuoteStyle.Quoted;
                return;
            }

            // An escape: scan to the closing quote, leaving the escapes in place. The caller decodes
            // only if it keeps the value.
            while (i < _text.Length)
            {
                if (_text[i] == '"')
                {
                    _valueStart = contentStart;
                    _valueLength = i - contentStart;
                    _pos = i + 1;
                    _quoteStyle = SQuoteStyle.Quoted;
                    _valueIsEscaped = true;
                    return;
                }

                i += _text[i] == '\\' && i + 1 < _text.Length ? 2 : 1;
            }

            throw new SExpressionFormatException("Unterminated quoted string");
        }
    }
}
