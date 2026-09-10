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
```

`Values`, `Children` and `Items` are **live views** over the same item list — mutate one and the
others see it. Typed reads and writes use the invariant culture, always.

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
