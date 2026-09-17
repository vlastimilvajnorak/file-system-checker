using FileSystemChangeTracker.Api.Models;
using FileSystemChangeTracker.Api.Services;

namespace FileSystemChangeTracker.Tests;

/// <summary>
/// Testy průchodu stromem nad skutečným (dočasným) filesystémem, s falešným hasherem,
/// který umí simulovat nečitelné a zmizelé soubory i zrušení uprostřed práce.
/// </summary>
public class DirectoryScannerTests : IDisposable
{
    private readonly string _root;
    private readonly PathPolicy _pathPolicy = new();

    public DirectoryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scanner-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "obsah a");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "obsah b");
        File.WriteAllText(Path.Combine(_root, "sub", "c.txt"), "obsah c");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeFileHasher : IFileHasher
    {
        /// <summary>Vrátí výjimku, kterou má hashování dané cesty vyhodit; null = úspěch.</summary>
        public Func<string, Exception?>? FailureFor { get; set; }

        /// <summary>Zavolá se před hashováním každého souboru (např. pro vyvolání zrušení).</summary>
        public Action<string>? OnHashing { get; set; }

        public async Task<string> ComputeHashAsync(string fullPath, Func<int, ValueTask>? reportBytesReadAsync, CancellationToken cancellationToken)
        {
            OnHashing?.Invoke(fullPath);
            var failure = FailureFor?.Invoke(fullPath);
            if (failure is not null)
            {
                throw failure;
            }

            if (reportBytesReadAsync is not null)
            {
                await reportBytesReadAsync(1);
            }

            return "hash-" + Path.GetFileName(fullPath);
        }
    }

    /// <summary>Skeneru stačí ze store znát umístění úložiště (adresář, který má přeskočit).</summary>
    private sealed class StubSnapshotStore(string storageDirectory) : ISnapshotStore
    {
        public string StorageDirectory { get; } = storageDirectory;

        public Task<DirectorySnapshot?> LoadAsync(string snapshotKey, CancellationToken cancellationToken) =>
            Task.FromResult<DirectorySnapshot?>(null);

        public Task SaveAsync(string snapshotKey, DirectorySnapshot snapshot, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<SnapshotMetadata?> GetMetadataAsync(string snapshotKey, CancellationToken cancellationToken) =>
            Task.FromResult<SnapshotMetadata?>(null);

        public Task<bool> DeleteAsync(string snapshotKey, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private DirectoryScanner CreateScanner(FakeFileHasher fakeHasher, string? storageDirectory = null) =>
        new(_pathPolicy, fakeHasher, new StubSnapshotStore(storageDirectory ?? Path.Combine(Path.GetTempPath(), "snapshots-mimo-strom")));

    private Task<ScanResult> ScanAsync(FakeFileHasher fakeHasher, CancellationToken cancellationToken = default) =>
        CreateScanner(fakeHasher)
            .ScanAsync(_pathPolicy.NormalizeRoot(_root), new AnalysisProgress(), checkpointAsync: null, cancellationToken);

    [Fact]
    public async Task Scan_ReturnsAllFilesAndDirectories_WithForwardSlashPaths()
    {
        var scan = await ScanAsync(new FakeFileHasher());

        Assert.False(scan.WasCancelled);
        Assert.Equal(["a.txt", "b.txt", "sub/c.txt"], scan.Files.Select(file => file.RelativePath).Order());
        Assert.Equal(["sub"], scan.Directories);
        Assert.Empty(scan.UnreadableFiles);
        Assert.Empty(scan.Warnings);
    }

    [Fact]
    public async Task UnreadableFile_IsRecordedWithWarning_AndScanContinues()
    {
        var fakeHasher = new FakeFileHasher
        {
            FailureFor = path => path.EndsWith("a.txt", StringComparison.Ordinal) ? new IOException("zamčeno") : null,
        };

        var scan = await ScanAsync(fakeHasher);

        Assert.Equal(["a.txt"], scan.UnreadableFiles);
        Assert.DoesNotContain("a.txt", scan.Files.Select(file => file.RelativePath));
        Assert.Contains(scan.Warnings, warning => warning.Contains("a.txt"));
        Assert.Equal(2, scan.Files.Count);
    }

    [Fact]
    public async Task VanishedFile_IsSkippedSilently()
    {
        // Soubor smazaný mezi enumerací a čtením je skutečně pryč — žádný záznam, žádné varování.
        var fakeHasher = new FakeFileHasher
        {
            FailureFor = path => path.EndsWith("b.txt", StringComparison.Ordinal) ? new FileNotFoundException() : null,
        };

        var scan = await ScanAsync(fakeHasher);

        Assert.DoesNotContain("b.txt", scan.Files.Select(file => file.RelativePath));
        Assert.Empty(scan.UnreadableFiles);
        Assert.Empty(scan.Warnings);
    }

    [Fact]
    public async Task CancelledScan_ReturnsPartialState_WithUnscannedRest()
    {
        using var cancellation = new CancellationTokenSource();
        var fakeHasher = new FakeFileHasher { OnHashing = _ => cancellation.Cancel() };

        var scan = await ScanAsync(fakeHasher, cancellation.Token);

        // První soubor se stihl, zbytek kořene je "nestihnutý" a podadresář se nikdy neotevřel.
        Assert.True(scan.WasCancelled);
        Assert.Single(scan.Files);
        Assert.Single(scan.UnscannedFiles);
        Assert.Equal(["sub"], scan.UnscannedDirectories);
        Assert.Contains("sub", scan.Directories);
    }

    [Fact]
    public async Task CancelDuringHashOfLastFile_StillReportsCancelledScan()
    {
        // Zrušení při hashování posledního souboru: žádná další iterace už kontrolu tokenu
        // neprovede, příznak musí nastavit přímo catch. Bez něj by se sken tvářil jako
        // dokončený, uložení by běželo se zrušeným tokenem a celý výsledek by se zahodil.
        using var cancellation = new CancellationTokenSource();
        var fakeHasher = new FakeFileHasher
        {
            // BFS: soubory kořene (a, b), pak podadresář sub — c.txt je poslední hashovaný.
            FailureFor = path =>
            {
                if (!path.EndsWith("c.txt", StringComparison.Ordinal))
                {
                    return null;
                }

                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            },
        };

        var scan = await ScanAsync(fakeHasher, cancellation.Token);

        Assert.True(scan.WasCancelled);
        Assert.Equal(["sub/c.txt"], scan.UnscannedFiles);
        Assert.Equal(2, scan.Files.Count);
    }

    [Fact]
    public async Task PreCancelledToken_ReportsRootAsUnscanned_ButNeverAsTrackedDirectory()
    {
        // Zrušení ještě před prvním adresářem: kořen (prázdný prefix) patří do "nestihnutých"
        // (= převzít z minula vše), ale do sledovaných adresářů snapshotu nepatří nikdy.
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var scan = await ScanAsync(new FakeFileHasher(), cancellation.Token);

        Assert.True(scan.WasCancelled);
        Assert.Empty(scan.Files);
        Assert.Equal([string.Empty], scan.UnscannedDirectories);
        Assert.DoesNotContain(string.Empty, scan.Directories);
    }

    [Fact]
    public async Task MissingRoot_ThrowsInsteadOfReportingEmptyTree()
    {
        // Zmizelý kořen musí vybublat jako chyba analýzy — kdyby ho spolkl catch pro nečitelné
        // podadresáře, analýza by tiše doběhla a tvářila se, že je vše v pořádku.
        // Očekávaná výjimka se chytá vlastním try/catch místo Assert.ThrowsAsync: xUnit je pro
        // debugger cizí kód, takže s "Just My Code" by Visual Studio na výjimce zastavovalo
        // jako na user-unhandled, přestože test prochází.
        var missingRoot = Path.Combine(_root, "neexistuje");

        DirectoryNotFoundException? thrown = null;
        try
        {
            await CreateScanner(new FakeFileHasher())
                .ScanAsync(_pathPolicy.NormalizeRoot(missingRoot), new AnalysisProgress(), checkpointAsync: null, CancellationToken.None);
        }
        catch (DirectoryNotFoundException exception)
        {
            thrown = exception;
        }

        Assert.NotNull(thrown);
    }

    [Fact]
    public async Task StorageDirectory_InsideScannedTree_IsSkipped()
    {
        // Analýza stromu obsahujícího vlastní úložiště snapshotů (např. celý disk) nesmí
        // sledovat vlastní stavové soubory — každý běh by jinak hlásil jejich změny.
        var storageDirectory = Path.Combine(_root, "snapshot-store");
        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(Path.Combine(storageDirectory, "stav.json"), "{}");

        var scan = await CreateScanner(new FakeFileHasher(), storageDirectory)
            .ScanAsync(_pathPolicy.NormalizeRoot(_root), new AnalysisProgress(), checkpointAsync: null, CancellationToken.None);

        Assert.DoesNotContain("snapshot-store", scan.Directories);
        Assert.DoesNotContain(scan.Files, file => file.RelativePath.StartsWith("snapshot-store/", StringComparison.Ordinal));
        Assert.Equal(3, scan.Files.Count); // zbytek stromu se sleduje normálně
    }
}
