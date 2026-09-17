using FileSystemChangeTracker.Api.Options;
using Microsoft.Extensions.Options;

namespace FileSystemChangeTracker.Api.Services;

/// <summary>
/// Background service, který bere analýzy z fronty a zpracovává je s omezenou souběžností.
/// HTTP request se tak nečeká na dokončení skenu.
/// </summary>
public sealed partial class AnalysisWorker(
    IAnalysisBacklog backlog,
    IAnalysisRegistry registry,
    IAnalysisService analysisService,
    IOptions<AnalysisOptions> options,
    ILogger<AnalysisWorker> logger) : BackgroundService
{
    private readonly int _maxConcurrency = Math.Max(1, options.Value.MaxConcurrentAnalyses);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = _maxConcurrency,
            CancellationToken = stoppingToken,
        };

        try
        {
            await Parallel.ForEachAsync(backlog.DequeueAllAsync(stoppingToken), parallelOptions, ProcessAsync);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Očekávané při zastavení aplikace.
        }
    }

    private async ValueTask ProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(id, out var state))
        {
            return;
        }

        // Analýzu může ukončit zastavení aplikace (cancellationToken) nebo ruční zrušení
        // klientem (DELETE /api/analyses/{id} → token z registru) — proto linked token.
        var manualCancellation = registry.GetCancellationToken(id);
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, manualCancellation);

        try
        {
            // Zrušený sken nevyhazuje výjimku: vrátí částečný výsledek (Partial), zpracovaná
            // část je uložená ve snapshotu a zbytek se doporovná při příštím běhu.
            var result = await analysisService.AnalyzeAsync(state.Path, state.Progress, linkedCancellation.Token);
            if (result.Partial)
            {
                registry.Cancel(id, result);
            }
            else
            {
                registry.Complete(id, result);
            }
        }
        // Zastavení aplikace během načítání/ukládání snapshotu: úlohu označit a nechat
        // výjimku probublat, ForEachAsync končí.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            registry.Cancel(id, result: null);
            throw;
        }
        // Ruční zrušení během načítání snapshotu (před vznikem výsledku): worker běží dál.
        catch (OperationCanceledException) when (manualCancellation.IsCancellationRequested)
        {
            registry.Cancel(id, result: null);
        }
        // Filtr na tokeny výše je záměrný: cizí OperationCanceledException (nevyvolaná ani
        // jedním tokenem) se zpracuje jako selhání jedné analýzy, ne pád celého workeru
        // (a s ním přes výchozí BackgroundServiceExceptionBehavior.StopHost celé aplikace).
        catch (Exception exception)
        {
            LogAnalysisFailed(logger, id, state.Path, exception);
            registry.Fail(id, exception.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Analýza {AnalysisId} pro cestu {Path} selhala.")]
    private static partial void LogAnalysisFailed(ILogger logger, Guid analysisId, string path, Exception exception);
}
