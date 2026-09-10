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

BenchmarkDotNet with `MemoryDiagnoser`, over real schematics the corpus provides. It **fails without
the corpus** on purpose: a benchmark over substitute input produces numbers that look valid and mean
nothing.

Two shapes are measured, and they are not interchangeable. Most cases reparse ONE document, which
keeps the parser's atom cache maximally warm and lets any per-document sizing decision be learned
once and reused; `Parse (corpus pass)` parses each of 19 distinct schematics once, which is what a
consumer does. PR #17 measured the same change at -5.8% on the first shape and -7.9% on the second,
so an allocation change is read on both.

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
| 'Parse (fidelity)'                      | large (142 KB) | 450,079.387 ns |   583.6490 ns |   545.9457 ns |  14.6484 |  11.2305 |        - |  758800 B |
| 'Parse (lean)'                          | large (142 KB) | 453,925.389 ns |   410.8317 ns |   320.7504 ns |  24.4141 |  20.9961 |        - | 1235624 B |
| 'Write (canonical)'                     | large (142 KB) | 321,375.287 ns |   287.5219 ns |   268.9482 ns | 181.6406 | 181.6406 | 181.6406 |  636978 B |
| 'Write (preserving, unmodified)'        | large (142 KB) |       8.404 ns |     0.1415 ns |     0.1323 ns |   0.0013 |        - |        - |      64 B |
| 'Write (preserving, one edit)'          | large (142 KB) | 200,063.390 ns |   231.3910 ns |   216.4433 ns | 181.6406 | 181.6406 | 181.6406 |  582906 B |
| 'Round trip (parse + preserving write)' | large (142 KB) | 444,764.118 ns | 1,402.7686 ns | 1,171.3753 ns |  14.6484 |  11.2305 |        - |  758864 B |
| 'Query: FindAll("kicad_sch/symbol")'    | large (142 KB) |     778.494 ns |     1.3884 ns |     1.0840 ns |   0.0114 |        - |        - |     616 B |
| 'Query: Descendants("property")'        | large (142 KB) | 137,340.144 ns |   543.1870 ns |   424.0847 ns |  11.2305 |        - |        - |  565760 B |
| 'Parse (fidelity)'                      | small (12 KB)  |  36,066.871 ns |    43.9024 ns |    41.0664 ns |   1.2207 |   0.1831 |        - |   63056 B |
| 'Parse (lean)'                          | small (12 KB)  |  37,252.077 ns |   102.7909 ns |    85.8350 ns |   2.0142 |   0.4883 |        - |  103448 B |
| 'Write (canonical)'                     | small (12 KB)  |  11,620.198 ns |    77.3663 ns |    72.3685 ns |   1.3123 |   0.1526 |        - |   66312 B |
| 'Write (preserving, unmodified)'        | small (12 KB)  |       8.402 ns |     0.1262 ns |     0.1181 ns |   0.0013 |        - |        - |      64 B |
| 'Write (preserving, one edit)'          | small (12 KB)  |   1,573.185 ns |     2.3451 ns |     2.1936 ns |   0.9632 |   0.0420 |        - |   48376 B |
| 'Round trip (parse + preserving write)' | small (12 KB)  |  36,437.101 ns |    33.6518 ns |    28.1008 ns |   1.2207 |   0.1831 |        - |   63120 B |
| 'Query: FindAll("kicad_sch/symbol")'    | small (12 KB)  |     177.569 ns |     0.2422 ns |     0.2147 ns |   0.0122 |        - |        - |     616 B |
| 'Query: Descendants("property")'        | small (12 KB)  |  12,411.517 ns |    42.0099 ns |    37.2407 ns |   0.9308 |        - |        - |   47120 B |

One measured operation of the corpus pass is one parse of **each** of the 19 real schematics
(1 590 KB in total, 84 KB average), so its row is 19 documents, not one:

| Method                     | Mean     | Error     | StdDev    | Gen0     | Gen1     | Allocated |
|--------------------------- |---------:|----------:|----------:|---------:|---------:|----------:|
| 'Parse (corpus pass)'      | 5.042 ms | 0.0038 ms | 0.0031 ms | 164.0625 | 140.6250 |   8.07 MB |
| 'Round trip (corpus pass)' | 5.080 ms | 0.0114 ms | 0.0101 ms | 164.0625 | 140.6250 |   8.07 MB |

Read across: a 142 KB schematic parses in **450 us** and round trips in **445 us**; writing it back
unmodified costs **8 ns** and 64 bytes, because nothing is dirty and the writer hands back the
source. `Parse (lean)` is not faster — turning off string pooling costs more in allocation (1.21 MB
vs 741 KB) than it saves in bookkeeping, which is the measurement that justifies pooling being on by
default.

