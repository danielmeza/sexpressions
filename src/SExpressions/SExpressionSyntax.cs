using System;
using System.Buffers;
using System.Text;

namespace SExpressions
{
    /// <summary>
    /// The scanning rules the tree parser and the streaming reader share.
    /// </summary>
    /// <remarks>
    /// One place on purpose. A streaming reader has to split a document exactly the way
    /// <see cref="SExpressionParser"/> does, or a
    /// consumer that inspects a file with one and edits it with the other is looking at two different
    /// documents -- and the difference would show up as a byte that moved, which is the one thing this
    /// library promises will not happen. The <see cref="SearchValues{T}"/> tables are also the
    /// expensive part to build, and this way they are built once for the process rather than once per
    /// type.
    /// </remarks>
    internal static class SExpressionSyntax
    {
        /// <summary>Everything that ends a bare atom. Vectorised by <see cref="SearchValues"/>.</summary>
        internal static readonly SearchValues<char> Delimiters = SearchValues.Create(" \t\r\n()");

        internal static readonly SearchValues<char> Whitespace = SearchValues.Create(" \t\r\n");

        internal static readonly SearchValues<char> QuoteOrEscape = SearchValues.Create("\"\\");

        internal static readonly SearchValues<char> LineBreak = SearchValues.Create("\r\n");

        /// <summary>
        /// Default options, for a reader constructed without any. Never handed out, so nothing can
        /// mutate it.
        /// </summary>
        internal static readonly SExpressionParserOptions Defaults = new();

        /// <summary>Moves <paramref name="pos"/> past any run of whitespace.</summary>
        /// <param name="s">The text.</param>
        /// <param name="pos">The position to advance.</param>
        internal static void SkipWhitespace(ReadOnlySpan<char> s, ref int pos)
        {
            if (pos >= s.Length)
            {
                return;
            }

            var rel = s[pos..].IndexOfAnyExcept(Whitespace);
            pos = rel < 0 ? s.Length : pos + rel;
        }

        /// <summary>The index just past the bare atom starting at <paramref name="pos"/>.</summary>
        /// <param name="s">The text.</param>
        /// <param name="pos">Where the atom starts.</param>
        /// <returns>The index of the delimiter that ends it, or the end of the text.</returns>
        internal static int EndOfBareAtom(ReadOnlySpan<char> s, int pos)
        {
            var rel = s[pos..].IndexOfAny(Delimiters);
            return rel < 0 ? s.Length : pos + rel;
        }

        /// <summary>
        /// Decodes the escapes in the content of a quoted atom.
        /// </summary>
        /// <param name="value">The content, without its surrounding quotes.</param>
        /// <returns>The decoded text.</returns>
        /// <remarks>
        /// Only the two escapes KiCad actually emits are decoded, so encoding them again is an exact
        /// inverse. Anything else keeps its backslash and stays as written.
        /// </remarks>
        internal static string Unescape(ReadOnlySpan<char> value)
        {
            var sb = new StringBuilder(value.Length);
            var i = 0;
            while (i < value.Length)
            {
                var c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    var next = value[i + 1];
                    if (next is '\\' or '"')
                    {
                        sb.Append(next);
                    }
                    else
                    {
                        sb.Append(c).Append(next);
                    }

                    i += 2;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Parses the boolean spellings this format uses: <c>yes</c>/<c>true</c>/<c>1</c> as true,
        /// <c>no</c>/<c>false</c>/<c>0</c> as false (words case-insensitive), anything else unparsed.
        /// </summary>
        /// <param name="raw">The text.</param>
        /// <param name="value">The parsed value.</param>
        /// <returns>True when it parsed.</returns>
        internal static bool TryParseBool(ReadOnlySpan<char> raw, out bool value)
        {
            if (raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("1", StringComparison.Ordinal))
            {
                value = true;
                return true;
            }

            if (raw.Equals("no", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("0", StringComparison.Ordinal))
            {
                value = false;
                return true;
            }

            value = false;
            return false;
        }
    }
}
