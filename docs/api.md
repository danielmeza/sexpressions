# API reference

The types, and the reasoning behind them. The short version is in the [README](../README.md).

## What it does

### The model

- **`SItem`** — one element of a form, in *source order*: an atom, a nested form, or a line comment.
  Values and children live in **one ordered list**, which is what makes `(a 1 (b) 2)` come back as
  written instead of re-ordered into values-then-children. A 16-byte readonly struct.
- **`SQuoteStyle`** — `Auto` / `Bare` / `Quoted`. A parsed atom *remembers* whether it arrived
  quoted; the writer never guesses for it.
- **`SExpression`** — one form: a token plus its items. `Token`, `Parent`, `Items`, `Values`,
  `Children`, `Comments`, `IsModified`, `HasSource`, `SourceSpan`.
- **`SDocument`** — a whole file: *every* top-level form, plus the comments and whitespace between
  them. `SExpressionParser.Parse` returns only the first form; `ParseAll` / `SDocument.Load` return
  all of them. A `.kicad_sch` has one top-level form; a `.kicad_dru` is a sequence of them
  interleaved with `#` comment blocks, and `Parse` would throw the rest of the file away.
- **`SExpressionFormatException`** (derives from `FormatException`) — carries `Position`, `Line` and
  `Column`, and its message reads `... at line 4, column 12 (offset 71).`

### Reading and writing

| API | What it does |
|---|---|
| `SExpression.Parse` / `.Load` | First top-level form, from text or a file. |
| `SDocument.Parse` / `.Load` / `.LoadAsync` | Every top-level form. |
| `SExpressionParser.ParseAllAsync(TextReader)` | Async I/O; the parse itself is not incremental. |
| `SExpressionParser.ClearThreadBuffers()` | Release this thread's atom cache and item scratch stack. |
| `.ToText()` / `.ToText(options)` / `.Save` / `.SaveAsync` | Render, to string or to a file. |
| `SExpressionWriter.WriteTo(TextWriter)` / `.WriteToFile` / `.WriteToFileAsync` | The writer directly. |

**Format-preserving write is the default.** A node that has not been modified is copied out of the
source text; a node that has been modified is re-rendered and spliced back into the original gaps
around it. Measured on a real 145,522-byte schematic: rewriting the root `uuid` changes **31 of
145,522 characters** — every one of them inside that atom — and the file length is unchanged.
`SExpressionFormat.Canonical` reformats everything from the tree instead, with `Indent` (default
tab) and `NewLine` (default `\n`) under your control.

**Composing works the same way as editing.** Adding a node has no original gap to splice into, so
the writer synthesises the one gap the new node needs — copied from the separator its neighbours
already use — and leaves every other span alone. Removing a node takes that node's own separator
with it and nothing else. A sibling nobody touched keeps its bytes *and* its indentation, a form
whose children were all on one line stays on one line, blank lines between siblings survive, and the
container's closing paren keeps its column. Measured on a 170,001-byte schematic: appending one
`(wire …)` to the root is **+150 bytes across 11 new lines and 0 rewritten ones**, and the whole
diff is a single contiguous insertion.

**A new node is indented the way the file already is.** Its first line copies the indentation of
the sibling it copied its separator from, and every line below that is laid out from the same text,
in the file's own unit: a table written by KiCad 8 with two spaces gets two spaces, a KiCad 10 file
gets tabs. The unit is read off the file, from how far a child on a line of its own sits inside its
parent, so `SExpressionWriterOptions.Indent` only applies to `Canonical`, to a tree built in memory,
and to a file that shows no indentation to follow. No line of a new node sits deeper than the
sibling it lines up with, even in a file whose indentation does not match its nesting.

**A parsed node that moves keeps its bytes but not its old indentation.** Taken from another file,
or from another depth of this one — a symbol out of a schematic's `lib_symbols` into a library, say —
it is re-indented line by line onto where it now stands: its first line where its new siblings
start, and each step further in, in its old file's unit, one step in the unit of the file it joined.
Only whitespace at the start of a line changes; atoms, a quoted value that spans lines included, are
copied as they are. A node moved between two places indented the same way, such as a symbol between
two KiCad 10 libraries, still comes across byte for byte.

