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
