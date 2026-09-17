using System.Threading.Channels;

namespace FileSystemChangeTracker.Api.Services;

/// <summary>Seznam analýz čekajících na zpracování workerem.</summary>
public interface IAnalysisBacklog
{
    void Enqueue(Guid analysisId);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}

/// <summary>In-memory fronta nad <see cref="Channel{T}"/>.</summary>
public sealed class AnalysisBacklog : IAnalysisBacklog
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    // Zápis je synchronní a bez tokenu záměrně: u neomezeného kanálu TryWrite vždy uspěje
    // (dokud se writer neuzavře, což se neděje). Vázat zápis na token HTTP requestu by při
    // zrušení requestu mezi založením analýzy a zařazením do fronty nechalo úlohu navždy
    // ve stavu Running a zablokovalo idempotenci pro danou cestu.
    public void Enqueue(Guid analysisId) => _channel.Writer.TryWrite(analysisId);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
