using System.Diagnostics;
using FileSystemChangeTracker.Api.Models;
using FileSystemChangeTracker.Api.Options;
using Microsoft.Extensions.Options;

namespace FileSystemChangeTracker.Api.Services;

/// <summary>Provede jednu analýzu: načti snapshot → projdi strom → porovnej → ulož.</summary>
public interface IAnalysisService
{
    Task<AnalysisResult> AnalyzeAsync(string normalizedRoot, AnalysisProgress progress, CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrace jedné analýzy. Snapshot se zapisuje po dokončeném běhu vždy; rozpracovaný stav
/// (průběžný checkpoint, merge po zrušení) jen tehdy, když rozšiřuje neúplnou nebo chybějící
/// baseline. Nad kompletní baseline se rozpracovaný stav nezapisuje nikdy: příští běh stejně
/// hashuje celý strom znovu (zápis by neušetřil práci) a hlavně by "spotřeboval" změny, které
/// se nikomu nenahlásily (pád či zastavení procesu, selhání finálního zápisu). Neošetřená
/// chyba nechá poslední platný snapshot beze změny.
/// </summary>
public sealed partial class AnalysisService(
    IPathPolicy pathPolicy,
    IDirectoryScanner scanner,
    ISnapshotComparer comparer,
    ISnapshotStore store,
    IOptions<AnalysisOptions> options,
    ILogger<AnalysisService> logger) : IAnalysisService
{
    public async Task<AnalysisResult> AnalyzeAsync(string normalizedRoot, AnalysisProgress progress, CancellationToken cancellationToken)
    {
        var key = pathPolicy.SnapshotKey(normalizedRoot);
        List<string> serviceWarnings = [];

        DirectorySnapshot? previous;
        try
        {
            previous = await store.LoadAsync(key, cancellationToken);
        }
        catch (SnapshotCorruptedException exception)
        {
            // Poškozený snapshot analýzu nezastaví, ale nesmí zmizet potichu: založí se nová
            // baseline (verze od 1) a uživatel se to dozví z varování ve výsledku i z logu.
            LogSnapshotCorrupted(logger, normalizedRoot, exception);
            serviceWarnings.Add("Uložený výchozí stav byl poškozený a nešel načíst — založil se znovu, historie verzí začíná od 1.");
            previous = null;
        }

        var checkpointInterval = options.Value.CheckpointInterval;
        var checkpointsEnabled = checkpointInterval > TimeSpan.Zero;
        var sinceLastSave = Stopwatch.StartNew();

        // Rozpracovaný stav má smysl ukládat jen tehdy, když nedoběhnutý průchod nemá z čeho
        // přebírat: první běh, nebo navazování na už neúplnou baseline. Zápis tam rozšiřuje
        // baseline o zpracované soubory (sledované od prvního zhashování). Nad kompletním
        // minulým stavem carry-over pokrývá vše a rozpracovaný stav se nezapisuje.
        bool NothingToCarryFrom() => (previous is null) || (previous.PartialBaseline is not null);

        AnalysisResult WithServiceWarnings(AnalysisResult result) => serviceWarnings.Count == 0
            ? result
            : result with { Warnings = [.. serviceWarnings, .. result.Warnings] };

        // Mezistavy ze skeneru (~1×/s) mají dvojí použití: (1) živě detekované změny pro
        // GET/SSE — porovnává je tentýž comparer jako finální výsledek, vždy vůči snapshotu
        // ze startu analýzy ("previous"), verze jsou tak deterministické; (2) u neúplné či
        // chybějící baseline se v intervalu CheckpointInterval mezistav uloží, aby pád procesu
        // během prvního běhu neztratil už zpracované soubory.
        async Task PublishIntermediateAsync(ScanResult intermediate, CancellationToken publishCancellation)
        {
            var intermediateOutcome = comparer.Compare(normalizedRoot, previous, intermediate);
            // Mezistav je z pohledu compareru "zrušený průchod" (stejná merge sémantika), ale
            // pro klienta nejde o zrušenou analýzu — příznak Partial patří jen finálnímu výsledku.
            progress.PublishLiveResult(WithServiceWarnings(intermediateOutcome.Result with { Partial = false }));
            if (checkpointsEnabled && NothingToCarryFrom() && (sinceLastSave.Elapsed >= checkpointInterval))
            {
                sinceLastSave.Restart();
                // Checkpoint je z definice rozpracovaný stav: když přežije jako poslední zápis,
                // analýzu přerušil pád procesu.
                var checkpointSnapshot = intermediateOutcome.Snapshot with
                {
                    PartialBaseline = PartialBaselineReason.Interrupted,
                };
                await store.SaveAsync(key, checkpointSnapshot, publishCancellation);
            }
        }

        var current = await scanner.ScanAsync(normalizedRoot, progress, PublishIntermediateAsync, cancellationToken);
        var outcome = comparer.Compare(normalizedRoot, previous, current);

        if (current.WasCancelled && !NothingToCarryFrom())
        {
            // Zrušený běh nad kompletní baseline: částečný výsledek se vrátí, baseline zůstává.
            // Příští dokončený běh nahlásí změny celého stromu (včetně těch ukázaných teď),
            // takže se ani po zastavení aplikace uprostřed skenu žádná změna neztratí.
            return WithServiceWarnings(outcome.Result);
        }

        // Finální zápis běží vždy s neaktivním tokenem: v tomto bodě je veškerá drahá práce
        // hotová a zápis malého JSONu je otázka milisekund. Zrušení (i takové, které dorazí
        // až mezi koncem skenu a zápisem) by jinak celý výsledek zahodilo.
        var finalSnapshot = outcome.Snapshot with
        {
            PartialBaseline = current.WasCancelled ? PartialBaselineReason.Cancelled : null,
        };
        await store.SaveAsync(key, finalSnapshot, CancellationToken.None);
        return WithServiceWarnings(outcome.Result);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshot pro cestu {Path} je poškozený — zakládá se nový výchozí stav.")]
    private static partial void LogSnapshotCorrupted(ILogger logger, string path, Exception exception);
}
