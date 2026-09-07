using BenchmarkDotNet.Running;

namespace SExpressions.Benchmarks
{
    /// <summary>
    /// Entry point. Run from the repository root with:
    /// <c>dotnet run -c Release --project benchmarks/SExpressions.Benchmarks</c>
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args) =>
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
