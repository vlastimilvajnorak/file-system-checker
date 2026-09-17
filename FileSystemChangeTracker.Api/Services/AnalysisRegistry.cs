using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using FileSystemChangeTracker.Api.Models;

namespace FileSystemChangeTracker.Api.Services;

/// <summary>
/// In-memory registr analýz. Zajišťuje idempotenci: pro stejnou cestu nemůže běžet více analýz
/// najednou – druhý požadavek dostane stejné id.
/// </summary>
public interface IAnalysisRegistry
{
    /// <summary>
    /// Vrátí id běžící analýzy pro danou cestu, nebo založí novou. <c>IsNew</c> je true,
    /// pokud volající má analýzu zařadit ke zpracování.
    /// </summary>
    (Guid Id, bool IsNew) GetOrStart(string normalizedRoot);

    bool TryGet(Guid id, [MaybeNullWhen(false)] out AnalysisState state);

    /// <summary>Běží pro danou cestu právě analýza? (Guard pro reset výchozího stavu.)</summary>
    bool IsRunning(string normalizedRoot);

    /// <summary>Token pro ruční zrušení dané analýzy; None, pokud už neběží.</summary>
    CancellationToken GetCancellationToken(Guid id);

    /// <summary>Požádá o zrušení běžící analýzy. U dokončené je to no-op.</summary>
    void RequestCancellation(Guid id);

    void Complete(Guid id, AnalysisResult result);
    void Fail(Guid id, string message);
    void Cancel(Guid id, AnalysisResult? result);
}

/// <inheritdoc />
public sealed class AnalysisRegistry(IPathPolicy pathPolicy) : IAnalysisRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Guid> _runningByKey = [];
    private readonly ConcurrentDictionary<Guid, AnalysisState> _byId = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellationById = new();

    public (Guid Id, bool IsNew) GetOrStart(string normalizedRoot)
    {
        var key = pathPolicy.SnapshotKey(normalizedRoot);

        // Krátká kritická sekce jen pro atomické rozhodnutí "vrátit existující, nebo založit novou".
        // Samotná analýza běží mimo zámek.
        lock (_gate)
        {
            if (_runningByKey.TryGetValue(key, out var existing))
            {
                return (existing, false);
            }

            var id = Guid.NewGuid();
            _byId[id] = new AnalysisState(normalizedRoot, key);
            _cancellationById[id] = new CancellationTokenSource();
            _runningByKey[key] = id;
            return (id, true);
        }
    }

    public bool TryGet(Guid id, [MaybeNullWhen(false)] out AnalysisState state) => _byId.TryGetValue(id, out state);

    public bool IsRunning(string normalizedRoot)
    {
        var key = pathPolicy.SnapshotKey(normalizedRoot);
        lock (_gate)
        {
            return _runningByKey.ContainsKey(key);
        }
    }

    public CancellationToken GetCancellationToken(Guid id) =>
        _cancellationById.TryGetValue(id, out var cancellation)
            ? cancellation.Token
            : CancellationToken.None;

    public void RequestCancellation(Guid id)
    {
        if (_cancellationById.TryGetValue(id, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    public void Complete(Guid id, AnalysisResult result) => Finish(id, state => state.Complete(result));

    public void Fail(Guid id, string message) => Finish(id, state => state.Fail(message));

    public void Cancel(Guid id, AnalysisResult? result) => Finish(id, state => state.Cancel(result));

    private void Finish(Guid id, Action<AnalysisState> apply)
    {
        if (!_byId.TryGetValue(id, out var state))
        {
            return;
        }

        // Zdroj zrušení se jen odebere, záměrně bez Dispose: souběžný RequestCancellation
        // si ho mohl už vytáhnout a Cancel na disposed zdroji by spadl. Neodstraněný zdroj
        // bez timeru je pro GC obyčejný objekt.
        _cancellationById.TryRemove(id, out _);

        // Nejdřív uvolnit cestu, teprve pak nastavit terminální stav. Jakmile GET vrátí
        // Completed/Failed, musí už další POST založit novou analýzu — obrácené pořadí by
        // v okně mezi oběma kroky vracelo id staré, právě dokončené analýzy. Opačné okno
        // (cesta uvolněná, stav ještě Running) je neškodné, práce staré analýzy je hotová.
        // Stav v _byId zůstává i po dokončení, aby GET vrátil výsledek.
        lock (_gate)
        {
            if (_runningByKey.TryGetValue(state.Key, out var current) && (current == id))
            {
                _runningByKey.Remove(state.Key);
            }
        }

        apply(state);
    }
}