### What moved, and when

**Working buffers, per thread instead of per instance.** The parser used to allocate a 32 KB atom
cache and an 8 KB item scratch stack per parser *instance*, to amortise them across parses. Every
entry point here builds a parser, parses once and drops it, so they never amortised.

| Method             | File  |     before |      after |  delta |
|--------------------|-------|-----------:|-----------:|-------:|
| Parse (fidelity)   | large | 983,368 B  | 926,400 B  | -5.8%  |
| Parse (fidelity)   | small | 122,272 B  |  76,872 B  | -37.1% |
| Parse (fidelity)   | large | 462.40 us  | 455.52 us  | -1.5%  |
| Parse (fidelity)   | small |  38.12 us  |  36.58 us  | -4.0%  |

The fixed ~41 KB is most of what a 12 KB file costs, which is why the small-file win is the large
one.

**One item block per document, sliced.** After the change above, `dotnet-trace --profile gc-verbose`
put 87% of a parse's allocation in the tree, and the single largest line in it was not data:
165.7 KB of `SItem[]` *object headers*, one array per form, 18% of the parse. MEASURED over 93 762
forms in a 65-file corpus, **48.8% of forms hold exactly one item** and a form averages 2.17, so that
24-byte header usually cost more than the 16-byte item it carried. Forms close in strict post-order,
so each form now takes a contiguous slice of one block per document instead of owning an array:

| Method             | File   |     before |      after |  delta |
|--------------------|--------|-----------:|-----------:|-------:|
| Parse (fidelity)   | large  | 926,400 B  | 758,800 B  | -18.1% |
| Parse (fidelity)   | small  |  76,872 B  |  63,056 B  | -18.0% |
| Parse (fidelity)   | corpus |   9.85 MB  |   8.07 MB  | -18.1% |
| Parse (fidelity)   | large  | 460.89 us  | 450.08 us  | -2.3%  |
| Parse (fidelity)   | corpus |   5.222 ms |   5.042 ms | -3.4%  |

Two things that cost most of the win before they were found, both worth not rediscovering:

- **A block per document lands on the large object heap.** 15 037 items is a 194 KB array and the
  LOH starts at 85 000 bytes, so every parse put a fresh LOH object down: 58.6 Gen2 collections per
  1 000 operations against zero, and **+40.8% time while the allocation figure went down** -- which
  is exactly how an LOH regression reads if you only look at bytes. Blocks are capped at 4 096 items
  (65 560 bytes with the header) and a document simply gets more of them.
- **The slice made the node bigger.** An offset and a count as two plain ints took `SExpression`
  from 64 bytes to 72 -- four references, four ints and a one-byte enum pads to 72 -- which is
  7 071 x 8 = 56.6 KB on that schematic, a third of the win. The count and the flags share one int
  now and the node is 64 bytes again. Both figures are measured on the real type, by allocating
  200 000 of them and reading `GC.GetAllocatedBytesForCurrentThread`.

**Untouched code got faster**, because a document's items are now contiguous rather than scattered
across 7 071 arrays. `Write (preserving, one edit)` allocates a byte-identical 582,906 B before and
after and runs **-18.0%** (243.94 us -> 200.06 us); canonical write is -11.7%. That is locality, and
it is the clean measurement of it, because nothing about what it allocates changed.

**What got slower**, and it is real rather than noise: `FindAll` is **+8.6%** on the large file
(716.7 -> 778.5 ns) and `Descendants` **+7.3%** on the small one. Those go through iterator methods,
and a `ref struct` span cannot live across a `yield`, so they index the slice per item instead of
walking a span. A struct enumerator that captures `(array, offset, count)` once was built to fix it
and **measured worse**: the enumerator becomes a field of a state machine that `Descendants`
allocates once per node, so 16 more bytes per node gave back every byte of the allocation win
(565,760 -> 622,336 B) and cost 4% more time. It was deleted. The indexed loops stay.

`Write (preserving, unmodified)` is the fast path this design exists for: nothing is dirty, so the
writer hands back the original source instead of rendering anything, and the cost is independent of
file size. Read it as "the fast path is O(1)", not as a throughput number.

There is no separate `Tokenize` benchmark because there is no separate tokenizer:
`SExpressionParser` scans and builds in one pass. `Parse (lean)` stands in for the scanning floor —
the same parse with source tracking and string pooling switched off.

> Earlier versions of this code quoted a "3.1x faster round trip" figure against the implementation
> it replaced. That predecessor has been deleted, the comparison no longer exists, and it has
> deliberately not been reconstructed. **The BenchmarkDotNet numbers are not comparable to it.**
