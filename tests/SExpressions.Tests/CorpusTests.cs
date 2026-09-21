using System.Diagnostics;

namespace SExpressions.Tests;

/// <summary>
/// The corpus harness itself: which files it hands to the round-trip tests (#32), and what it
/// leaves on disk when a run fails or is killed.
/// </summary>
public class CorpusTests
{
    public static TheoryData<string> BrokenOnPurpose()
    {
        var d = new TheoryData<string>();
        if (!Corpus.Missing)
        {
            foreach (var f in Corpus.BrokenOnPurpose)
            {
                d.Add(f);
            }
        }

        if (d.Count == 0)
        {
            d.Add("<no corpus>");
        }

        return d;
    }

    /// <summary>
    /// A file the corpus cut short on purpose is not skipped silently: it is taken out of the
    /// round-trip tests by name, and here both parsers must refuse it, having run out of input
    /// rather than tripped over anything in it. When the fixture changes -- fixed, moved, or cut
    /// somewhere else -- this fails and the list has to be looked at again.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrokenOnPurpose))]
    public void AFileCutShortOnPurpose_IsRefusedByTheParserAndTheReader_AndRoundTrippedByNothing(string relative)
    {
        if (relative == "<no corpus>")
        {
            return;
        }

        var path = Path.Combine(Corpus.RequireRoot(), relative);
        Assert.True(File.Exists(path), $"{relative} is in Corpus.BrokenOnPurpose but not in the corpus; take it off the list.");
        var text = File.ReadAllText(path);

        var tree = Assert.Throws<SExpressionFormatException>(() => SDocument.Parse(text));
        Assert.StartsWith("Unexpected end of input", tree.Message, StringComparison.Ordinal);
        Assert.Equal(text.Length, tree.Position);

        var reader = Assert.Throws<SExpressionFormatException>(() =>
        {
            var r = new SExpressionReader(text);
            while (r.Read())
            {
            }
        });
        Assert.StartsWith("Unexpected end of input", reader.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(relative, Corpus.All());
    }

    [Fact]
    public void AStagedCopy_DeletesItselfWhenDisposed()
    {
        var scratch = NewScratch();
        var source = NewScratch();
        try
        {
            File.WriteAllText(Path.Combine(source, "board.kicad_pcb"), "(kicad_pcb)");
            File.WriteAllText(Path.Combine(source, "board.kicad_pro"), "{}");

            string dir;
            using (var staged = Corpus.StageInto(scratch, source, "board.kicad_pcb", "(kicad_pcb (version 1))"))
            {
                dir = staged.Dir;
                Assert.Equal("(kicad_pcb (version 1))", File.ReadAllText(staged.File));
                Assert.True(File.Exists(Path.Combine(dir, "board.kicad_pro")));
            }

            Assert.False(Directory.Exists(dir));
            Assert.Empty(Directory.GetFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void AStagingThatFailsHalfWay_LeavesNothingBehind()
    {
        var scratch = NewScratch();
        try
        {
            var missing = Path.Combine(scratch, "no-such-project");
            Assert.Throws<DirectoryNotFoundException>(() => Corpus.StageInto(scratch, missing, "x.kicad_sch", "(kicad_sch)"));

            Assert.Empty(Directory.GetFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// What a killed run leaves -- its own <c>run-&lt;pid&gt;</c> directory -- the next run removes.
    /// Nothing else is touched: this process's directory, one whose process is alive, and any
    /// directory not in that layout, such as the bare GUID ones older versions staged into.
    /// </summary>
    [Fact]
    public void TheSweep_RemovesOnlyTheRunsWhoseProcessIsGone()
    {
        var root = NewScratch();
        try
        {
            var gone = Directory.CreateDirectory(Path.Combine(root, $"run-{int.MaxValue}")).FullName;
            File.WriteAllText(Path.Combine(gone, "left-behind.kicad_pcb"), "(kicad_pcb)");
            var self = Directory.CreateDirectory(Path.Combine(root, $"run-{Environment.ProcessId}")).FullName;
            var legacy = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
            var notARun = Directory.CreateDirectory(Path.Combine(root, "run-not-a-pid")).FullName;

            using var alive = Process.GetCurrentProcess().Id == 1 ? null : TryGetProcess(1);
            var otherLive = alive is null ? null : Directory.CreateDirectory(Path.Combine(root, "run-1")).FullName;

            var deleted = Corpus.SweepAbandonedRuns(root);

            Assert.Equal([gone], deleted);
            Assert.False(Directory.Exists(gone));
            Assert.True(Directory.Exists(self));
            Assert.True(Directory.Exists(legacy));
            Assert.True(Directory.Exists(notARun));
            if (otherLive is not null)
            {
                Assert.True(Directory.Exists(otherLive));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process? TryGetProcess(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string NewScratch() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sexpr-corpus-{Guid.NewGuid():N}")).FullName;
}
