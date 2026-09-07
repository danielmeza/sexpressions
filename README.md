# SExpressions

A small, dependency-free S-expression parser and writer for .NET.

It reads and writes the Lisp-style parenthesised syntax used by KiCad files, Guile/Scheme
configuration and similar formats, into a plain object tree you can walk, query and serialise back
out — and it round trips **byte for byte**. Quoting style, comments, source order and the original
whitespace all survive a parse/write cycle, so you can load a 145 KB schematic, change one value,
and write a diff that touches exactly that one value.

```
dotnet add package SExpressions
```

## Quick start

```csharp
using SExpressions;

// Parse every top-level form in a file. Parse() returns just the first.
var doc = SDocument.Load("board.kicad_pcb");

// Query by path or by a recursive walk.
var version = doc.Find("kicad_pcb/version")?.GetValueAsInt();
var nets    = doc.Forms.SelectMany(f => f.Descendants("net")).Count();

// Edit, then write back. Untouched forms keep their original bytes.
doc.Find("kicad_pcb/general/thickness")!.SetValue(0, "1.6");
doc.Save("board.kicad_pcb");
```

Writing is format-preserving by default. Ask for `SExpressionFormat.Canonical` when you want the
whole document reformatted from the tree instead:

```csharp
var text = doc.ToText(new SExpressionWriterOptions { Format = SExpressionFormat.Canonical });
```

## Why "SExpressions" and not "SExpressionSharp"

This library was extracted from
[danielmeza/kicad-ultra](https://github.com/danielmeza/kicad-ultra), where it was called
`SExpressionSharp`. It was renamed on the way out, before the first push to nuget.org, because a
package ID can never be changed afterwards.

The `Sharp` suffix earns its keep when a library wraps something that already has a name and needs
to say "this is the .NET one" — which is why the sibling package
[`KiCadSharp`](https://github.com/danielmeza/kicad-sharp) **kept** its name: it reads as a
third-party .NET client for KiCad, where a bare `KiCad.*` prefix would look like it came from the
KiCad project and would be borrowing their trademark.

S-expressions are not anybody's product. There is nothing for the suffix to disambiguate here, so
`Sharp` added noise and nothing else. The generic half of the split gets the generic name.

This is recorded here so the question is not re-litigated later: the two halves are named
differently **on purpose**.

## Fidelity, and how it is verified

The round-trip guarantee is not a claim, it is a gate. The suite parses and rewrites a corpus of
**52 real KiCad 10.0.6 files — 1,968,451 bytes** (schematics, boards, a symbol library, a worksheet
and design rules) and asserts the output is byte-identical to the input. On top of that:

- boards and schematics are re-opened with `kicad-cli` after a canonical round trip, and their
  netlists and DRC results compared;
- tokens that other parsers silently drop (`exclude_from_sim`, `do_not_autoplace`,
  `duplicate_pin_numbers_are_jumpers`, embedded fonts, `generator_version`) are asserted to survive.

The corpus lives outside this repository. Point `ORBION_KICAD_ROOT` at a checkout of it to run those
tests; without it they skip themselves and the parser/writer unit tests still run, which is what CI
does.

```
dotnet test SExpressions.slnx -c Release
```

## Benchmarks

```
dotnet run -c Release --project benchmarks/SExpressions.Benchmarks
```

A [BenchmarkDotNet](https://benchmarkdotnet.org) project with `MemoryDiagnoser` enabled, measuring
parse, canonical write, format-preserving write, round trip and two query shapes against a large
(142 KB) and a small (12 KB) real schematic. It requires the corpus and fails without it — a
benchmark over substitute input produces numbers that look valid and mean nothing.

There is no separate `Tokenize` benchmark because there is no separate tokenizer to measure:
`SExpressionParser` scans and builds in one pass. `Parse (lean)` stands in for the scanning floor —
the same parse with source tracking and string pooling switched off.

> Earlier versions of this code quoted a "3.1x faster round trip" figure. That number came from a
> hand-rolled best-of-five harness comparing this parser against the implementation it replaced,
> inside one process. That predecessor has been deleted, so the comparison no longer exists and has
> deliberately not been reconstructed. **The BenchmarkDotNet numbers are not comparable to it** —
> different methodology, different question.

## History

`git log` here goes back to the original commits in `kicad-ultra`, including the acceptance suite
that was committed red before the rewrite that made it pass. The history was extracted with
`git filter-repo`, not copied.

## License

MIT — see [LICENSE](LICENSE).
