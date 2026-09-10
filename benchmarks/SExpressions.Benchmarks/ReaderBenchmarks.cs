using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;

namespace SExpressions.Benchmarks
{
    /// <summary>
    /// The read-only path, both ways: build the tree and walk it once, or stream the text and never
    /// build anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two workloads on purpose, because they answer two different questions and only one of them can
    /// be called "zero allocation".
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>Scan</c> keeps NOTHING -- it counts what it sees. This is where a streaming read allocates
    /// nothing at all, and it is the honest demonstration of the ceiling: nothing here is proportional
    /// to the input.
    /// </description></item>
    /// <item><description>
    /// <c>Extract</c> keeps a record per <c>property</c> form, which is what a consumer actually does.
    /// The strings it returns are the floor: the reader cannot get under them, and neither can
    /// MessagePack, which allocates its output objects too.
    /// </description></item>
    /// </list>
    /// <para>
    /// Both are measured over the whole corpus in one pass rather than one document reparsed, for the
    /// reason <see cref="CorpusPassBenchmarks"/> gives.
    /// </para>
    /// </remarks>
    [MemoryDiagnoser]
    public class ReaderBenchmarks
    {
        private string[] _documents = null!;

        [GlobalSetup]
        public void Setup()
        {
            var root = Corpus.RequireRoot();
            _documents = Corpus.Schematics().Select(r => File.ReadAllText(Path.Combine(root, r))).ToArray();
        }

        // ------------------------------------------------------------------------------- scan only

        [Benchmark(Description = "Scan: count symbols (tree)")]
        public int ScanViaTree()
        {
            var n = 0;
            foreach (var text in _documents)
            {
                foreach (var form in new SExpressionParser().ParseAll(text))
                {
                    n += form.Descendants("symbol").Count();
                }
            }

            return n;
        }

        [Benchmark(Description = "Scan: count symbols (reader)")]
        public int ScanViaReader()
        {
            var n = 0;
            foreach (var text in _documents)
            {
                var reader = new SExpressionReader(text);
                while (reader.Read())
                {
                    if (reader.TokenType == SExpressionTokenType.StartForm && reader.ValueEquals("symbol"))
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        // ---------------------------------------------------------------------------- extract records

        [Benchmark(Description = "Extract: properties (tree)")]
        public int ExtractViaTree()
        {
            var records = new List<(string Name, string Value)>();
            foreach (var text in _documents)
            {
                foreach (var form in new SExpressionParser().ParseAll(text))
                {
                    foreach (var property in form.Descendants("property"))
                    {
                        records.Add((property.GetValue(0) ?? string.Empty, property.GetValue(1) ?? string.Empty));
                    }
                }
            }

            return records.Count;
        }

        [Benchmark(Description = "Extract: properties (reader)")]
        public int ExtractViaReader()
        {
            var records = new List<(string Name, string Value)>();
            foreach (var text in _documents)
            {
                var reader = new SExpressionReader(text);
                while (reader.Read())
                {
                    if (reader.TokenType != SExpressionTokenType.StartForm || !reader.ValueEquals("property"))
                    {
                        continue;
                    }

                    var name = reader.Read() && reader.TokenType == SExpressionTokenType.Atom ? reader.GetString() : string.Empty;
                    var value = reader.Read() && reader.TokenType == SExpressionTokenType.Atom ? reader.GetString() : string.Empty;
                    records.Add((name, value));
                }
            }

            return records.Count;
        }
    }
}