**The start of a document belongs to the file.** Removing or moving the first top-level form leaves
the next item at the top, not behind the line break that used to separate them, and whatever
whitespace opened the file stays there.

### Query and mutation

```csharp
node["uuid"]                       // first child by token, or null
node.GetChild(t) / GetChildren(t)  // first / all children by token
node.Find("kicad_sch/lib_symbols/symbol")     // first match by path
node.FindAll("kicad_pcb/net")                 // every match by path
node.Descendants("property")                  // recursive walk, optionally filtered
node.GetValue(i) / GetValueAsString/Int/Double/Bool
node.TryGetValue<T>(i, out v) / GetValue<T>(i, fallback) / GetRequiredValue<T>(i)
node.GetChildValue("uuid")
node.SetValue(i, s, quote) / SetValue<T>(i, v, format) / SetChildValue(t, s)
node.AddValue / AddValues / AddChild / CreateChild / AddComment
node.RemoveChild(t) / RemoveChildren(t)
node.Clone()                       // keeps its source, so an untouched copy still writes byte-for-byte
                                   // (re-indented if it is added somewhere indented differently)
```

`Values`, `Children` and `Items` are **live views** over the same item list — mutate one and the
others see it. Typed reads and writes use the invariant culture, always.

**A form has one parent, so adding one moves it.** `AddChild`, `SDocument.Add`, and `Add`, `Insert`
and the indexer on `Children` and `Items` take the form out of wherever it was, another document
included, and put that same form here, its own bytes untouched. Add a `Clone()` to leave the
original where it is.

A form added to the list it is already in moves within that list:

- **An insert index is where the form ends up**, read in the list without it, the way
  `ObservableCollection<T>.Move` reads its new index. `Count` still means the end. On `[a, b, c]`,
  `Insert(2, a)` and `Insert(3, a)` both give `[b, c, a]`, and `Insert(0, c)` gives `[c, a, b]`.
  `Add` moves it after every item the form holds.
- **A move to where it already is changes nothing.** The document is not marked modified and saves
  byte for byte.
- **Setting `Children[i]` or `Items[i]` to it replaces the item at `i`** as the list stood when you
  set it. That item leaves the tree, the form closes the gap it left, and the list is one shorter:
  on `[a, b, c]`, `Children[2] = a` gives `[b, a]`.

A form that moves within one list is written exactly as one moved in from another form would be.
The whitespace in front of it goes with it, and the separator where it lands is copied from its new
neighbours. A comment that sat beside it is an item of its own and stays where it was.

**An `SItem` taken from a parse is written from what it holds, not from where it was parsed.**
Inserted anywhere, from this document or another, an atom or comment is rendered from its text and
the quoting it arrived with, behind a separator copied from its new neighbours. Only its own bytes
are added.

**Out of range throws, as `IList<T>` says.** `Insert` on `Items`, `Children` and `Values` takes 0
to `Count`, and `Count` appends; the `Items` and `Children` indexers take 0 to `Count - 1`.
Anything else throws `ArgumentOutOfRangeException` before anything changes.

**A form cannot contain itself.** Adding a form to itself, or into a form nested inside it, throws
`InvalidOperationException` before anything moves. Moving a descendant up or out is fine.

**A walk visits what the form held when it started.** A `foreach` over `Items`, `Children` or
`Values`, or a walk of `GetChildren`, `Descendants` or `Comments`, may add, remove or move items of
the form it walks: `foreach (var c in a.Children) b.AddChild(c)` moves every child, and
`values.AddRange(values)` appends each value once. The next walk sees the form as it is then.
`Count` and the indexers stay live. An item replaced in place during a walk (an indexer, or
`SetValue`) may be visited as it was or as it is now.

### Parser knobs (`SExpressionParserOptions`)

