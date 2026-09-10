# SExpressions

[![NuGet](https://img.shields.io/nuget/v/SExpressions.svg)](https://www.nuget.org/packages/SExpressions)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Read and write S-expression files in .NET without losing anything. Edit one value, save, and the
rest of the file comes back byte for byte.

```
dotnet add package SExpressions
```

```csharp
using SExpressions;

var doc = SDocument.Load("board.kicad_sch");

Console.WriteLine(doc.Find("kicad_sch/version")?.GetValueAsInt());   // 20250114

doc.Find("kicad_sch/uuid")!.SetValue(0, "00000000-0000-0000-0000-000000000001");
doc.Save("board.kicad_sch");   // only those 31 characters changed
```

No dependencies, `net10.0`.

## Why not one of the others

Most S-expression libraries know a schema. When they meet a token they were never taught, they drop
it — quietly, on save.

For KiCad files that's not cosmetic. A parser that doesn't know `exclude_from_sim` writes back a
schematic with the simulation exclusions gone, and nothing anywhere says so. We hit this with
`kiutils`; a single 145 KB schematic lost 89 `exclude_from_sim` tokens, 122 `do_not_autoplace` and
all 19 `embedded_fonts`.

This library has no schema, so there's nothing for it to not recognise. Quoting style, comments,
source order and whitespace all survive the round trip.

That makes it a good fit for KiCad, Guile/Scheme config, and anything else where you need to change
one thing and leave the file alone.

## Usage

**Read a value**

```csharp
var doc = SDocument.Load("board.kicad_pcb");
double thickness = doc.Find("kicad_pcb/general/thickness")!.GetValueAsDouble();
```

**Walk the tree**

```csharp
foreach (var prop in doc.Forms.SelectMany(f => f.Descendants("property")))
    Console.WriteLine($"{prop.GetValueAsString(0)} = {prop.GetValueAsString(1)}");
```

**Add something**

```csharp
var net = new SExpression("net");
net.AddValue(42);
net.AddValue("GND", SQuoteStyle.Quoted);
doc.Forms[0].Add(net);
doc.Save("board.kicad_pcb");
```

**Check a file parses, without loading it twice**

```csharp
if (!SExpressionParser.TryParseAll(File.ReadAllText(path), out var forms, out var error))
    Console.Error.WriteLine(error);
```

There's a CLI too, in [KiCadSharp](https://github.com/danielmeza/kicad-sharp):

```
dotnet tool install --global KiCadSharp.Cli
kicadsharp fmt board.kicad_sch --in-place
```

## The model, briefly

| Type | What it is |
|---|---|
| `SDocument` | A whole file: every top-level form, plus the whitespace and comments between them. |
| `SExpression` | One form — a token and its items. |
| `SItem` | One item of a form, in source order: an atom, a child form, or a comment. |
| `SQuoteStyle` | Whether an atom was quoted. Parsed atoms remember; the writer never guesses. |

Values and children live in **one ordered list**, which is why `(a 1 (b) 2)` comes back as written
instead of reordered into values-then-children.

Full API reference: [docs/api.md](docs/api.md).

## Performance

Roughly 65 MB/s parsing on a 145 KB KiCad schematic, and a save touches only the bytes that changed.

For a document you read once and throw away, `SExpressionReader` — a `ref struct` over
`ReadOnlySpan<char>`, the `Utf8JsonReader` pattern — skips the tree entirely: counting every `symbol`
across 1.59 MB of schematics costs **zero bytes** against 14.8 MB through the tree, and half the
time. It is read-only; editing still goes through the tree, which is what the byte-identical write is
built on.

Numbers, methodology and the BenchmarkDotNet project: [docs/benchmarks.md](docs/benchmarks.md).

## Limits

It parses S-expressions, not Lisp. No reader macros, no dotted pairs, no `#|block comments|#`, no
evaluation. Block comments and a few other edge cases are listed in
[docs/limits.md](docs/limits.md) — worth a look before you point it at a format that isn't KiCad's.

## Contributing

Issues and pull requests welcome. `dotnet test` runs the suite, including a corpus round-trip check
over real KiCad files.

## License

MIT — see [LICENSE](LICENSE).

Extracted from [kicad-ultra](https://github.com/danielmeza/kicad-ultra), where it was called
`SExpressionSharp`. Renamed before the first NuGet push, since a package ID is permanent.
