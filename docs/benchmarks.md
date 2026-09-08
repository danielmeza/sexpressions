# Benchmarks and evidence

How the round-trip guarantee and the throughput numbers were measured.

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
