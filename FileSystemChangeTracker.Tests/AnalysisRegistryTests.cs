using FileSystemChangeTracker.Api.Models;
using FileSystemChangeTracker.Api.Services;

namespace FileSystemChangeTracker.Tests;

/// <summary>
/// Testy in-memory registru: idempotence per cesta, uvolnění cesty po dokončení
/// (včetně pořadí, které chrání před vrácením id staré analýzy) a lifecycle zrušení.
/// </summary>
public class AnalysisRegistryTests
{
    private static readonly string _rootA = Path.Combine(Path.GetTempPath(), "registry-a");
    private static readonly string _rootB = Path.Combine(Path.GetTempPath(), "registry-b");

    private readonly AnalysisRegistry _registry = new(new PathPolicy());

    private static AnalysisResult EmptyResult(string root) => new() { Path = root, InitialScan = true };

    [Fact]
    public void GetOrStart_IsIdempotentWhileRunning()
    {
        var (firstId, firstIsNew) = _registry.GetOrStart(_rootA);
        var (secondId, secondIsNew) = _registry.GetOrStart(_rootA);

        Assert.True(firstIsNew);
        Assert.False(secondIsNew);
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void GetOrStart_DifferentPathsGetDifferentAnalyses()
    {
        var (firstId, _) = _registry.GetOrStart(_rootA);
        var (secondId, secondIsNew) = _registry.GetOrStart(_rootB);

        Assert.True(secondIsNew);
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public void Complete_ReleasesPath_AndKeepsResultForPolling()
    {
        var (id, _) = _registry.GetOrStart(_rootA);

        _registry.Complete(id, EmptyResult(_rootA));

        // Invariant opravené race: jakmile je stav terminální, nový POST založí novou analýzu.
        Assert.True(_registry.TryGet(id, out var state));
        var (status, result, _) = state.Read();
        Assert.Equal(AnalysisStatus.Completed, status);
        Assert.NotNull(result);

        var (nextId, nextIsNew) = _registry.GetOrStart(_rootA);
        Assert.True(nextIsNew);
        Assert.NotEqual(id, nextId);
    }

    [Fact]
    public void RequestCancellation_CancelsTokenOfRunningAnalysis()
    {
        var (id, _) = _registry.GetOrStart(_rootA);
        var token = _registry.GetCancellationToken(id);

        Assert.True(token.CanBeCanceled);
        Assert.False(token.IsCancellationRequested);

        _registry.RequestCancellation(id);

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_StoresPartialResult_AndReleasesCancellationSource()
    {
        var (id, _) = _registry.GetOrStart(_rootA);

        _registry.Cancel(id, EmptyResult(_rootA));

        Assert.True(_registry.TryGet(id, out var state));
        var (status, result, _) = state.Read();
        Assert.Equal(AnalysisStatus.Cancelled, status);
        Assert.NotNull(result);

        // Zdroj zrušení je po dokončení odebraný — token dalších dotazů už není aktivní.
        Assert.False(_registry.GetCancellationToken(id).CanBeCanceled);
    }
}
