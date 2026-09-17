using System.Text;
using FileSystemChangeTracker.Api.Models;
using FileSystemChangeTracker.Api.Options;
using FileSystemChangeTracker.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FileSystemChangeTracker.Tests;

/// <summary>
/// Testy JSON úložiště snapshotů: roundtrip a čtení staršího formátu (pole navíc,
/// např. dřívější Size/LastWriteTimeUtc, se při deserializaci ignorují).
/// </summary>
public class SnapshotStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "store-tests-" + Path.GetRandomFileName());

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private JsonSnapshotStore CreateStore() => new(
        Microsoft.Extensions.Options.Options.Create(new AnalysisOptions { SnapshotsDirectory = _directory }),
        new StubEnvironment());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveAndLoad_Roundtrip()
    {
        var store = CreateStore();
        DirectorySnapshot snapshot = new()
        {
            Root = "/data",
            Files = [new FileRecord { RelativePath = "a.txt", Hash = "h1", Version = 3 }],
            Directories = ["sub"],
            PartialBaseline = PartialBaselineReason.Interrupted,
        };

        await store.SaveAsync("klic", snapshot, CancellationToken.None);
        var loaded = await store.LoadAsync("klic", CancellationToken.None);

        Assert.NotNull(loaded);
        var record = Assert.Single(loaded.Files);
        Assert.Equal("a.txt", record.RelativePath);
        Assert.Equal("h1", record.Hash);
        Assert.Equal(3, record.Version);
        Assert.Equal(["sub"], loaded.Directories);
        Assert.Equal(PartialBaselineReason.Interrupted, loaded.PartialBaseline);
    }

    [Fact]
    public async Task Load_IgnoresUnknownFieldsFromOlderFormat()
    {
        // Starší snapshoty obsahovaly i Size a LastWriteTimeUtc — musí zůstat čitelné.
        Directory.CreateDirectory(_directory);
        var legacyJson = """
        {
          "Root": "/data",
          "Files": [
            {
              "RelativePath": "stary.txt",
              "Size": 42,
              "LastWriteTimeUtc": "2026-01-01T00:00:00Z",
              "Hash": "h-stary",
              "Version": 7
            }
          ],
          "Directories": []
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_directory, "legacy.json"), legacyJson, Encoding.UTF8);

        var loaded = await CreateStore().LoadAsync("legacy", CancellationToken.None);

        Assert.NotNull(loaded);
        var record = Assert.Single(loaded.Files);
        Assert.Equal("stary.txt", record.RelativePath);
        Assert.Equal(7, record.Version);
        // Starší formát pole PartialBaseline nemá — chybějící hodnota = kompletní baseline.
        Assert.Null(loaded.PartialBaseline);
    }

    [Fact]
    public async Task Load_CorruptJson_ThrowsSnapshotCorrupted()
    {
        // Poškozený snapshot se nesmí tvářit jako chybějící (tichá ztráta historie verzí) —
        // volající dostane zřetelnou výjimku a rozhodne, jak pokračovat. Vlastní try/catch
        // místo Assert.ThrowsAsync ze stejného důvodu jako v DirectoryScannerTests.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "vadny.json"), "{ tohle není JSON", Encoding.UTF8);

        SnapshotCorruptedException? thrown = null;
        try
        {
            await CreateStore().LoadAsync("vadny", CancellationToken.None);
        }
        catch (SnapshotCorruptedException exception)
        {
            thrown = exception;
        }

        Assert.NotNull(thrown);
        Assert.EndsWith("vadny.json", thrown.SnapshotPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMetadata_CorruptJson_ReportsNoBaseline()
    {
        // Dotaz na baseline (nápověda v UI) nemá kde selhat: poškozený stav = "není založen".
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "vadny.json"), "{ tohle není JSON", Encoding.UTF8);

        var metadata = await CreateStore().GetMetadataAsync("vadny", CancellationToken.None);

        Assert.Null(metadata);
    }

    [Fact]
    public void StorageDirectory_IsFullyNormalized()
    {
        // Skener porovnává StorageDirectory s cestami z enumerace, aby vlastní úložiště
        // přeskočil — "./", ".." ani koncový separátor tam nesmí zůstat.
        var expected = Path.Combine(_directory, "snap");

        var relative = new JsonSnapshotStore(
            Microsoft.Extensions.Options.Options.Create(new AnalysisOptions { SnapshotsDirectory = "./jiny/../snap/" }),
            new StubEnvironment { ContentRootPath = _directory });
        var absolute = new JsonSnapshotStore(
            Microsoft.Extensions.Options.Options.Create(new AnalysisOptions { SnapshotsDirectory = Path.Combine(_directory, ".", "jiny", "..", "snap") }),
            new StubEnvironment());

        Assert.Equal(expected, relative.StorageDirectory);
        Assert.Equal(expected, absolute.StorageDirectory);
    }

    [Fact]
    public async Task GetMetadata_PropagatesFileCountAndPartialBaseline()
    {
        // Metadata jsou jediný kanál, kterým se příznak neúplné baseline dostane do API a UI.
        var store = CreateStore();
        DirectorySnapshot snapshot = new()
        {
            Root = "/data",
            Files = [new FileRecord { RelativePath = "a.txt", Hash = "h1", Version = 1 }],
            PartialBaseline = PartialBaselineReason.Cancelled,
        };
        await store.SaveAsync("klic", snapshot, CancellationToken.None);

        var metadata = await store.GetMetadataAsync("klic", CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal(1, metadata.FileCount);
        Assert.Equal(PartialBaselineReason.Cancelled, metadata.PartialBaseline);
    }

    [Fact]
    public async Task Delete_RemovesSnapshot_SecondDeleteReportsMissing()
    {
        // Reset výchozího stavu: smazání vrátí true a snapshot zmizí; opakované smazání false.
        var store = CreateStore();
        await store.SaveAsync("klic", new DirectorySnapshot { Root = "/data" }, CancellationToken.None);

        Assert.True(await store.DeleteAsync("klic", CancellationToken.None));
        Assert.Null(await store.LoadAsync("klic", CancellationToken.None));
        Assert.False(await store.DeleteAsync("klic", CancellationToken.None));
    }
}
