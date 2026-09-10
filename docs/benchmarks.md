# Benchmarks and evidence

How the round-trip guarantee and the throughput numbers were measured.

## Evidence

- **245 / 245 tests pass** (`dotnet test SExpressions.slnx -c Release`, ~62 s with the corpus).
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

```
taskset -c 0-7 dotnet run -c Release --project benchmarks/SExpressions.Benchmarks -- --filter '*'
```

BenchmarkDotNet with `MemoryDiagnoser`, over the same two real schematics the corpus provides. It
**fails without the corpus** on purpose: a benchmark over substitute input produces numbers that
look valid and mean nothing.

**Pin the run.** This machine is a 7950X3D, whose two CCDs are not interchangeable:
`cpu0-7,16-23` share 96 MB of 3D V-cache, `cpu8-15,24-31` share 32 MB, and an unpinned run
lands on either. MEASURED: `Parse (lean)` moved 4% between two consecutive runs of *identical*
code while reporting a +/-0.9% confidence interval. Even pinned, treat anything under ~1.5% on
the large file as noise; the numbers below are pinned to `cpu0-7`.

```
BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 7950X3D 2.99GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.400
  DefaultJob : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
```

| Method                                  | File           | Mean           | Error         | StdDev        | Gen0     | Gen1     | Gen2     | Allocated |
|---------------------------------------- |--------------- |---------------:|--------------:|--------------:|---------:|---------:|---------:|----------:|
| 'Parse (fidelity)'                      | large (142 KB) | 455,515.229 ns |   376.6980 ns |   352.3635 ns |  18.0664 |  14.1602 |        - |  926400 B |
| 'Parse (lean)'                          | large (142 KB) | 468,940.908 ns | 4,260.9781 ns | 3,985.7216 ns |  27.8320 |  22.4609 |        - | 1403224 B |
| 'Write (canonical)'                     | large (142 KB) | 360,496.464 ns |   616.4280 ns |   546.4474 ns | 181.6406 | 181.6406 | 181.6406 |  636978 B |
| 'Write (preserving, unmodified)'        | large (142 KB) |       9.273 ns |     0.1157 ns |     0.1083 ns |   0.0013 |        - |        - |      64 B |
| 'Write (preserving, one edit)'          | large (142 KB) | 244,005.436 ns |   155.9194 ns |   130.1997 ns | 181.6406 | 181.6406 | 181.6406 |  582906 B |
| 'Round trip (parse + preserving write)' | large (142 KB) | 456,539.478 ns |   382.2308 ns |   357.5390 ns |  18.0664 |  14.1602 |        - |  926464 B |
| 'Query: FindAll("kicad_sch/symbol")'    | large (142 KB) |     685.585 ns |     2.5965 ns |     2.1682 ns |   0.0124 |        - |        - |     632 B |
| 'Query: Descendants("property")'        | large (142 KB) | 139,885.627 ns |   815.9661 ns |   763.2552 ns |  12.2070 |        - |        - |  622336 B |
| 'Parse (fidelity)'                      | small (12 KB)  |  36,582.388 ns |    55.2985 ns |    43.1734 ns |   1.5259 |   0.3052 |        - |   76872 B |
| 'Parse (lean)'                          | small (12 KB)  |  37,585.075 ns |    37.2766 ns |    33.0447 ns |   2.3193 |   0.6714 |        - |  117264 B |
| 'Write (canonical)'                     | small (12 KB)  |  11,358.551 ns |    29.7689 ns |    27.8459 ns |   1.3123 |   0.1526 |        - |   66312 B |
| 'Write (preserving, unmodified)'        | small (12 KB)  |       8.210 ns |     0.1162 ns |     0.1087 ns |   0.0013 |        - |        - |      64 B |
| 'Write (preserving, one edit)'          | small (12 KB)  |   1,580.296 ns |     6.7110 ns |     6.2775 ns |   0.9632 |   0.0420 |        - |   48376 B |
| 'Round trip (parse + preserving write)' | small (12 KB)  |  36,778.044 ns |    76.4765 ns |    67.7944 ns |   1.5259 |   0.3052 |        - |   76936 B |
| 'Query: FindAll("kicad_sch/symbol")'    | small (12 KB)  |     170.087 ns |     0.8129 ns |     0.7604 ns |   0.0124 |        - |        - |     632 B |
| 'Query: Descendants("property")'        | small (12 KB)  |  12,166.130 ns |    44.3074 ns |    41.4452 ns |   1.0223 |        - |        - |   51832 B |

Read across: a 142 KB schematic parses in **456 us** and round trips in **457 us**; writing it back
unmodified costs **9 ns** and 64 bytes, because nothing is dirty and the writer hands back the
source. `Parse (lean)` is not faster — turning off string pooling costs more in allocation (1.40 MB
vs 905 KB) than it saves in bookkeeping, which is the measurement that justifies pooling being on by
default.

### What moved, and when

The parser's working buffers used to be allocated per parser *instance* — a 32 KB atom cache and an
8 KB item scratch stack — to amortise them across parses. Every entry point here builds a parser,
parses once and drops it, so they never amortised. They are held per thread now. Against the commit
before that change, pinned:

| Method             | File  |     before |      after |  delta |
|--------------------|-------|-----------:|-----------:|-------:|
| Parse (fidelity)   | large | 983,368 B  | 926,400 B  | -5.8%  |
| Parse (fidelity)   | small | 122,272 B  |  76,872 B  | -37.1% |
| Parse (fidelity)   | large | 462.40 us  | 455.52 us  | -1.5%  |
| Parse (fidelity)   | small |  38.12 us  |  36.58 us  | -4.0%  |

The fixed ~41 KB is most of what a 12 KB file costs, which is why the small-file win is the large
one. The write and query rows are untouched code; the movement in them is this machine's noise, and
`Write (preserving, unmodified)` is an 8-nanosecond measurement that is mostly call overhead.

After that, `dotnet-trace --profile gc-verbose` puts **87% of a parse's remaining allocation in the
tree itself** — 386.7 KB of `SExpression` nodes, 234.9 KB of `SItem[]` payload, and 165.7 KB of
`SItem[]` *headers*, one array per form. 48.4% of forms hold exactly one item, so that header is
usually bigger than the payload it carries. Cutting it means giving nodes a slice of one contiguous
block instead of their own array, which is a data-model change, not a tuning change.

`Write (preserving, unmodified)` is the fast path this design exists for: nothing is dirty, so the
writer hands back the original source instead of rendering anything, and the cost is independent of
file size. Read it as "the fast path is O(1)", not as a throughput number.

There is no separate `Tokenize` benchmark because there is no separate tokenizer:
`SExpressionParser` scans and builds in one pass. `Parse (lean)` stands in for the scanning floor —
the same parse with source tracking and string pooling switched off.

> Earlier versions of this code quoted a "3.1x faster round trip" figure against the implementation
> it replaced. That predecessor has been deleted, the comparison no longer exists, and it has
> deliberately not been reconstructed. **The BenchmarkDotNet numbers are not comparable to it.**
