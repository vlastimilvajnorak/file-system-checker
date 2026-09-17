using System.Diagnostics;
using FileSystemChangeTracker.Api.Models;

namespace FileSystemChangeTracker.Tests;

/// <summary>
/// Skupina: nečitelné položky (zámek, oprávnění) — poslední známý stav se přenáší,
/// nikdy nevzniká falešné „smazáno" ani reset verzí.
/// </summary>
public class SnapshotComparerUnreadableTests : SnapshotComparerTestBase
{
    [Fact]
    public void UnreadableFile_KeepsPreviousRecord_AndIsNotReportedDeleted()
    {
        // Soubor existuje, ale nešel přečíst (např. zamčený). Nesmí se nahlásit jako smazaný
        // a jeho záznam (včetně verze) se přenese do nového snapshotu beze změny.
        var previous = Snapshot(Record("locked.txt", "h1", version: 3));
        var current = ScanWithUnreadable(["locked.txt"]);

        var outcome = Comparer.Compare(Root, previous, current);

        Assert.Empty(outcome.Result.DeletedFiles);
        Assert.Empty(outcome.Result.NewFiles);
        Assert.Empty(outcome.Result.ModifiedFiles);
        var kept = Assert.Single(outcome.Snapshot.Files);
        Assert.Equal("locked.txt", kept.RelativePath);
        Assert.Equal(3, kept.Version);
    }

    [Fact]
    public void UnreadableFile_WithoutHistory_IsNotAddedToSnapshot()
    {
        // Nový, ale nečitelný soubor nejde zaevidovat (není znám obsah). Objeví se jako nový
        // až při prvním čitelném běhu.
        var previous = Snapshot(Record("other.txt", "h1", version: 1));
        var current = ScanWithUnreadable(["mystery.txt"], File("other.txt", "h1"));

        var outcome = Comparer.Compare(Root, previous, current);

        Assert.Empty(outcome.Result.NewFiles);
        var only = Assert.Single(outcome.Snapshot.Files);
        Assert.Equal("other.txt", only.RelativePath);
    }

    [Fact]
    public void UnreadableDirectory_KeepsWholeSubtree_AndReportsNothingDeleted()
    {
        // Adresář nešel projít (odebraná práva, zamčená položka při enumeraci). Celý jeho
        // podstrom — soubory i podadresáře — se přenese z minula a nic se nehlásí jako smazané.
        var previous = Snapshot(
            ["locked", "locked/sub"],
            Record("locked/a.txt", "h1", version: 2),
            Record("locked/sub/b.txt", "h2", version: 5),
            Record("other.txt", "h3", version: 1));
        ScanResult current = new()
        {
            Files = [File("other.txt", "h3")],
            Directories = ["locked"], // rodič ho vyjmenoval, dovnitř se nedalo
            UnreadableDirectories = ["locked"],
        };

        var outcome = Comparer.Compare(Root, previous, current);

        Assert.Empty(outcome.Result.DeletedFiles);
        Assert.Empty(outcome.Result.DeletedDirectories);
        Assert.Empty(outcome.Result.NewFiles);
        Assert.Equal(3, outcome.Snapshot.Files.Count);
        Assert.Equal(5, outcome.Snapshot.Files.Single(file => file.RelativePath == "locked/sub/b.txt").Version);
        Assert.Contains("locked/sub", outcome.Snapshot.Directories);
    }

    [Fact]
    public void CarriedDirectory_MatchesOnlyRealAncestors_NotNamePrefixes()
    {
        // "ab" neprojitý: "ab/x.txt" i "ab/sub/y.txt" se přenesou, ale "abc/z.txt" (jen textový
        // prefix) je skutečně smazaný. Adresář "ab" sám se nepočítá za svého předka.
        var previous = Snapshot(
            ["ab", "ab/sub", "abc"],
            Record("ab/x.txt", "h1", version: 1),
            Record("ab/sub/y.txt", "h2", version: 2),
            Record("abc/z.txt", "h3", version: 3));
        ScanResult current = new()
        {
            Files = [],
            Directories = ["ab"],
            UnreadableDirectories = ["ab"],
        };

        var outcome = Comparer.Compare(Root, previous, current);

        Assert.Equal("abc/z.txt", Assert.Single(outcome.Result.DeletedFiles).Path);
        Assert.Equal("abc", Assert.Single(outcome.Result.DeletedDirectories).Path);
        Assert.Equal(2, outcome.Snapshot.Files.Count);
        Assert.Contains("ab/sub", outcome.Snapshot.Directories);
    }

    [Fact]
    public void ManyCarriedDirectories_CompareStaysFast()
    {
        // Zrušené BFS nad velkým stromem: desítky tisíc nestihnutých adresářů a statisíce
        // souborů v minulém snapshotu. Lineární prefixové porovnání by trvalo hodiny a skener
        // by nešel zrušit; dotaz po rodičích cesty musí projít v řádu sekundy.
        const int directoryCount = 50_000;
        const int filesPerDirectory = 6;
        var directories = Enumerable.Range(0, directoryCount).Select(index => $"d{index / 100}/s{index}").ToArray();
        var files = directories
            .SelectMany(directory => Enumerable.Range(0, filesPerDirectory).Select(file => Record($"{directory}/f{file}.bin", "h", version: 1)))
            .ToArray();
        var previous = Snapshot(directories, files);
        ScanResult current = new()
        {
            WasCancelled = true,
            Files = [],
            Directories = directories,
            UnscannedDirectories = directories,
        };

        var started = Stopwatch.GetTimestamp();
        var outcome = Comparer.Compare(Root, previous, current);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Empty(outcome.Result.DeletedFiles);
        Assert.Equal(files.Length, outcome.Snapshot.Files.Count);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Porovnání trvalo {elapsed.TotalSeconds:F1} s.");
    }

    [Fact]
    public void UnreadableDirectory_DoesNotShadowRealDeletions_Elsewhere()
    {
        // Nedostupnost jednoho podstromu nesmí zamaskovat skutečné smazání jinde.
        var previous = Snapshot(
            ["locked"],
            Record("locked/a.txt", "h1", version: 1),
            Record("gone.txt", "h2", version: 3));
        ScanResult current = new()
        {
            Files = [],
            Directories = ["locked"],
            UnreadableDirectories = ["locked"],
        };

        var outcome = Comparer.Compare(Root, previous, current);

        var deleted = Assert.Single(outcome.Result.DeletedFiles);
        Assert.Equal("gone.txt", deleted.Path);
        Assert.Equal(3, deleted.LastKnownVersion);
    }
}
