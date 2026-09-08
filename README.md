# SExpressions

A schema-free S-expression parser and writer for .NET. No dependencies, `net10.0`.

```
dotnet add package SExpressions
```

It reads the Lisp-style parenthesised syntax used by KiCad files, Guile/Scheme configuration and
similar formats into a plain object tree, and writes it back **byte for byte**. Quoting style,
comments, source order and the original whitespace all survive a parse/write cycle.

## Why it exists

It has no schema, so it **cannot drop a token it does not recognise**. That is the whole point.

Every Python alternative measured against a KiCad 10.0.6 schematic silently drops tokens it was
never taught — `kiutils` among them. In one 145 KB file from the test corpus, these are the counts
this library keeps and they do not:

| Token | Occurrences kept |
|---|---|
| `exclude_from_sim` | 89 |
| `do_not_autoplace` | 122 |
| `duplicate_pin_numbers_are_jumpers` | 18 |
| `embedded_fonts` | 19 |
| `generator_version` | 1 |

A dropped token is not a formatting difference. Write that file back and KiCad has lost the
simulation exclusions, the autoplace flags and the embedded fonts, with no error anywhere.

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

## Evidence

- **239 / 239 tests pass** (`dotnet test SExpressions.slnx -c Release`, ~62 s with the corpus).
- **Byte-identical round trip over 52 real KiCad 10.0.6 files — 1,968,451 bytes**: schematics,
  boards, a symbol library, a worksheet and design rules. Asserted, not claimed.
- Boards and schematics are re-opened with `kicad-cli` after a *canonical* round trip and their
  netlists and DRC results compared, so the guarantee is not merely textual.
- The five tokens in the table above are counted before and after a canonical round trip, for
  **every** file in the corpus, and must match exactly.

The corpus lives outside this repository. Point `ORBION_KICAD_ROOT` at a checkout of it to run the
corpus-backed tests; without it they return early and the parser/writer unit tests still run, which
is what CI does.

## Benchmarks

`dotnet run -c Release --project benchmarks/SExpressions.Benchmarks -- --filter '*'`

BenchmarkDotNet with `MemoryDiagnoser`, over the same two real schematics the corpus provides. It
**fails without the corpus** on purpose: a benchmark over substitute input produces numbers that
look valid and mean nothing.

```
BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 7950X3D 2.99GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.400
  DefaultJob : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
```

| Method                                  | File           | Mean           | Error         | StdDev        | Gen0     | Gen1     | Gen2     | Allocated |
|---------------------------------------- |--------------- |---------------:|--------------:|--------------:|---------:|---------:|---------:|----------:|
| 'Parse (fidelity)'                      | large (142 KB) | 458,997.936 ns | 1,798.9387 ns | 1,682.7283 ns |  19.5313 |  16.1133 |        - |  982824 B |
| 'Parse (lean)'                          | large (142 KB) | 470,858.580 ns | 2,794.6324 ns | 2,477.3689 ns |  27.8320 |  19.5313 |        - | 1410752 B |
| 'Write (canonical)'                     | large (142 KB) | 341,182.885 ns | 1,507.7277 ns | 1,410.3295 ns | 181.6406 | 181.6406 | 181.6406 |  636578 B |
| 'Write (preserving, unmodified)'        | large (142 KB) |       8.717 ns |     0.2063 ns |     0.2207 ns |   0.0013 |        - |        - |      64 B |
| 'Write (preserving, one edit)'          | large (142 KB) | 230,876.651 ns | 4,127.5185 ns | 6,665.1736 ns | 181.6406 | 181.6406 | 181.6406 |  582506 B |
| 'Round trip (parse + preserving write)' | large (142 KB) | 464,602.969 ns | 1,050.6511 ns |   877.3413 ns |  19.5313 |  16.1133 |        - |  982888 B |
| 'Query: FindAll("kicad_sch/symbol")'    | large (142 KB) |     669.820 ns |     2.4012 ns |     2.2461 ns |   0.0124 |        - |        - |     632 B |
| 'Query: Descendants("property")'        | large (142 KB) | 161,642.530 ns | 2,518.9507 ns | 2,356.2280 ns |  12.2070 |        - |        - |  622072 B |
| 'Parse (fidelity)'                      | small (12 KB)  |  38,653.488 ns |   377.4551 ns |   334.6042 ns |   2.3804 |   0.7324 |        - |  122264 B |
| 'Parse (lean)'                          | small (12 KB)  |  38,241.330 ns |   201.6307 ns |   188.6055 ns |   2.4414 |   0.7935 |        - |  125472 B |
| 'Write (canonical)'                     | small (12 KB)  |  11,371.023 ns |   100.0507 ns |    88.6923 ns |   1.3123 |   0.1526 |        - |   66312 B |
| 'Write (preserving, unmodified)'        | small (12 KB)  |       8.806 ns |     0.1126 ns |     0.2086 ns |   0.0013 |        - |        - |      64 B |
| 'Write (preserving, one edit)'          | small (12 KB)  |   2,553.243 ns |    35.2199 ns |    32.9447 ns |   0.9613 |   0.0420 |        - |   48376 B |
| 'Round trip (parse + preserving write)' | small (12 KB)  |  38,621.780 ns |   245.2333 ns |   229.3914 ns |   2.3804 |   0.7324 |        - |  122328 B |
| 'Query: FindAll("kicad_sch/symbol")'    | small (12 KB)  |     170.980 ns |     0.7808 ns |     0.7303 ns |   0.0124 |        - |        - |     632 B |
| 'Query: Descendants("property")'        | small (12 KB)  |  12,301.120 ns |    67.8360 ns |    63.4539 ns |   1.0223 |        - |        - |   51832 B |

