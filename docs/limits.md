# What this library does not do

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
- **A single form cannot hold more than 134,217,727 items**, and throws if one would: the item count
  shares an `int` with the node's flag bits. Reaching it takes a source of at least 268 MB holding
  one form of nothing but atoms.
- **Nothing is incremental over I/O.** Both APIs need the whole file in memory as a `string` or a
  `ReadOnlySpan<char>` first, and the async entry points are async *I/O* only, so there is no way to
  parse a file larger than memory. `SExpressionReader` is pull-based and builds no tree, which
  removes the several-times-the-file *tree*, not the file itself.
- **`SExpressionReader` is read-only, and deliberately.** It cannot edit, and there is no writer that
  takes one: the byte-identical write is built on the source spans the tree remembers. A consumer
  that changes a document parses it.
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
