using System;
using System.IO;

namespace SExpressions.Benchmarks
{
    /// <summary>
    /// One real KiCad file held in memory for the duration of a benchmark run.
    /// </summary>
    /// <remarks>
    /// The benchmarks measure real input on purpose: a synthetic s-expression has uniform token
    /// lengths and nesting, and the parser's cost is dominated by exactly the things a synthetic
    /// input flattens out (quoted atoms, deep nesting under <c>lib_symbols</c>, long float lists).
    /// </remarks>
    public sealed class CorpusFile
    {
        public CorpusFile(string label, string relativePath)
        {
            Label = label;
            RelativePath = relativePath;
            Text = File.ReadAllText(Path.Combine(Corpus.RequireRoot(), relativePath));
        }

        public string Label { get; }

        public string RelativePath { get; }

        public string Text { get; }

        /// <summary>BenchmarkDotNet prints this in the parameter column, so keep it short.</summary>
        public override string ToString() => $"{Label} ({Text.Length / 1024.0:F0} KB)";
    }

    /// <summary>
    /// Locates the real KiCad 10 corpus. Unlike the test suite, which skips corpus-backed tests so a
    /// bare CI agent still runs the unit tests, the benchmarks fail loudly when the corpus is absent:
    /// a benchmark run over substitute input would produce numbers that look valid and mean nothing.
    /// </summary>
    public static class Corpus
    {
        public const string LargeSchematic = "templates/orbion-esp32s3/orbion-esp32s3.kicad_sch";
        public const string SmallSchematic = "blocks/orbion-mcu.kicad_blocks/console-pads.kicad_block/console-pads.kicad_sch";

        public static string RequireRoot()
        {
            var env = Environment.GetEnvironmentVariable("ORBION_KICAD_ROOT");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            {
                return env;
            }

            const string Default = "/home/daniel-meza/Documents/repos/spn/orbion/hwr/orbion-kicad";
            if (Directory.Exists(Default))
            {
                return Default;
            }

            throw new DirectoryNotFoundException(
                "The KiCad corpus is required to run the benchmarks. Set ORBION_KICAD_ROOT to a checkout of the orbion-kicad repository.");
        }
    }
}
