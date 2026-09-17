using System.Net;
using System.Net.Http.Json;
using FileSystemChangeTracker.Api.Contracts;
using FileSystemChangeTracker.Api.Models;
using FileSystemChangeTracker.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FileSystemChangeTracker.Tests;

/// <summary>
/// Integrační testy HTTP vrstvy a workeru nad in-memory TestServerem: validace cesty,
/// idempotence POST, životní cyklus úlohy (Completed / Cancelled / Failed), SSE stream
/// a endpointy baseline. Vlastní analýza je nahrazená řiditelným stubem — testuje se
/// controller, registr, fronta a worker, ne skener.
/// </summary>
public sealed class AnalysesApiTests : IDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly string _snapshots;
    private readonly ControllableAnalysisService _service = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AnalysesApiTests()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "api-tests-" + Path.GetRandomFileName());
        _root = Path.Combine(baseDirectory, "watched");
        _snapshots = Path.Combine(baseDirectory, "snapshots");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "obsah");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Analysis:SnapshotsDirectory", _snapshots);
            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IAnalysisService>(_service)));
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
    }

    /// <summary>Stub analýzy: chování se nastavuje per test (okamžitě hotovo, blokování, chyba).</summary>
    private sealed class ControllableAnalysisService : IAnalysisService
    {
        public Func<string, CancellationToken, Task<AnalysisResult>> Behavior { get; set; } =
            (root, _) => Task.FromResult(CompletedResult(root));

        public Task<AnalysisResult> AnalyzeAsync(string normalizedRoot, AnalysisProgress progress, CancellationToken cancellationToken) =>
            Behavior(normalizedRoot, cancellationToken);

        public static AnalysisResult CompletedResult(string root) => new()
        {
            Path = root,
            InitialScan = false,
            NewFiles = [new NewFile("novy.txt", 1)],
        };

        /// <summary>Analýza "běží", dokud test neuvolní bránu; ruční zrušení vrátí částečný výsledek.</summary>
        public TaskCompletionSource Gate { get; private set; } = new();

        public void BlockUntilReleased()
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = Gate;
            Behavior = async (root, cancellationToken) =>
            {
                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                    return CompletedResult(root);
                }
                catch (OperationCanceledException)
                {
                    return CompletedResult(root) with { Partial = true };
                }
            };
        }
    }

    private static string Query(string path) => $"?path={Uri.EscapeDataString(path)}";

    private async Task<AnalysisAcceptedResponse> StartAsync()
    {
        var response = await _client.PostAsync($"/api/analyses{Query(_root)}", content: null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AnalysisAcceptedResponse>();
        Assert.NotNull(body);
        return body;
    }

    private async Task<AnalysisStatusResponse> GetStatusAsync(Guid id)
    {
        var status = await _client.GetFromJsonAsync<AnalysisStatusResponse>($"/api/analyses/{id}");
        Assert.NotNull(status);
        return status;
    }

    /// <summary>Polluje stav, dokud není terminální (worker běží na pozadí, dokončení je asynchronní).</summary>
    private async Task<AnalysisStatusResponse> WaitForTerminalAsync(Guid id)
    {
        using var cancellation = new CancellationTokenSource(_timeout);
        while (true)
        {
            var status = await GetStatusAsync(id);
            if (status.Status != nameof(AnalysisStatus.Running))
            {
                return status;
            }

            await Task.Delay(20, cancellation.Token);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relativni/cesta")]
    public async Task Post_InvalidPath_Returns400(string path)
    {
        var response = await _client.PostAsync($"/api/analyses{Query(path)}", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_MissingDirectory_Returns400()
    {
        var response = await _client.PostAsync($"/api/analyses{Query(Path.Combine(_root, "neexistuje"))}", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_FileInsteadOfDirectory_Returns400()
    {
        var response = await _client.PostAsync($"/api/analyses{Query(Path.Combine(_root, "a.txt"))}", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Contains("soubor", problem?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/api/analyses/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_ValidDirectory_Returns202WithLocation_AndCompletes()
    {
        var response = await _client.PostAsync($"/api/analyses{Query(_root)}", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<AnalysisAcceptedResponse>();
        Assert.NotNull(accepted);
        Assert.Equal(nameof(AnalysisStatus.Running), accepted.Status);
        // Location ukazuje na stavový endpoint (async request-reply).
        Assert.EndsWith($"/api/analyses/{accepted.AnalysisId}", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);

        var final = await WaitForTerminalAsync(accepted.AnalysisId);
        Assert.Equal(nameof(AnalysisStatus.Completed), final.Status);
        Assert.NotNull(final.Result);
        Assert.Equal("novy.txt", Assert.Single(final.Result.NewFiles).Path);
        Assert.Equal(final.Path, final.Result.Path);
    }

    [Fact]
    public async Task Post_SamePathWhileRunning_IsIdempotent_AndBlocksBaselineReset()
    {
        _service.BlockUntilReleased();
        var first = await StartAsync();

        // Běžící analýza pro tutéž cestu (i v jiném zápisu velikosti písmen na Windows) → stejné id.
        var second = await StartAsync();
        Assert.Equal(first.AnalysisId, second.AnalysisId);
        Assert.Equal(nameof(AnalysisStatus.Running), (await GetStatusAsync(first.AnalysisId)).Status);

        // Reset výchozího stavu je během běhu odmítnut.
        var reset = await _client.DeleteAsync($"/api/analyses/baseline{Query(_root)}");
        Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);

        _service.Gate.SetResult();
        Assert.Equal(nameof(AnalysisStatus.Completed), (await WaitForTerminalAsync(first.AnalysisId)).Status);

        // Po dokončení založí další POST novou analýzu.
        var third = await StartAsync();
        Assert.NotEqual(first.AnalysisId, third.AnalysisId);
        await WaitForTerminalAsync(third.AnalysisId);
    }

    [Fact]
    public async Task Delete_RunningAnalysis_EndsAsCancelled_WithPartialResult()
    {
        _service.BlockUntilReleased();
        var started = await StartAsync();

        var cancel = await _client.DeleteAsync($"/api/analyses/{started.AnalysisId}");
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        var final = await WaitForTerminalAsync(started.AnalysisId);
        Assert.Equal(nameof(AnalysisStatus.Cancelled), final.Status);
        Assert.NotNull(final.Result);
        Assert.True(final.Result.Partial);

        // Zrušení už dokončené analýzy je no-op, ne chyba.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/analyses/{started.AnalysisId}")).StatusCode);
    }

    [Fact]
    public async Task FailingAnalysis_EndsAsFailed_WithError_AndWorkerSurvives()
    {
        _service.Behavior = (_, _) => throw new InvalidOperationException("simulovaná chyba");
        var failed = await StartAsync();

        var final = await WaitForTerminalAsync(failed.AnalysisId);
        Assert.Equal(nameof(AnalysisStatus.Failed), final.Status);
        Assert.Equal("simulovaná chyba", final.Error);
        Assert.Null(final.Result);

        // Selhání jedné analýzy nesmí shodit worker: další úloha se normálně zpracuje.
        _service.Behavior = (root, _) => Task.FromResult(ControllableAnalysisService.CompletedResult(root));
        var next = await StartAsync();
        Assert.Equal(nameof(AnalysisStatus.Completed), (await WaitForTerminalAsync(next.AnalysisId)).Status);
    }

    [Fact]
    public async Task Events_StreamsProgress_AndEndsWithResultEvent()
    {
        _service.BlockUntilReleased();
        var started = await StartAsync();

        using var cancellation = new CancellationTokenSource(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/analyses/{started.AnalysisId}/events");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
        List<string> eventNames = [];
        while (await reader.ReadLineAsync(cancellation.Token) is { } line)
        {
            if (!line.StartsWith("event: ", StringComparison.Ordinal))
            {
                continue;
            }

            var eventName = line["event: ".Length..];
            eventNames.Add(eventName);
            if (eventName == "result")
            {
                break; // terminální událost — server stream vzápětí zavře
            }

            // Po prvním snímku průběhu analýzu dokončit; další snímek už musí být terminální.
            _service.Gate.TrySetResult();
        }

        Assert.Contains("progress", eventNames);
        Assert.Equal("result", eventNames[^1]);
    }

    [Fact]
    public async Task Baseline_ReflectsStoredSnapshot_AndResetDeletesIt()
    {
        var before = await _client.GetFromJsonAsync<BaselineResponse>($"/api/analyses/baseline{Query(_root)}");
        Assert.NotNull(before);
        Assert.False(before.Exists);
        Assert.False(before.AnalysisRunning);

        // Stub analýzy nic neukládá — snapshot založíme přímo přes úložiště aplikace.
        var pathPolicy = _factory.Services.GetRequiredService<IPathPolicy>();
        var store = _factory.Services.GetRequiredService<ISnapshotStore>();
        var key = pathPolicy.SnapshotKey(pathPolicy.NormalizeRoot(_root));
        await store.SaveAsync(
            key,
            new DirectorySnapshot
            {
                Root = _root,
                Files = [new FileRecord { RelativePath = "a.txt", Hash = "h", Version = 2 }],
                PartialBaseline = PartialBaselineReason.Interrupted,
            },
            CancellationToken.None);

        var after = await _client.GetFromJsonAsync<BaselineResponse>($"/api/analyses/baseline{Query(_root)}");
        Assert.NotNull(after);
        Assert.True(after.Exists);
        Assert.Equal(1, after.TrackedFileCount);
        Assert.Equal(nameof(PartialBaselineReason.Interrupted), after.PartialReason);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/analyses/baseline{Query(_root)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/analyses/baseline{Query(_root)}")).StatusCode);
    }

    [Fact]
    public async Task Browse_ReturnsSubdirectories_NotFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var browse = await _client.GetFromJsonAsync<BrowseResponse>($"/api/browse{Query(_root)}");

        Assert.NotNull(browse);
        Assert.False(browse.IsDriveList);
        Assert.Equal("sub", Assert.Single(browse.Directories).Name);
    }
}