Read across: a 142 KB schematic parses in **459 us** and round trips in **465 us**; writing it back
unmodified costs **8.7 ns** and 64 bytes, because nothing is dirty and the writer hands back the
source. `Parse (lean)` is not faster — turning off string pooling costs more in allocation (1.41 MB
vs 983 KB) than it saves in bookkeeping, which is the measurement that justifies pooling being on by
default.

`Write (preserving, unmodified)` is the fast path this design exists for: nothing is dirty, so the
writer hands back the original source instead of rendering anything, and the cost is independent of
file size. Read it as "the fast path is O(1)", not as a throughput number.

There is no separate `Tokenize` benchmark because there is no separate tokenizer:
`SExpressionParser` scans and builds in one pass. `Parse (lean)` stands in for the scanning floor —
the same parse with source tracking and string pooling switched off.

> Earlier versions of this code quoted a "3.1x faster round trip" figure against the implementation
> it replaced. That predecessor has been deleted, the comparison no longer exists, and it has
> deliberately not been reconstructed. **The BenchmarkDotNet numbers are not comparable to it.**

## What is NOT supported

Measured, not guessed. Each item below was reproduced against this build.

- **A backslash escape other than `\"` or `\\` is re-escaped by the canonical writer.** The parser
  decodes only the two escapes KiCad emits and keeps any other backslash literally, so `"c:\path"`
  parses to the text `c:\path` — and the canonical writer then escapes that backslash, emitting
  `"c:\\path"`. The *value* still round trips (re-parsing `\\` yields `\`), but the bytes do not.
  Format-preserving mode is unaffected: it copies the source. No such escape exists anywhere in the
  52-file corpus.
- **The canonical writer corrupts a comment that sits inside a form.** `(a # note` / `1)` is
  re-rendered as `(a\n\t# note 1\n)`, putting the following atom on the comment's line, where a
  re-parse swallows it: the value `1` is gone. Top-level comments (what a `.kicad_dru` actually
  contains, and what the corpus tests cover) are safe — the document writer breaks the line after
  every top-level item. Format-preserving mode is safe for both. **Do not run
  `SExpressionFormat.Canonical` over a file with in-form comments.**
- **A UTF-8 BOM is not preserved through `Load`/`Save`.** `File.ReadAllText` strips it and
  `File.WriteAllText` does not write one back, so a 9-byte BOM'd file comes back as 6 bytes. KiCad
  never writes a BOM; other producers might.
- **Line endings are only preserved in format-preserving mode.** Canonical output uses
  `SExpressionWriterOptions.NewLine`, which defaults to `\n`; a CRLF file canonicalises to LF.
- **`TryGetValue<bool>` does not understand KiCad's `yes` / `no`.** It is `bool.TryParse`, so it
  accepts `True`/`False` only. Use `GetValueAsBool`, which accepts `yes`, `true` and `1`.
  Symmetrically, `GetValueAsInt` / `GetValueAsDouble` / `GetValueAsBool` return `0`/`0.0`/`false`
  for an unparsable or missing value rather than throwing — `GetRequiredValue<T>` is the one that
  throws.
- **Nesting deeper than `MaxDepth` (256) throws** rather than parsing.
- **A file longer than 134,217,727 characters silently loses source tracking**, and with it the
  byte-identical write — the span is packed into an `SItem` and cannot be represented past that.
- **Nothing is incremental.** Parsing materialises the whole file as a string and builds a tree
  several times its size; the async entry points are async *I/O* only. There is no streaming or
  pull-based reader, and no way to parse a file larger than memory.
- **`SExpressionParser` is not thread-safe.** Instances are cheap; give each thread its own. The
  parsed tree is not synchronised for concurrent mutation either.
- **`SExpression` itself only removes a child by token** (`RemoveChild` removes the first,
  `RemoveChildren` removes all). For positional work use the views: `node.Children` is an
  `IList<SExpression>` (`Insert`, `Remove`, `RemoveAt`, `Clear`) and `node.Items` an `IList<SItem>`,
  which is the only one that can place a comment or an atom at an exact index.
- **`Parent` is `null` for a top-level form**, by design: the document's container is not a form and
  is not exposed.
- **No schema, no validation, no typed mapping.** There is no POCO binder, no attribute-driven
  serializer, no format-specific knowledge of any kind. That is deliberate — it is what lets the
  parser carry tokens it has never heard of — but it means every consumer writes its own accessors.
  For a KiCad-aware layer on top, see [`KiCadSharp`](https://github.com/danielmeza/kicad-sharp).
- **Only `#`-to-end-of-line comments.** No block comments, no `;` comments, and the prefix is a
  single `char`.
- Parsing accepts a **bare atom at the top level** (it lands in `SDocument.Items` and round trips),
  but it is not a form, so `Forms`, `Count` and the indexer do not see it.

## Quick start

Run against a real KiCad schematic:

```csharp
using SExpressions;

var doc = SDocument.Load("orbion-esp32s3.kicad_sch");   // 145,522 bytes

Console.WriteLine(doc.Find("kicad_sch/version")?.GetValueAsInt());          // 20250114
Console.WriteLine(doc.Forms.Sum(f => f.Descendants("property").Count()));   // 497

// Edit one value; only its bytes change on disk (31 characters, here).
doc.Find("kicad_sch/uuid")!.SetValue(0, "00000000-0000-0000-0000-000000000001");
doc.Save("orbion-esp32s3.kicad_sch");
```

From the command line, the same file through
[`KiCadSharp.Cli`](https://github.com/danielmeza/kicad-sharp):

```
$ kicadsharp parse orbion-esp32s3.kicad_sch
bytes         145522
top-level     1 form(s) parsed, 1 present in the file
nodes         7068
values        7963
max depth     9
```

## Naming

Extracted from [danielmeza/kicad-ultra](https://github.com/danielmeza/kicad-ultra), where it was
called `SExpressionSharp`, and renamed before the first push to nuget.org because a package ID can
never be changed afterwards. The `Sharp` suffix earns its keep when a library wraps something that
already has a name — which is why [`KiCadSharp`](https://github.com/danielmeza/kicad-sharp) **kept**
its name. S-expressions are not anybody's product, so there was nothing to disambiguate. The two
halves are named differently on purpose.

`git log` here goes back to the original commits in `kicad-ultra`, including the acceptance suite
committed red before the rewrite that made it pass. The history was extracted with `git filter-repo`,
not copied.

## License

MIT — see [LICENSE](LICENSE).
