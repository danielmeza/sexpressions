using System.Diagnostics;

namespace SExpressions.Tests;

/// <summary>
/// Locates the real KiCad 10 corpus and the kicad-cli shim. Every corpus-backed test skips
/// (rather than fails) when the corpus is absent, so the suite still runs on a bare CI agent.
/// </summary>
public static class Corpus
{
    public static readonly string? Root = FindRoot();

    /// <summary>
    /// Corpus files cut short on purpose, relative to the root. They exist to test tools that must
    /// report a file they cannot read, so they are no longer KiCad files and nothing here
    /// round-trips them; <c>CorpusTests</c> checks instead that the parser and the reader both
    /// refuse each one.
    /// </summary>
    /// <remarks>
    /// Named, not detected. A file that stops parsing for any other reason is a parser regression
    /// or a corpus change somebody has to look at, and it must still fail the round-trip tests.
    /// </remarks>
    public static readonly IReadOnlyList<string> BrokenOnPurpose =
    [
        // Cut off inside its (layers block; see the README beside it.
        Path.Combine("tests", "design-rules", "audit-fixtures", "footprint", "board-unreadable", "board-unreadable.kicad_pcb"),
    ];

    private static string? FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("ORBION_KICAD_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return env;
        }

        const string Default = "/home/daniel-meza/Documents/repos/spn/orbion/hwr/orbion-kicad";
        return Directory.Exists(Default) ? Default : null;
    }

    /// <summary>True when the corpus is missing; xunit v2 cannot skip dynamically, so corpus-backed tests return early.</summary>
    public static bool Missing => Root is null;

    public static string RequireRoot()
    {
        Assert.True(Root is not null, "KiCad corpus not present (set ORBION_KICAD_ROOT).");
        return Root!;
    }

    public static string? KiCadCli
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ORBION_KICAD_CLI");
            if (!string.IsNullOrEmpty(env))
            {
                return env;
            }

            if (Root is not null)
            {
                var shim = Path.Combine(Root, "scripts", "kicad-cli");
                if (File.Exists(shim))
                {
                    return shim;
                }
            }

            return null;
        }
    }

    public static string RequireKiCadCli()
    {
        var cli = KiCadCli;
        Assert.True(cli is not null, "kicad-cli not available (set ORBION_KICAD_CLI).");
        return cli!;
    }

    /// <summary>Every schematic in the corpus, relative to the corpus root.</summary>
    public static IEnumerable<string> Schematics()
    {
        if (Root is null)
        {
            yield break;
        }

        foreach (var f in Directory.GetFiles(Path.Combine(Root, "templates"), "*.kicad_sch", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (char.IsLower(Path.GetFileName(f)[0]))
            {
                yield return Path.GetRelativePath(Root, f);
            }
        }

        foreach (var f in Directory.GetFiles(Path.Combine(Root, "blocks"), "*.kicad_sch", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            yield return Path.GetRelativePath(Root, f);
        }
    }

    public static IEnumerable<string> Boards()
    {
        if (Root is null)
        {
            yield break;
        }

        foreach (var dir in new[] { "templates", "blocks", "tests" })
        {
            var full = Path.Combine(Root, dir);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var f in Directory.GetFiles(full, "*.kicad_pcb", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(Root, f);
                if (!BrokenOnPurpose.Contains(relative, StringComparer.Ordinal))
                {
                    yield return relative;
                }
            }
        }
    }

    public static IEnumerable<string> DesignRules()
    {
        if (Root is null)
        {
            yield break;
        }

        foreach (var f in Directory.GetFiles(Path.Combine(Root, "config", "design-rules"), "*.kicad_dru").OrderBy(x => x, StringComparer.Ordinal))
        {
            yield return Path.GetRelativePath(Root, f);
        }
    }

    /// <summary>
    /// The two fixed files the acceptance suite round-trips, which are named rather than discovered.
    /// Like every other enumerator here they must yield nothing when the corpus is absent: a theory
    /// that yields a case the test body cannot then read turns "skipped" into "failed".
    /// </summary>
    private static IEnumerable<string> FixedFiles()
    {
        if (Root is null)
        {
            yield break;
        }

        yield return Path.Combine("libs", "orbion.kicad_sym");
        yield return Path.Combine("config", "worksheets", "orbion-a3.kicad_wks");
    }

    /// <summary>Everything the acceptance suite round-trips: schematics, boards, the symbol library, the worksheet and the design rules.</summary>
    public static IEnumerable<string> All() =>
        Schematics()
            .Concat(Boards())
            .Concat(DesignRules())
            .Concat(FixedFiles());

    public static string Read(string relative) => File.ReadAllText(Path.Combine(RequireRoot(), relative));

    public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
    {
        public string All => StdOut + StdErr;
    }

    /// <summary>How long one kicad-cli run may take before the test gives up on it.</summary>
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(5);

    /// <remarks>
    /// Both streams are read while the process runs, and the timeout is real. The old version read
    /// stdout to the end first, which blocks until kicad-cli exits, so a hung kicad-cli hung the
    /// test run until something killed it -- and a killed run leaves its staging directories
    /// behind. A run that times out is killed, with its children, and fails the test.
    /// </remarks>
    public static CliResult RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo(RequireKiCadCli())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RequireRoot(),
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(CliTimeout))
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
            Assert.Fail($"kicad-cli {string.Join(' ', args)} did not finish within {CliTimeout.TotalMinutes} minutes and was killed.");
        }

        p.WaitForExit();
        return new CliResult(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Copies the project directory that holds <paramref name="relative"/> to a scratch directory so
    /// relative references (lib tables, project file) still resolve, then overwrites the one file.
    /// </summary>
    /// <returns>The copy, which deletes itself when disposed: take it with <c>using var</c>.</returns>
    /// <remarks>
    /// Nothing is left behind by a failure: a copy that fails half way deletes what it wrote, and
    /// with <c>using var</c> a second <c>Stage</c> that throws still disposes the first. A run that
    /// is killed cannot clean up after itself, so each run stages under a directory of its own,
    /// and the next run removes the ones whose process is gone (<see cref="SweepAbandonedRuns"/>).
    /// </remarks>
    public static StagedCopy Stage(string relative, string newContent)
    {
        var srcDir = Path.GetDirectoryName(Path.Combine(RequireRoot(), relative))!;
        return StageInto(RunScratch.Value, srcDir, Path.GetFileName(relative), newContent);
    }

    /// <summary>
    /// The copy <see cref="Stage"/> makes, into a new directory under <paramref name="scratch"/>.
    /// Separate so that its failure path can be tested without the corpus.
    /// </summary>
    public static StagedCopy StageInto(string scratch, string srcDir, string fileName, string newContent)
    {
        var dstDir = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dstDir);
        try
        {
            foreach (var f in Directory.GetFiles(srcDir))
            {
                File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)));
            }

            var target = Path.Combine(dstDir, fileName);
            File.WriteAllText(target, newContent);
            return new StagedCopy(dstDir, target);
        }
        catch
        {
            TryDelete(dstDir);
            throw;
        }
    }

    /// <summary>A staged copy of a project; see <see cref="Stage"/>.</summary>
    public sealed class StagedCopy(string dir, string file) : IDisposable
    {
        /// <summary>The directory holding the copy.</summary>
        public string Dir { get; } = dir;

        /// <summary>The file that was overwritten.</summary>
        public string File { get; } = file;

        /// <summary>
        /// Deletes the copy. Never throws: an exception here would replace the one that failed the
        /// test, and whatever is left the next run's sweep removes with the rest of this run.
        /// </summary>
        public void Dispose() => TryDelete(Dir);
    }

    /// <summary>
    /// Where staging goes: <c>ORBION_SEXPR_SCRATCH</c>, or <c>~/.cache/sexpr-roundtrip</c>.
    /// </summary>
    /// <remarks>
    /// MEASURED: kicad-cli here is a flatpak, and <c>--filesystem=host</c> does NOT expose /tmp --
    /// the sandbox has its own. Staging under $HOME (or ORBION_SEXPR_SCRATCH) is visible to both.
    /// </remarks>
    public static string ScratchRoot =>
        Environment.GetEnvironmentVariable("ORBION_SEXPR_SCRATCH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "sexpr-roundtrip");

    /// <summary>This run's own directory under <see cref="ScratchRoot"/>, created on first use.</summary>
    private static readonly Lazy<string> RunScratch = new(CreateRunScratch);

    private const string RunPrefix = "run-";

    private static string CreateRunScratch()
    {
        var root = ScratchRoot;
        Directory.CreateDirectory(root);
        SweepAbandonedRuns(root);

        var dir = Path.Combine(root, RunPrefix + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        // A run that ends normally removes its own directory; one that is killed leaves it for the
        // next run's sweep.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(dir);
        return dir;
    }

    /// <summary>
    /// Deletes every <c>run-&lt;pid&gt;</c> directory under <paramref name="root"/> whose process no
    /// longer exists: what a run that was killed, or crashed, could not delete itself.
    /// </summary>
    /// <param name="root">The scratch root to sweep.</param>
    /// <returns>The directories deleted.</returns>
    /// <remarks>
    /// Only this layout is touched. A directory with a live process behind it is left alone even if
    /// that process is not a test run -- a reused PID costs a leftover, never another run's files --
    /// and anything not named <c>run-&lt;pid&gt;</c>, such as the bare GUID directories older
    /// versions of this class staged straight into the root, is not this sweep's to judge.
    /// </remarks>
    public static IReadOnlyList<string> SweepAbandonedRuns(string root)
    {
        var deleted = new List<string>();
        foreach (var dir in Directory.GetDirectories(root, RunPrefix + "*"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name.AsSpan(RunPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)
                || pid == Environment.ProcessId
                || IsRunning(pid))
            {
                continue;
            }

            if (TryDelete(dir))
            {
                deleted.Add(dir);
            }
        }

        return deleted;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Strips the lines a netlist export stamps with the source path, the run date and the tool
    /// version, and rewrites the staging directory out of the "Sheetfile" property KiCad emits as a
    /// path relative to the working directory.
    /// </summary>
    public static string NormalizeNetlist(string netlist, string stagingDir)
    {
        netlist = netlist.Replace(stagingDir, "<staged>", StringComparison.Ordinal)
                         .Replace(Path.GetFileName(stagingDir), "<staged>", StringComparison.Ordinal);

        var lines = netlist.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', lines.Where(l =>
        {
            var t = l.TrimStart();
            return !t.StartsWith("(source ", StringComparison.Ordinal)
                && !t.StartsWith("(date ", StringComparison.Ordinal)
                && !t.StartsWith("(tool ", StringComparison.Ordinal);
        }));
    }
}
