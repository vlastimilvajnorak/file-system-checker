using FileSystemChangeTracker.Api.Models;

namespace FileSystemChangeTracker.Tests;

/// <summary>
/// Skupina: zrušený průchod — zpracovaná část se eviduje od prvního zhashování,
/// nestihnutý zbytek si podrží poslední známý stav a výsledek je označen jako částečný.
/// </summary>
public class SnapshotComparerCancelledScanTests : SnapshotComparerTestBase
{
    [Fact]
    public void CancelledScan_CarriesUnscannedSubtreeAndFile_AndReportsPartial()
    {
        // Zrušený průchod: zpracovaná část je čerstvá, nestihnuté soubory/podstromy se přenesou
        // z minula beze změny — nic se nehlásí jako smazané a výsledek je označen jako částečný.
        var previous = Snapshot(
            ["done", "later"],
            Record("done/a.txt", "h1", version: 2),
            Record("done/skipped.txt", "h2", version: 4),
            Record("later/b.txt", "h3", version: 3));
        ScanResult current = new()
        {
            WasCancelled = true,
            Files = [File("done/a.txt", "h1")],
            Directories = ["done", "later"],
            UnscannedFiles = ["done/skipped.txt"],
            UnscannedDirectories = ["later"],
        };

        var outcome = Comparer.Compare(Root, previous, current);

        Assert.True(outcome.Result.Partial);
        Assert.Empty(outcome.Result.DeletedFiles);
        Assert.Empty(outcome.Result.DeletedDirectories);
        Assert.Equal(3, outcome.Snapshot.Files.Count);
        Assert.Equal(4, outcome.Snapshot.Files.Single(file => file.RelativePath == "done/skipped.txt").Version);
        Assert.Equal(3, outcome.Snapshot.Files.Single(file => file.RelativePath == "later/b.txt").Version);
    }

    [Fact]
    public void CancelledBaseline_TracksScannedFilesOnly()
    {
        // Zrušený první běh: baseline vznikne ze zpracované části — soubor je evidovaný
        // od prvního zhashování. Nestihnuté soubory se objeví jako nové až příště.
        ScanResult current = new()
        {
            WasCancelled = true,
            Files = [File("scanned.txt", "h1")],
            Directories = [],
            UnscannedFiles = ["pending.txt"],
        };

        var outcome = Comparer.Compare(Root, previous: null, current);

        Assert.True(outcome.Result.InitialScan);
        Assert.True(outcome.Result.Partial);
        var tracked = Assert.Single(outcome.Snapshot.Files);
        Assert.Equal("scanned.txt", tracked.RelativePath);
        Assert.Equal(1, tracked.Version);
    }
}
