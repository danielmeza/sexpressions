using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;

namespace SExpressions.Benchmarks
{
    /// <summary>
    /// The operations a consumer actually pays for, measured on real KiCad files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no separate "tokenize" benchmark because the library has no separate tokenizer to
    /// measure: <see cref="SExpressionParser"/> is a single pass that scans and builds at the same
    /// time. <see cref="ParseLean"/> stands in for the scanning floor instead - it is the same parse
    /// with source-span tracking and string pooling switched off, so what remains is dominated by
    /// scanning and node allocation. Measuring a hand-rolled scanner here would produce a number
    /// about the benchmark rather than about the library.
    /// </para>
    /// <para>
    /// These numbers are NOT comparable to the "3.1x faster round trip" figure quoted when this
    /// parser replaced its predecessor. That came from a hand-rolled best-of-five harness comparing
    /// two implementations in one process; the predecessor has been deleted, so the comparison no
    /// longer exists and has deliberately not been reconstructed.
    /// </para>
    /// </remarks>
    [MemoryDiagnoser]
    public class SExpressionBenchmarks
    {
        private static readonly SExpressionParserOptions LeanOptions =
            new() { TrackSource = false, PoolStrings = false };

        private static readonly SExpressionWriterOptions CanonicalOptions =
            new() { Format = SExpressionFormat.Canonical };

        private SDocument _document = null!;
        private SDocument _editedDocument = null!;

        public IEnumerable<CorpusFile> Files()
        {
            yield return new CorpusFile("large", Corpus.LargeSchematic);
            yield return new CorpusFile("small", Corpus.SmallSchematic);
        }

        [ParamsSource(nameof(Files))]
        public CorpusFile File { get; set; } = null!;

        /// <summary>
        /// The write and query benchmarks measure writing and querying, not parsing, so the document
        /// they operate on is built once per parameter set rather than inside the measured region.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _document = new SExpressionParser().ParseAll(File.Text);

            // A second copy with exactly one value changed, for the write path that has real work to
            // do. Writing never clears the dirty flag, so this document stays in the same state
            // across iterations and every iteration measures the same partial rebuild.
            _editedDocument = new SExpressionParser().ParseAll(File.Text);
            _editedDocument.Find($"{RootToken}/uuid")!.SetValue(0, "00000000-0000-0000-0000-000000000001");
        }

        /// <summary>The head of the single top-level form in a .kicad_sch file.</summary>
        private const string RootToken = "kicad_sch";

        /// <summary>Full-fidelity parse: source spans recorded, atom strings pooled.</summary>
        [Benchmark(Description = "Parse (fidelity)")]
        public SDocument Parse() => new SExpressionParser().ParseAll(File.Text);

        /// <summary>The scanning floor: same parse without source tracking or string pooling.</summary>
        [Benchmark(Description = "Parse (lean)")]
        public SDocument ParseLean() => new SExpressionParser(LeanOptions).ParseAll(File.Text);

        /// <summary>Reformat every form from the tree, ignoring the original layout.</summary>
        [Benchmark(Description = "Write (canonical)")]
        public string WriteCanonical() => new SExpressionWriter(CanonicalOptions).Write(_document);

        /// <summary>
        /// Write back an untouched document. This is the fast path the design exists for: nothing is
        /// dirty, so the writer hands back the original source instead of rendering anything, and the
        /// cost is independent of file size. Read it as "proof the fast path is O(1)", not as a
        /// throughput number - it is not writing 142 KB in single-digit nanoseconds, it is writing
        /// nothing at all.
        /// </summary>
        [Benchmark(Description = "Write (preserving, unmodified)")]
        public string WritePreservingUnmodified() => new SExpressionWriter().Write(_document);

        /// <summary>
        /// Write back a document with one value changed: the writer re-renders the forms on the path
        /// to the edit and splices the untouched source around them. This is the number that matters
        /// for an edit-and-save workload.
        /// </summary>
        [Benchmark(Description = "Write (preserving, one edit)")]
        public string WritePreservingAfterEdit() => new SExpressionWriter().Write(_editedDocument);

        /// <summary>Parse and write back: the end-to-end cost of touching a file at all.</summary>
        [Benchmark(Description = "Round trip (parse + preserving write)")]
        public string RoundTrip() => new SExpressionParser().ParseAll(File.Text).ToText();

        /// <summary>
        /// Path lookup over the parsed tree. The path is rooted at the top-level form, so it is
        /// "kicad_sch/symbol" rather than "symbol" - the latter matches nothing and would benchmark
        /// an empty enumeration.
        /// </summary>
        [Benchmark(Description = "Query: FindAll(\"kicad_sch/symbol\")")]
        public int QueryFindAll() => _document.FindAll($"{RootToken}/symbol").Count();

        /// <summary>Full recursive walk looking for one token - the expensive query shape.</summary>
        [Benchmark(Description = "Query: Descendants(\"property\")")]
        public int QueryDescendants() => _document.Forms.Sum(form => form.Descendants("property").Count());
    }
}
