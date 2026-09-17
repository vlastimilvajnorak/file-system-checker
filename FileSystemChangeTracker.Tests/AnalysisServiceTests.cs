using FileSystemChangeTracker.Api.Models;
using FileSystemChangeTracker.Api.Options;
using FileSystemChangeTracker.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSystemChangeTracker.Tests;

/// <summary>
/// Testy orchestrace analýzy: kdy se rozpracovaný stav (merge po zrušení, checkpoint) ukládá
/// a kdy záměrně ne, publikace živě detekovaných změn z mezistavů skeneru a chování nad
/// poškozeným snapshotem.
/// </summary>
public class AnalysisServiceTests
{
    private static readonly string _root = Path.Combine(Path.GetTempPath(), "service-tests");

    private sealed class StubScanner : IDirectoryScanner
    {
        public required ScanResult Result { get; init; }
        public ScanResult? Intermediate { get; init; }

        public async Task<ScanResult> ScanAsync(
            string normalizedRoot,
            AnalysisProgress progress,
            Func<ScanResult, CancellationToken, Task>? checkpointAsync,
            CancellationToken cancellationToken)
        {
            if ((Intermediate is not null) && (checkpointAsync is not null))
            {
                await checkpointAsync(Intermediate, cancellationToken);
            }

            return Result;
        }
    }

    private sealed class RecordingStore : ISnapshotStore
    {
        public string StorageDirectory => Path.Combine(Path.GetTempPath(), "snapshots-mimo-strom");

        /// <summary>Snapshot, který LoadAsync vrátí jako minulý stav; null = první běh.</summary>
        public DirectorySnapshot? Previous { get; set; }

        /// <summary>Simulace poškozeného snapshotu na disku.</summary>
        public bool Corrupted { get; set; }

        public List<(DirectorySnapshot Snapshot, bool TokenCanBeCanceled)> Saves { get; } = [];

        public Task<DirectorySnapshot?> LoadAsync(string snapshotKey, CancellationToken cancellationToken) =>
            Corrupted
                ? throw new SnapshotCorruptedException("vadny.json", new InvalidOperationException("simulace"))
                : Task.FromResult(Previous);

        public Task SaveAsync(string snapshotKey, DirectorySnapshot snapshot, CancellationToken cancellationToken)
        {
            Saves.Add((snapshot, cancellationToken.CanBeCanceled));
            return Task.CompletedTask;
        }

        public Task<SnapshotMetadata?> GetMetadataAsync(string snapshotKey, CancellationToken cancellationToken) =>
            Task.FromResult<SnapshotMetadata?>(null);

