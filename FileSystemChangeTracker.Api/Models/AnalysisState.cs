namespace FileSystemChangeTracker.Api.Models;

/// <summary>
/// Měnitelný stav úlohy v in-memory registru. Zápis provádí worker po dokončení,
/// čtení obsluha GET requestu – proto je přechod stavu i čtení chráněno zámkem.
/// </summary>
public sealed class AnalysisState(string path, string key)
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AnalysisStatus _status = AnalysisStatus.Running;
    private AnalysisResult? _result;
    private string? _error;

    /// <summary>
    /// Dokončí se přechodem do terminálního stavu (Completed / Failed / Cancelled). SSE stream
    /// na něj čeká spolu s časovačem, aby závěrečnou událost poslal okamžitě, ne až v dalším
    /// tiku — jinak by zrušení či dokončení mělo v UI prodlevu až celý interval snímku.
    /// </summary>
    public Task Terminal => _terminal.Task;

    /// <summary>Normalizovaná absolutní cesta k analyzovanému adresáři.</summary>
    public string Path { get; } = path;

    /// <summary>Klíč adresáře (case-fold hash) pro registr běžících analýz.</summary>
    public string Key { get; } = key;

    /// <summary>Průběžné čítače skenu; plní je worker, čtou GET requesty (thread-safe).</summary>
    public AnalysisProgress Progress { get; } = new();

    public (AnalysisStatus Status, AnalysisResult? Result, string? Error) Read()
    {
        lock (_gate)
        {
            return (_status, _result, _error);
        }
    }

    public void Complete(AnalysisResult result)
    {
        lock (_gate)
        {
            _status = AnalysisStatus.Completed;
            _result = result;
        }

        _terminal.TrySetResult();
    }

    public void Fail(string message)
    {
        lock (_gate)
        {
            _status = AnalysisStatus.Failed;
            _error = message;
        }

        _terminal.TrySetResult();
    }

    /// <summary>
    /// Zrušení; <paramref name="result"/> je částečný výsledek (zpracovaná část uložená
    /// ve snapshotu), null pokud zrušení proběhlo dřív, než vznikl.
    /// </summary>
    public void Cancel(AnalysisResult? result)
    {
        lock (_gate)
        {
            _status = AnalysisStatus.Cancelled;
            _result = result;
        }

        _terminal.TrySetResult();
    }
}