| Option | Default | Effect |
|---|---|---|
| `CommentPrefix` | `'#'` | `null` disables comments entirely, for dialects where `#` is an atom. |
| `MaxDepth` | `256` | Nesting past this throws, as a guard against hostile input. |
| `TrackSource` | `true` | Off drops the source reference — and with it the byte-identical write. |
| `PoolStrings` | `true` | De-duplicates short atoms; roughly halves allocations on KiCad files. |

**The parser's working buffers belong to the thread, not to the instance.** A parser takes the atom
cache and the item scratch stack from thread-local storage for the duration of a parse, so
`new SExpressionParser().ParseAll(text)` in a loop pays for them once rather than once per call.
Two parsers on one thread are therefore not isolated from each other: nothing observable leaks — the
scratch stack is wiped when a parse ends, and the cache only ever returns a string equal to the one
just scanned — but they will hand out the same `string` instance for equal atoms.

The cache holds up to 4096 strings of at most 32 characters, per thread. That is a few hundred KB at
the very worst, and it is per thread rather than per process: the async entry points resume on a pool
thread, so a long-running host can accumulate one cache per thread that has ever completed a parse.
`SExpressionParser.ClearThreadBuffers()` gives the calling thread's buffers back; parsing after it is
correct and simply pays to rebuild them.

## Reading without a tree (`SExpressionReader`)

`SExpressionReader` is a `ref struct` over `ReadOnlySpan<char>` that yields tokens without
materialising anything — the `Utf8JsonReader` pattern. It is an **additional** API, not a
replacement: the tree is what generators mutate and what the byte-identical write is built on. Use
the reader when a document is read once and thrown away, which is what an audit or an inspection
does.

```csharp
var reader = new SExpressionReader(text);
while (reader.Read())
{
    if (reader.TokenType == SExpressionTokenType.StartForm && reader.ValueEquals("property"))
    {
        reader.Read();                        // the property name
        var name = reader.GetString();
        reader.Read();                        // its value
        var value = reader.GetString();
    }
}
```

| Member | What it does |
|---|---|
| `Read()` | Advances to the next token. False at the end of the input. |
| `TokenType` | `StartForm`, `EndForm`, `Atom` or `Comment`. |
| `Depth` | 1 for a top-level form's `StartForm` and its matching `EndForm`. |
| `ValueSpan` | The form's token, the atom's content without quotes, or the comment without its prefix — a slice of the source, **not** unescaped. |
| `ValueIsEscaped` | True when `ValueSpan` still holds a backslash escape `GetString` would decode. |
| `ValueEquals(span)` | Compares through escapes. Allocates nothing. |
| `GetString()` | Materialises the value, decoding escapes. **The one call here that allocates.** |
| `TryGetValue<T>(out T)` | Parses the value in the invariant culture; `bool` reads `yes`/`no`. |
| `SkipForm()` | Walks past the current form's whole subtree, leaving the reader on its `EndForm`. |
| `TryReadChild(token)` | Advances to the next direct child form with that token, skipping the rest. |

### What "zero allocation" means here

**No allocation proportional to the input, not literally none.** The reader allocates nothing at all
and every token it reports is a slice of the caller's span; what the caller keeps is the caller's
cost. Measured over one pass of 19 real schematics (1.59 MB):

| Workload                                  |   tree |    reader |
|-------------------------------------------|-------:|----------:|
| Count every `symbol` form (keeps nothing)  | 14.78 MB | **0 B**  |
| Extract every `property` as a record       | 15.04 MB | 676 KB   |

The second row is the floor: those 676 KB are the strings the caller asked for. `MessagePackReader`
has exactly the same ceiling — it allocates its output objects too. Use `ValueEquals` and
`TryGetValue<T>` where a value is only being tested or parsed, and `GetString()` only for what is
kept.

The reader does not intern. A 2-way atom cache was built, measured and rejected in PR #17 (909 → 723
strings per parse for 3% more time), and there is no tree here to amortise one over.