        public Task<bool> DeleteAsync(string snapshotKey, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private static ScannedFile File(string relativePath) => new()
    {
        RelativePath = relativePath,
        Hash = "h-" + relativePath,
    };

    private static DirectorySnapshot PreviousSnapshot(PartialBaselineReason? partialBaseline = null) => new()
    {
        Root = _root,
        Files = [new FileRecord { RelativePath = "done.txt", Hash = "h-done.txt", Version = 1 }],
        PartialBaseline = partialBaseline,
    };

    private static AnalysisService CreateService(StubScanner scanner, RecordingStore store, TimeSpan? checkpointInterval = null)
    {
        var pathPolicy = new PathPolicy();
        var analysisOptions = new AnalysisOptions();
        if (checkpointInterval is { } interval)
        {
            analysisOptions.CheckpointInterval = interval;
        }

        return new AnalysisService(
            pathPolicy,
            scanner,
            new SnapshotComparer(pathPolicy),
            store,
            Microsoft.Extensions.Options.Options.Create(analysisOptions),
            NullLogger<AnalysisService>.Instance);
    }

    [Fact]
    public async Task CancelledScan_SavesMergeSnapshot_EvenWithCancelledToken()
    {
        var scanner = new StubScanner
        {
            Result = new ScanResult { WasCancelled = true, Files = [File("done.txt")], UnscannedFiles = ["later.txt"] },
        };
        var store = new RecordingStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await CreateService(scanner, store).AnalyzeAsync(_root, new AnalysisProgress(), cancellation.Token);

        Assert.True(result.Partial);
        var save = Assert.Single(store.Saves);
        // Zpracovaná část se musí uložit i po zrušení — proto zápis běží s neaktivním tokenem.
        Assert.False(save.TokenCanBeCanceled);
        Assert.Equal("done.txt", Assert.Single(save.Snapshot.Files).RelativePath);
        // Zrušený první běh neměl z čeho přebírat → baseline je označená jako neúplná.
        Assert.Equal(PartialBaselineReason.Cancelled, save.Snapshot.PartialBaseline);
    }

    [Fact]
    public async Task IntermediateState_PublishesLiveResult_WithoutPrematureSave()
    {
        var scanner = new StubScanner
        {
            Intermediate = new ScanResult { WasCancelled = true, Files = [File("early.txt")] },
            Result = new ScanResult { Files = [File("early.txt"), File("late.txt")] },
        };
        var store = new RecordingStore();
        var progress = new AnalysisProgress();

        var result = await CreateService(scanner, store).AnalyzeAsync(_root, progress, CancellationToken.None);

        // Živý výsledek se publikoval z mezistavu; na disk se (v rámci CheckpointInterval) uložil jen finál.
        Assert.NotNull(progress.LiveResult);
        // Mezistav není zrušená analýza — příznak Partial patří jen finálnímu výsledku.
        Assert.False(progress.LiveResult.Partial);
        Assert.False(result.Partial);
        Assert.Single(store.Saves);
        Assert.Equal(2, store.Saves[0].Snapshot.Files.Count);
    }

    [Fact]
    public async Task LateCancellation_AfterCompletedScan_StillSavesSnapshot()
    {
        // Zrušení, které dorazí až po dokončení skenu: drahá práce je hotová a finální zápis
        // běží s neaktivním tokenem — výsledek celého běhu se nesmí zahodit kvůli milisekundám.
        var scanner = new StubScanner { Result = new ScanResult { Files = [File("done.txt")] } };
        var store = new RecordingStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await CreateService(scanner, store).AnalyzeAsync(_root, new AnalysisProgress(), cancellation.Token);

        Assert.False(result.Partial);
        var save = Assert.Single(store.Saves);
        Assert.False(save.TokenCanBeCanceled);
        Assert.Null(save.Snapshot.PartialBaseline);
    }

    [Fact]
    public async Task CompletedRun_ClearsPartialBaselineFlag()
    {
        // Dokončený běh zeskenoval celý strom — baseline je od té chvíle kompletní,
        // i když ta minulá vznikla z nedokončeného prvního běhu.
        var scanner = new StubScanner { Result = new ScanResult { Files = [File("done.txt"), File("later.txt")] } };
        var store = new RecordingStore { Previous = PreviousSnapshot(PartialBaselineReason.Cancelled) };

        await CreateService(scanner, store).AnalyzeAsync(_root, new AnalysisProgress(), CancellationToken.None);

        Assert.Null(Assert.Single(store.Saves).Snapshot.PartialBaseline);
    }

    [Fact]
    public async Task CancelledRun_OverCompleteBaseline_ReturnsPartialResult_ButLeavesSnapshotUntouched()
    {
        // Nad kompletní baseline se zrušený běh neukládá: příští běh stejně hashuje vše znovu
        // a uložený merge by "spotřeboval" změny, které se (např. při zastavení aplikace)
        // nikomu nenahlásily. Částečný výsledek se ale klientovi vrátí.
        var scanner = new StubScanner
        {
            Result = new ScanResult
            {
                WasCancelled = true,
                Files = [File("done.txt") with { Hash = "h-zmena" }],
                UnscannedFiles = ["later.txt"],
            },
        };
        var store = new RecordingStore { Previous = PreviousSnapshot() };

        var result = await CreateService(scanner, store).AnalyzeAsync(_root, new AnalysisProgress(), CancellationToken.None);

        Assert.True(result.Partial);
        Assert.Equal("done.txt", Assert.Single(result.ModifiedFiles).Path);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task CancelledRun_OverPartialBaseline_ExtendsBaseline_AndKeepsPartialFlag()
    {
        // Navazuje-li zrušený běh na neúplnou baseline, zpracovaná část ji rozšiřuje (uloží se),
        // ale nestihnutá část pořád není pokrytá — příznak nesmí zmizet dřív než po prvním
        // dokončeném běhu.
        var scanner = new StubScanner
        {
            Result = new ScanResult { WasCancelled = true, Files = [File("novy.txt")], UnscannedFiles = ["done.txt"] },
        };
        var store = new RecordingStore { Previous = PreviousSnapshot(PartialBaselineReason.Interrupted) };

        await CreateService(scanner, store).AnalyzeAsync(_root, new AnalysisProgress(), CancellationToken.None);

        var save = Assert.Single(store.Saves);
        Assert.Equal(PartialBaselineReason.Cancelled, save.Snapshot.PartialBaseline);
        Assert.Equal(2, save.Snapshot.Files.Count);
    }

    [Fact]
    public async Task CheckpointDuringFirstRun_MarksBaselineInterrupted()
    {
        // Když checkpoint přežije jako poslední zápis (pád procesu), musí nést příznak
        // přerušení; finální uložení dokončeného běhu ho zase smaže.
        var scanner = new StubScanner
        {
            Intermediate = new ScanResult { WasCancelled = true, Files = [File("early.txt")] },
            Result = new ScanResult { Files = [File("early.txt"), File("late.txt")] },
        };
        var store = new RecordingStore();

        await CreateService(scanner, store, checkpointInterval: TimeSpan.FromTicks(1))
            .AnalyzeAsync(_root, new AnalysisProgress(), CancellationToken.None);

        Assert.Equal(2, store.Saves.Count);
        Assert.Equal(PartialBaselineReason.Interrupted, store.Saves[0].Snapshot.PartialBaseline);
        Assert.Null(store.Saves[1].Snapshot.PartialBaseline);
    }

    [Fact]
    public async Task CheckpointOverCompleteBaseline_IsNotSaved()
    {
        // Nad kompletním minulým stavem checkpoint nic nešetří (příští běh hashuje vše znovu)
        // a po pádu procesu by pohltil nenahlášené změny — ukládá se až finální výsledek.
        var scanner = new StubScanner
        {
            Intermediate = new ScanResult { WasCancelled = true, Files = [File("done.txt")] },
            Result = new ScanResult { Files = [File("done.txt")] },
        };
        var store = new RecordingStore { Previous = PreviousSnapshot() };
        var progress = new AnalysisProgress();

        await CreateService(scanner, store, checkpointInterval: TimeSpan.FromTicks(1))
            .AnalyzeAsync(_root, progress, CancellationToken.None);

        Assert.NotNull(progress.LiveResult); // živé změny se publikují dál
        var save = Assert.Single(store.Saves);
        Assert.Null(save.Snapshot.PartialBaseline);
    }

    [Fact]
    public async Task CorruptSnapshot_StartsNewBaseline_AndWarns()
    {
        // Poškozený snapshot nesmí analýzu shodit ani zmizet potichu: běh se chová jako první
        // (baseline se založí znovu, verze od 1) a varování je ve výsledku i v živém mezistavu.
        var scanner = new StubScanner
        {
            Intermediate = new ScanResult { WasCancelled = true, Files = [File("a.txt")] },
            Result = new ScanResult { Files = [File("a.txt")] },
        };
        var store = new RecordingStore { Corrupted = true };
        var progress = new AnalysisProgress();

        var result = await CreateService(scanner, store).AnalyzeAsync(_root, progress, CancellationToken.None);

        Assert.True(result.InitialScan);
        Assert.Contains(result.Warnings, warning => warning.Contains("poškozený", StringComparison.Ordinal));
        Assert.NotNull(progress.LiveResult);
        Assert.Contains(progress.LiveResult.Warnings, warning => warning.Contains("poškozený", StringComparison.Ordinal));
        var save = Assert.Single(store.Saves);
        Assert.Equal(1, Assert.Single(save.Snapshot.Files).Version);
    }
}
