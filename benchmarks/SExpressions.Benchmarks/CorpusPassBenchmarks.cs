using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;

namespace SExpressions.Benchmarks
{
    /// <summary>
    /// The same parse as <see cref="SExpressionBenchmarks.Parse"/>, but over DISTINCT documents
    /// rather than one document reparsed. One measured operation is one pass over the whole corpus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED, and why this exists: every other benchmark in this project reparses ONE document.
    /// That keeps the parser's per-thread atom cache maximally warm, and it lets any per-document
    /// sizing decision be learned once and reused. A consumer parses different files. PR #17 measured
    /// the same change at -5.8% on the single-document shape and -7.9% on a rotation, so the two
    /// shapes are not interchangeable and an allocation change has to be read on both.
    /// </para>
    /// <para>
    /// MEASURED TRAP, and the reason one operation is a whole pass rather than one document taken
    /// from a rotating cursor: a cursor has to be reset somewhere, and resetting it in
    /// <c>[IterationSetup]</c> makes BenchmarkDotNet drop to <c>InvocationCount=1</c>. Every iteration
    /// then parses the FIRST document only -- the rotation never happens -- and the reported mean was
    /// 2.9 ms against the ~390 us that document actually takes, because a single un-unrolled
    /// invocation is mostly harness. Letting the cursor run free instead leaves the mean depending on
    /// where the previous iteration stopped. A whole pass has neither problem: it is deterministic,
    /// it covers every document exactly once, and per-document figures are the row divided by
    /// <see cref="DocumentCount"/>.
    /// </para>
    /// </remarks>
    [MemoryDiagnoser]
    public class CorpusPassBenchmarks
    {
        private string[] _documents = null!;

        /// <summary>How many documents one measured operation covers. Printed with the run.</summary>
        public int DocumentCount => _documents.Length;

        [GlobalSetup]
        public void Setup()
        {
            var root = Corpus.RequireRoot();
            _documents = Corpus.Schematics().Select(r => File.ReadAllText(Path.Combine(root, r))).ToArray();
            Console.WriteLine(
                $"// corpus pass: {_documents.Length} schematics, "
                + $"{_documents.Sum(d => (long)d.Length) / 1024.0:F0} KB total, "
                + $"{_documents.Sum(d => (long)d.Length) / 1024.0 / _documents.Length:F0} KB average");
        }

        /// <summary>Full-fidelity parse of every schematic in the corpus, once each.</summary>
        [Benchmark(Description = "Parse (corpus pass)")]
        public int ParseCorpusPass()
        {
            var forms = 0;
            foreach (var text in _documents)
            {
                forms += new SExpressionParser().ParseAll(text).Count;
            }

            return forms;
        }

        /// <summary>Parse and write back every schematic in the corpus, once each.</summary>
        [Benchmark(Description = "Round trip (corpus pass)")]
        public int RoundTripCorpusPass()
        {
            var characters = 0;
            foreach (var text in _documents)
            {
                characters += new SExpressionParser().ParseAll(text).ToText().Length;
            }

            return characters;
        }
    }
}
