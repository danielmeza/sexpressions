using System;
using System.Collections;
using System.Collections.Generic;

namespace SExpressions
{
    /// <summary>
    /// The ordered contents of a form: atoms, nested forms and comments as they appear in the source.
    /// Mutating this list is what every other view ultimately does.
    /// </summary>
    /// <remarks>A view, not storage: it holds one reference and costs nothing to obtain.</remarks>
    public readonly struct SItemList : IList<SItem>, IReadOnlyList<SItem>
    {
        private readonly SExpression _owner;

        internal SItemList(SExpression owner) => _owner = owner;

        /// <summary>
        /// The form this view belongs to. A view is only ever valid when it came from
        /// <see cref="SExpression"/>; a default-constructed one (which a C# collection expression can
        /// produce, because this is a struct with an <c>Add</c> method) has nothing to act on.
        /// </summary>
        private SExpression Owner => _owner ?? throw new InvalidOperationException(
            "This view is not attached to an expression. Get it from SExpression.Values, .Children or .Items instead of constructing one.");


        /// <inheritdoc />
        public int Count => Owner.ItemCount;

        /// <inheritdoc />
        public bool IsReadOnly => false;

        /// <summary>Gets or sets the item at <paramref name="index"/>.</summary>
        /// <param name="index">The item index.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is negative, or not less than <see cref="Count"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Setting it to a form that is this form, or one it is nested in.
        /// </exception>
        /// <remarks>
        /// Setting a child form that is already an item of this form moves it into the place of the
        /// item at <paramref name="index"/>, which leaves the tree; the list is then one item shorter,
        /// and the form sits at <paramref name="index"/> - 1 when it came from in front of it. See
        /// <see cref="SChildCollection.this[int]"/>.
        /// </remarks>
        public SItem this[int index]
        {
            get => Owner.ItemsSpan[index];
            set => Owner.ReplaceItem(index, value);
        }

        /// <summary>Appends an item after every item the form holds.</summary>
        /// <param name="item">The item. A child form moves out of wherever it was.</param>
        /// <remarks>
        /// A child form of this same form moves to the end, and nothing changes if it is already the
        /// last item. See <see cref="Insert"/>.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="item"/> holds this form, or a form it is nested in.
        /// </exception>
        public void Add(SItem item) => Owner.InsertItem(Owner.ItemCount, item);

        /// <summary>Inserts an item at <paramref name="index"/>.</summary>
        /// <param name="index">From 0 to <see cref="Count"/>.</param>
        /// <param name="item">The item. A child form moves out of wherever it was.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is negative or greater than <see cref="Count"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="item"/> holds this form, or a form it is nested in.
        /// </exception>
        /// <remarks>
        /// <para>
        /// A child form belongs to one form at a time, so inserting one that another form holds, in
        /// this document or another, takes it out of that form. Insert a
        /// <see cref="SExpression.Clone"/> to leave the original where it is.
        /// </para>
        /// <para>
        /// A child form of this same form is moved, not duplicated. <paramref name="index"/> is where
        /// it ends up, read in the list without it, the way <c>ObservableCollection&lt;T&gt;.Move</c>
        /// reads its new index; <see cref="Count"/> still means the end. So on <c>[a, b, c]</c>,
        /// <c>Insert(2, a)</c> and <c>Insert(3, a)</c> both give <c>[b, c, a]</c>, and
        /// <c>Insert(0, c)</c> gives <c>[c, a, b]</c>. When that is where it already is, nothing
        /// changes, and the file still saves byte for byte.
        /// </para>
        /// <para>
        /// A moved form is written as one moved in from another form would be: the whitespace in
        /// front of it goes with it, the separator where it lands is copied from its new neighbours,
        /// and its own text is untouched.
        /// </para>
        /// <para>
        /// An item taken from a parse -- this document's or another's -- is written from what it
        /// holds, not from where it was parsed: its atom or comment text, and the quoting it arrived
        /// with, behind a separator copied from its new neighbours.
        /// </para>
        /// </remarks>
        public void Insert(int index, SItem item) => Owner.InsertItem(index, item);

        /// <inheritdoc />
        public void RemoveAt(int index) => Owner.RemoveItemAt(index);

        /// <inheritdoc />
        public void Clear() => Owner.ClearItems();

        /// <inheritdoc />
        public bool Contains(SItem item) => IndexOf(item) >= 0;

        /// <inheritdoc />
        public int IndexOf(SItem item) => Owner.ItemsSpan.IndexOf(item);

        /// <inheritdoc />
        public void CopyTo(SItem[] array, int arrayIndex)
        {
            // Checked here rather than left to the span: AsSpan on a null array is an empty span, so
            // without this a null destination would report "destination too short".
            ArgumentNullException.ThrowIfNull(array);
            Owner.ItemsSpan.CopyTo(array.AsSpan(arrayIndex));
        }

        /// <inheritdoc />
        public bool Remove(SItem item)
        {
            var i = IndexOf(item);
            if (i < 0)
            {
                return false;
            }

            RemoveAt(i);
            return true;
        }

        /// <summary>Walks the items the form holds at the moment this is called.</summary>
        /// <returns>An enumerator over those items.</returns>
        /// <remarks>
        /// The loop may add, remove or move items; the walk still visits each item that was there
        /// when it started, once, in order. See <see cref="SExpression"/>.
        /// </remarks>
        public IEnumerator<SItem> GetEnumerator()
        {
            var owner = Owner;
            return Walk(owner.ItemArray, owner.ItemOffset, owner.ItemCount);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Gets the items as a span, for allocation-free iteration.</summary>
        /// <returns>The span.</returns>
        public ReadOnlySpan<SItem> AsSpan() => Owner.ItemsSpan;

        private static IEnumerator<SItem> Walk(SItem[] items, int start, int count)
        {
            var end = start + count;
            for (var i = start; i < end; i++)
            {
                yield return items[i];
            }
        }
    }

    /// <summary>
    /// A live view of a form's atoms as strings. Source-compatible with the plain
    /// <c>List&lt;string&gt;</c> this used to be, but backed by the ordered item list.
    /// </summary>
    public readonly struct SValueCollection : IList<string>, IReadOnlyList<string>
    {
        private readonly SExpression _owner;

        internal SValueCollection(SExpression owner) => _owner = owner;

        /// <summary>
        /// The form this view belongs to. A view is only ever valid when it came from
        /// <see cref="SExpression"/>; a default-constructed one (which a C# collection expression can
        /// produce, because this is a struct with an <c>Add</c> method) has nothing to act on.
        /// </summary>
        private SExpression Owner => _owner ?? throw new InvalidOperationException(
            "This view is not attached to an expression. Get it from SExpression.Values, .Children or .Items instead of constructing one.");


        /// <inheritdoc />
        public int Count => Owner.CountOf(SItemKind.Atom);

        /// <inheritdoc />
        public bool IsReadOnly => false;

        /// <inheritdoc />
        public string this[int index]
        {
            get => Owner.GetValue(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
            set => Owner.SetValue(index, value);
        }

        /// <inheritdoc />
        public void Add(string item) => Owner.AppendValue(item, SQuoteStyle.Auto);

        /// <summary>Appends an atom with an explicit quoting style.</summary>
        /// <param name="item">The atom text.</param>
        /// <param name="quote">How to write it back out.</param>
        public void Add(string item, SQuoteStyle quote) => Owner.AppendValue(item, quote);

        /// <summary>Appends several atoms.</summary>
        /// <param name="items">The atom texts.</param>
        public void AddRange(IEnumerable<string> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            foreach (var i in items)
            {
                Owner.AppendValue(i, SQuoteStyle.Auto);
            }
        }

        /// <summary>Inserts an atom at <paramref name="index"/> among the atoms.</summary>
        /// <param name="index">
        /// From 0 to <see cref="Count"/>. <see cref="Count"/> puts the atom right after the last
        /// atom, as <see cref="Add(string)"/> does.
        /// </param>
        /// <param name="item">The atom text.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is negative or greater than <see cref="Count"/>.
        /// </exception>
        public void Insert(int index, string item)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);
            var itemIndex = ItemIndexOf(index);
            if (itemIndex < 0)
            {
                Owner.AppendValue(item, SQuoteStyle.Auto);
                return;
            }

            Owner.InsertItem(itemIndex, SItem.CreateAtom(item));
        }

        /// <inheritdoc />
        public void RemoveAt(int index)
        {
            var itemIndex = ItemIndexOf(index);
            if (itemIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            Owner.RemoveItemAt(itemIndex);
        }

        /// <inheritdoc />
        public void Clear()
        {
            for (var i = Owner.ItemCount - 1; i >= 0; i--)
            {
                if (Owner.ItemAt(i).Kind == SItemKind.Atom)
                {
                    Owner.RemoveItemAt(i);
                }
            }
        }

        /// <inheritdoc />
        public int IndexOf(string item)
        {
            var seen = 0;
            foreach (var i in Owner.ItemsSpan)
            {
                if (i.Kind != SItemKind.Atom)
                {
                    continue;
                }

                if (string.Equals(i.Text, item, StringComparison.Ordinal))
                {
                    return seen;
                }

                seen++;
            }

            return -1;
        }

        /// <inheritdoc />
        public bool Contains(string item) => IndexOf(item) >= 0;

        /// <inheritdoc />
        public void CopyTo(string[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            foreach (var v in this)
            {
                array[arrayIndex++] = v;
            }
        }

        /// <inheritdoc />
        public bool Remove(string item)
        {
            var i = IndexOf(item);
            if (i < 0)
            {
                return false;
            }

            RemoveAt(i);
            return true;
        }

        /// <summary>Walks the atoms the form holds at the moment this is called.</summary>
        /// <returns>An enumerator over those atoms.</returns>
        /// <remarks>
        /// The loop may add or remove atoms, and <c>values.AddRange(values)</c> appends each atom
        /// once. See <see cref="SExpression"/>.
        /// </remarks>
        public IEnumerator<string> GetEnumerator()
        {
            var owner = Owner;
            return Walk(owner.ItemArray, owner.ItemOffset, owner.ItemCount);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private static IEnumerator<string> Walk(SItem[] items, int start, int count)
        {
            var end = start + count;
            for (var i = start; i < end; i++)
            {
                if (items[i].Kind == SItemKind.Atom)
                {
                    yield return items[i].Text!;
                }
            }
        }

        private int ItemIndexOf(int valueIndex)
        {
            var seen = 0;
            var items = Owner.ItemsSpan;
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].Kind == SItemKind.Atom && seen++ == valueIndex)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// A live view of a form's nested forms. Source-compatible with the plain
    /// <c>List&lt;SExpression&gt;</c> this used to be.
    /// </summary>
    public readonly struct SChildCollection : IList<SExpression>, IReadOnlyList<SExpression>
    {
        private readonly SExpression _owner;

        internal SChildCollection(SExpression owner) => _owner = owner;

        /// <summary>
        /// The form this view belongs to. A view is only ever valid when it came from
        /// <see cref="SExpression"/>; a default-constructed one (which a C# collection expression can
        /// produce, because this is a struct with an <c>Add</c> method) has nothing to act on.
        /// </summary>
        private SExpression Owner => _owner ?? throw new InvalidOperationException(
            "This view is not attached to an expression. Get it from SExpression.Values, .Children or .Items instead of constructing one.");


        /// <inheritdoc />
        public int Count => Owner.CountOf(SItemKind.Expression);

        /// <inheritdoc />
        public bool IsReadOnly => false;

        /// <summary>Gets or sets the child form at <paramref name="index"/>.</summary>
        /// <param name="index">The child index.</param>
        /// <remarks>
        /// <para>
        /// Setting replaces that child, which leaves the tree, with the form given, which moves out of
        /// wherever it was. The form takes the replaced child's place in the file, keeping the
        /// whitespace in front of it.
        /// </para>
        /// <para>
        /// A form that is already a child here is moved the same way: the child replaced is the
        /// one at <paramref name="index"/> when you set it, the form closes the gap it leaves behind,
        /// and the list is one shorter. So on <c>[a, b, c]</c>, <c>this[2] = a</c> gives
        /// <c>[b, a]</c>, with <c>a</c> at index 1, and <c>this[0] = c</c> gives <c>[c, b]</c>.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Setting it to this form, or to a form it is nested in.
        /// </exception>
        public SExpression this[int index]
        {
            get
            {
                var i = ItemIndexOf(index);
                return i >= 0 ? Owner.ItemAt(i).Expression! : throw new ArgumentOutOfRangeException(nameof(index));
            }

            set
            {
                var i = ItemIndexOf(index);
                if (i < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                Owner.ReplaceItem(i, SItem.CreateExpression(value));
            }
        }

        /// <summary>Appends a child form after every item the form holds, moving it out of wherever it was.</summary>
        /// <param name="item">The form to append.</param>
        /// <remarks>
        /// A child of this same form moves to the end, and nothing changes if it is already the last
        /// item. See <see cref="SExpression.AddChild"/>.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="item"/> is this form, or a form it is nested in.
        /// </exception>
        public void Add(SExpression item) => Owner.AddChild(item);

        /// <summary>Appends several child forms, in order, each moving out of wherever it was.</summary>
        /// <param name="items">The forms to append.</param>
        /// <remarks>
        /// <paramref name="items"/> is read in full before the first form is added. Adding a form
        /// moves it, so a lazy sequence over the children of the form it comes from, such as
        /// <c>node.Children.AddRange(node.GetChildren("symbol"))</c> or
        /// <c>other.Children.AddRange(node.Children)</c>, would otherwise skip some of them and, over
        /// this form's own children, visit some twice.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// One of <paramref name="items"/> is this form, or a form it is nested in. None has moved.
        /// </exception>
        public void AddRange(IEnumerable<SExpression> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            var owner = Owner;
            var forms = new List<SExpression>(items);

            // All of them checked before the first moves, so a refusal leaves everything in place.
            foreach (var form in forms)
            {
                ArgumentNullException.ThrowIfNull(form, nameof(items));
                owner.ThrowIfAncestorOrSelf(form);
            }

            foreach (var form in forms)
            {
                owner.AddChild(form);
            }
        }

        /// <summary>
        /// Inserts a child form at <paramref name="index"/> among the child forms, moving it out of
        /// wherever it was.
        /// </summary>
        /// <param name="index">
        /// From 0 to <see cref="Count"/>. <see cref="Count"/> puts the form after every item the form
        /// holds.
        /// </param>
        /// <param name="item">The form to insert.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is negative or greater than <see cref="Count"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="item"/> is this form, or a form it is nested in.
        /// </exception>
        /// <remarks>
        /// <para>
        /// A form belongs to one parent at a time, so one that another form holds, in this document
        /// or another, leaves it. Insert <see cref="SExpression.Clone"/> to keep the original.
        /// </para>
        /// <para>
        /// A form that is already a child here is moved, not duplicated.
        /// <paramref name="index"/> is the child index it ends up at, read among the children
        /// without it, the way <c>ObservableCollection&lt;T&gt;.Move</c> reads its new index;
        /// <see cref="Count"/> still means the end. So on <c>[a, b, c]</c>, <c>Insert(2, a)</c> and
        /// <c>Insert(3, a)</c> both give <c>[b, c, a]</c>, and <c>Insert(0, c)</c> gives
        /// <c>[c, a, b]</c>. When the index is the one it already has, nothing changes, and the file
        /// still saves byte for byte. Otherwise it lands where a new form inserted into the list
        /// without it would, and is written as one moved in from another form: the whitespace in
        /// front of it goes with it, the separator where it lands is copied from its new neighbours,
        /// and its own text is untouched.
        /// </para>
        /// </remarks>
        public void Insert(int index, SExpression item) => Owner.InsertChild(index, item);

        /// <inheritdoc />
        public void RemoveAt(int index)
        {
            var i = ItemIndexOf(index);
            if (i < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            Owner.RemoveItemAt(i);
        }

        /// <inheritdoc />
        public void Clear()
        {
            for (var i = Owner.ItemCount - 1; i >= 0; i--)
            {
                if (Owner.ItemAt(i).Kind == SItemKind.Expression)
                {
                    Owner.RemoveItemAt(i);
                }
            }
        }

        /// <inheritdoc />
        public int IndexOf(SExpression item)
        {
            var seen = 0;
            foreach (var i in Owner.ItemsSpan)
            {
                if (i.Kind != SItemKind.Expression)
                {
                    continue;
                }

                if (ReferenceEquals(i.Expression, item))
                {
                    return seen;
                }

                seen++;
            }

            return -1;
        }

        /// <inheritdoc />
        public bool Contains(SExpression item) => IndexOf(item) >= 0;

        /// <inheritdoc />
        public void CopyTo(SExpression[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            foreach (var v in this)
            {
                array[arrayIndex++] = v;
            }
        }

        /// <inheritdoc />
        public bool Remove(SExpression item)
        {
            var i = IndexOf(item);
            if (i < 0)
            {
                return false;
            }

            RemoveAt(i);
            return true;
        }

        /// <summary>Walks the child forms the form holds at the moment this is called.</summary>
        /// <returns>An enumerator over those children.</returns>
        /// <remarks>
        /// The loop may move, remove or add children: <c>foreach (var c in a.Children) b.AddChild(c)</c>
        /// moves every one. See <see cref="SExpression"/>.
        /// </remarks>
        public IEnumerator<SExpression> GetEnumerator()
        {
            var owner = Owner;
            return Walk(owner.ItemArray, owner.ItemOffset, owner.ItemCount);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private static IEnumerator<SExpression> Walk(SItem[] items, int start, int count)
        {
            var end = start + count;
            for (var i = start; i < end; i++)
            {
                if (items[i].Expression is { } child)
                {
                    yield return child;
                }
            }
        }

        private int ItemIndexOf(int childIndex) => Owner.ItemIndexOfChild(childIndex, skip: null);
    }
}
