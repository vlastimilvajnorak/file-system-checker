using System.Text.Json;
using FileSystemChangeTracker.Api.Models;
using FileSystemChangeTracker.Api.Options;
using Microsoft.Extensions.Options;

namespace FileSystemChangeTracker.Api.Services;

/// <summary>Základní údaje o uloženém snapshotu (bez obsahu) pro dotaz na existenci baseline.</summary>
public sealed record SnapshotMetadata(DateTime LastAnalysisUtc, int FileCount, PartialBaselineReason? PartialBaseline);

/// <summary>
/// Snapshot existuje, ale nejde načíst (poškozený JSON, chybějící povinná pole). Volající
/// rozhodne, zda pokračovat s novou baseline — tiché "jako by nebyl" by ztratilo historii
/// verzí bez jakéhokoli varování.
/// </summary>
public sealed class SnapshotCorruptedException(string snapshotPath, Exception innerException)
    : IOException($"Snapshot '{snapshotPath}' je poškozený a nejde načíst.", innerException)
{
    public string SnapshotPath { get; } = snapshotPath;
}

/// <summary>Načítání a ukládání snapshotů. Žádná databáze – stav je v JSON souborech na disku.</summary>
public interface ISnapshotStore
{
    /// <summary>
    /// Plná cesta k adresáři úložiště. Skener ji přeskakuje, aby analýza (např. celého disku)
    /// nesledovala vlastní stavové soubory.
    /// </summary>
    string StorageDirectory { get; }

    /// <summary>Načte snapshot; null = žádný neexistuje. Poškozený vyhodí <see cref="SnapshotCorruptedException"/>.</summary>
    Task<DirectorySnapshot?> LoadAsync(string snapshotKey, CancellationToken cancellationToken);
    Task SaveAsync(string snapshotKey, DirectorySnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Vrátí metadata snapshotu, nebo null, pokud pro klíč žádný neexistuje (nebo je poškozený).</summary>
    Task<SnapshotMetadata?> GetMetadataAsync(string snapshotKey, CancellationToken cancellationToken);

    /// <summary>Smaže snapshot (reset výchozího stavu). Vrátí false, pokud žádný neexistoval.</summary>
    Task<bool> DeleteAsync(string snapshotKey, CancellationToken cancellationToken);
}

/// <summary>
/// JSON snapshot store. Zápis je atomický (temp soubor → přejmenování), takže pád aplikace
/// uprostřed zápisu nepoškodí poslední platný snapshot.
/// </summary>
public sealed class JsonSnapshotStore : ISnapshotStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    public JsonSnapshotStore(IOptions<AnalysisOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        // Konfigurace smí používat proměnné prostředí (%LOCALAPPDATA% apod.) — expandují se
        // zde, aby přepsaná hodnota v appsettings.json platila pro každý účet a stroj.
        var configured = Environment.ExpandEnvironmentVariables(options.Value.SnapshotsDirectory);
        var resolved = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);

        // Plná normalizace ("./snapshots", "..", zdvojené separátory): skener porovnává tuto
        // cestu s cestami z enumerace, aby vlastní úložiště přeskočil — nenormalizovaná
        // hodnota by se nikdy nerovnala a úložiště by se sledovalo.
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
    }

    public string StorageDirectory => _directory;

    public async Task<DirectorySnapshot?> LoadAsync(string snapshotKey, CancellationToken cancellationToken)
    {
        var path = GetPath(snapshotKey);
        if (!File.Exists(path))
        {
            return null;
        }

        // FileShare vč. Write/Delete: čtenář (GET baseline) nesmí zablokovat souběžné atomické
        // přejmenování při ukládání checkpointu/výsledku (File.Move overwrite níže).
        FileStreamOptions readOptions = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous,
        };

        try
        {
            await using var stream = new FileStream(path, readOptions);
            return await JsonSerializer.DeserializeAsync<DirectorySnapshot>(stream, _jsonOptions, cancellationToken)
                ?? throw new SnapshotCorruptedException(path, new JsonException("Soubor obsahuje JSON 'null'."));
        }
        catch (JsonException exception)
        {
            // Poškozený snapshot se nemaskuje jako chybějící: analýza sice založí nový výchozí
            // stav (soubor přepíše atomický zápis níže), ale zaloguje to a nahlásí varováním.
            throw new SnapshotCorruptedException(path, exception);
        }
    }

    public async Task SaveAsync(string snapshotKey, DirectorySnapshot snapshot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var finalPath = GetPath(snapshotKey);
        var tempPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            // Selhání/zrušení uprostřed zápisu nesmí nechávat ležet temp soubory.
            File.Delete(tempPath);
            throw;
        }
    }

    public async Task<SnapshotMetadata?> GetMetadataAsync(string snapshotKey, CancellationToken cancellationToken)
    {
        var path = GetPath(snapshotKey);
        if (!File.Exists(path))
        {
            return null;
        }

        DirectorySnapshot? snapshot;
        try
        {
            snapshot = await LoadAsync(snapshotKey, cancellationToken);
        }
        catch (SnapshotCorruptedException)
        {
            // Pro nápovědu v UI platí "výchozí stav není" — příští analýza ho založí znovu
            // a poškození nahlásí varováním; dotaz na baseline nemá kde selhat.
            return null;
        }

        if (snapshot is null)
        {
            return null;
        }

        // Čas poslední analýzy = čas posledního zápisu snapshot souboru (finální uložení,
        // průběžný checkpoint během skenu, nebo merge po zrušení).
        return new SnapshotMetadata(File.GetLastWriteTimeUtc(path), snapshot.Files.Count, snapshot.PartialBaseline);
    }

    public Task<bool> DeleteAsync(string snapshotKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(snapshotKey);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        try
        {
            // Soubor smazaný v mezičase jiným procesem File.Delete tiše ignoruje (no-op) —
            // mazání je idempotentní. Zmizelý celý adresář úložiště ale vyhodí výjimku;
            // cíl (žádný snapshot) je i tak splněn.
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    private string GetPath(string snapshotKey) => Path.Combine(_directory, $"{snapshotKey}.json");
}
