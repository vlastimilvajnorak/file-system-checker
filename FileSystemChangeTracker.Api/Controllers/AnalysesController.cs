using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using FileSystemChangeTracker.Api.Contracts;
using FileSystemChangeTracker.Api.Models;
using FileSystemChangeTracker.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileSystemChangeTracker.Api.Controllers;

/// <summary>
/// REST API pro spuštění analýzy a dotaz na její výsledek. Cesta se předává jako URL parametr.
/// Spuštění je idempotentní: opakovaný POST pro stejnou (běžící) cestu vrátí stejné id.
/// </summary>
[ApiController]
[Route("api/analyses")]
public sealed class AnalysesController(
    IPathPolicy pathPolicy,
    IAnalysisRegistry registry,
    IAnalysisBacklog backlog,
    ISnapshotStore snapshotStore) : ControllerBase
{
    /// <summary>
    /// Vrátí, zda pro danou cestu existuje výchozí stav (baseline) z dřívější analýzy.
    /// Adresář se nekontroluje — dotaz se týká jen uloženého snapshotu.
    /// </summary>
    [HttpGet("baseline")]
    [ProducesResponseType<BaselineResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBaseline([FromQuery] string? path, CancellationToken cancellationToken)
    {
        if (TryValidatePath(path, out var normalizedRoot) is { } validationProblem)
        {
            return validationProblem;
        }

        var key = pathPolicy.SnapshotKey(normalizedRoot);
        var metadata = await snapshotStore.GetMetadataAsync(key, cancellationToken);
        return Ok(new BaselineResponse(
            metadata is not null,
            metadata?.LastAnalysisUtc,
            metadata?.FileCount,
            metadata?.PartialBaseline?.ToString(),
            registry.IsRunning(normalizedRoot)));
    }

    /// <summary>
    /// Reset výchozího stavu: smaže uložený snapshot, takže příští analýza založí baseline
    /// celého stromu znovu (a nic nenahlásí). Určeno hlavně pro neúplnou baseline po
    /// nedokončeném prvním běhu. Během běžící analýzy vrací 409 — její uložení by smazaný
    /// stav vzápětí nahradilo. Guard je best-effort: okno mezi kontrolou a smazáním nelze
    /// bez globálního zámku uzavřít, souběžně odstartovaná analýza může stav obratem uložit
    /// znovu (pro ruční reset přes UI je to přijatelné riziko).
    /// </summary>
    [HttpDelete("baseline")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResetBaseline([FromQuery] string? path, CancellationToken cancellationToken)
    {
        if (TryValidatePath(path, out var normalizedRoot) is { } validationProblem)
        {
            return validationProblem;
        }

        if (registry.IsRunning(normalizedRoot))
        {
            return Problem(
                $"Pro cestu '{normalizedRoot}' právě běží analýza — počkejte na dokončení, nebo ji zrušte (DELETE /api/analyses/{{id}}).",
                statusCode: StatusCodes.Status409Conflict);
        }

        var key = pathPolicy.SnapshotKey(normalizedRoot);
        bool deleted;
        try
        {
            deleted = await snapshotStore.DeleteAsync(key, cancellationToken);
        }
        catch (IOException)
        {
            // Soubor drží jiný proces (zálohování, antivirus) — přechodný stav, ne chyba serveru.
            return Problem($"Snapshot pro cestu '{normalizedRoot}' se právě používá — zkuste reset za okamžik znovu.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only atribut nebo ACL na souboru snapshotu — bez zásahu se to samo nespraví.
            return Problem($"Snapshot pro cestu '{normalizedRoot}' nelze smazat — chybí oprávnění k souboru v úložišti snapshotů.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (!deleted)
        {
            return Problem($"Pro cestu '{normalizedRoot}' není založen žádný výchozí stav.", statusCode: StatusCodes.Status404NotFound);
        }

        return NoContent();
    }

    /// <summary>Založí analýzu adresáře a vrátí 202 s jejím identifikátorem.</summary>
    [HttpPost]
    [ProducesResponseType<AnalysisAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public IActionResult Start([FromQuery] string? path)
    {
        if (TryValidatePath(path, out var normalizedRoot) is { } validationProblem)
        {
            return validationProblem;
        }

        if (System.IO.File.Exists(normalizedRoot))
        {
            return Problem($"Cesta '{normalizedRoot}' je soubor, ne adresář.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            // Dotkne se kořene; záměrně bez IgnoreInaccessible, aby se odlišilo "neexistuje" od
            // "nemám práva" (typické při běhu pod servisním účtem). Iteruje se jen první položka.
            _ = Directory.EnumerateFileSystemEntries(normalizedRoot).Any();
        }
        catch (DirectoryNotFoundException)
        {
            return Problem($"Adresář '{normalizedRoot}' neexistuje.", statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UnauthorizedAccessException)
        {
            return Problem($"K adresáři '{normalizedRoot}' nemá aplikace přístup (zkontrolujte oprávnění účtu).", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (IOException)
        {
            return Problem($"Cesta '{normalizedRoot}' není platný adresář.", statusCode: StatusCodes.Status400BadRequest);
        }

        (var id, var isNew) = registry.GetOrStart(normalizedRoot);
        if (isNew)
        {
            backlog.Enqueue(id);
        }

        AnalysisAcceptedResponse response = new(id, AnalysisStatus.Running.ToString());
        return AcceptedAtAction(nameof(Get), new { id }, response);
    }

    /// <summary>
    /// Společná validace vstupní cesty. Vrátí odpověď s chybou 400, nebo null a normalizovanou
    /// cestu. Vyžaduje se plně kvalifikovaná cesta (relativní by se tiše přeložila vůči cwd
    /// procesu); samotné označení disku se bere jako jeho kořen.
    /// </summary>
    private ObjectResult? TryValidatePath(string? path, out string normalizedRoot)
    {
        normalizedRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Problem("Parametr 'path' je povinný.", statusCode: StatusCodes.Status400BadRequest);
        }

        var trimmedPath = path.Trim();

        // Samotné označení disku (S:) by GetFullPath přeložil na "aktuální adresář na disku S".
        // V kontextu tohoto API je ale zjevným záměrem kořen disku — normalizuje se na "S:\".
        if ((trimmedPath.Length == 2) && char.IsAsciiLetter(trimmedPath[0]) && (trimmedPath[1] == ':'))
        {
            trimmedPath += Path.DirectorySeparatorChar;
        }

        if (!Path.IsPathFullyQualified(trimmedPath))
        {
            return Problem(
                $"Cesta '{trimmedPath}' není absolutní. Zadejte plnou cestu k adresáři (např. C:\\Data\\Watched), nebo kořen disku (C:\\).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            normalizedRoot = pathPolicy.NormalizeRoot(trimmedPath);
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Problem("Zadaná cesta není platná.", statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Vrátí stav analýzy a po dokončení i výsledek.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<AnalysisStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Get(Guid id)
    {
        if (!registry.TryGet(id, out var state))
        {
            return Problem($"Analýza s id '{id}' neexistuje. Stav úloh je jen v paměti procesu a nepřežije restart aplikace — spusťte analýzu znovu přes POST /api/analyses.", statusCode: StatusCodes.Status404NotFound);
        }

        (var status, var result, var error) = state.Read();

        // Během běhu se místo finálního výsledku vrací průběžně detekované změny (LiveResult).
        AnalysisStatusResponse response = new(id, status.ToString(), state.Path, state.Progress.Snapshot(), result ?? state.Progress.LiveResult, error);
        return Ok(response);
    }

    /// <summary>
    /// Požádá o zrušení běžící analýzy. Zrušení je asynchronní — stav se krátce poté překlopí
    /// na Cancelled (sledovatelné přes GET/SSE) s částečným výsledkem (Partial = true).
    /// U prvního běhu se zpracovaná část uloží jako (neúplný) výchozí stav; nad kompletní
    /// baseline se nic neukládá a příští dokončený běh nahlásí změny celého stromu.
    /// U již dokončené analýzy je volání no-op.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Cancel(Guid id)
    {
        if (!registry.TryGet(id, out _))
        {
            return Problem(
                $"Analýza s id '{id}' neexistuje. Stav úloh je jen v paměti procesu a nepřežije restart aplikace — spusťte analýzu znovu přes POST /api/analyses.",
                statusCode: StatusCodes.Status404NotFound);
        }

        registry.RequestCancellation(id);
        return NoContent();
    }

    /// <summary>
    /// Streamuje průběh analýzy jako Server-Sent Events: každých 500 ms jeden snapshot stavu,
    /// terminální stav (Completed/Failed/Cancelled) je poslední událost a stream se zavře.
    /// Payload událostí má stejný tvar jako odpověď GET, takže klient nepotřebuje dva modely.
    /// POZOR: Swagger UI streamy nezobrazuje průběžně (čeká na uzavření odpovědi) — pro živé
    /// sledování použijte mini UI na "/", `curl -N`, nebo `EventSource` v prohlížeči.
    /// </summary>
    [HttpGet("{id:guid}/events")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IResult StreamEvents(Guid id, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(id, out var state))
        {
            return Results.Problem(
                $"Analýza s id '{id}' neexistuje. Stav úloh je jen v paměti procesu a nepřežije restart aplikace — spusťte analýzu znovu přes POST /api/analyses.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.ServerSentEvents(StreamStatusAsync(id, state, cancellationToken));
    }

    private static async IAsyncEnumerable<SseItem<AnalysisStatusResponse>> StreamStatusAsync(
        Guid id,
        AnalysisState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            (var status, var result, var error) = state.Read();
            AnalysisStatusResponse payload = new(id, status.ToString(), state.Path, state.Progress.Snapshot(), result ?? state.Progress.LiveResult, error);
            var isTerminal = status != AnalysisStatus.Running;
            yield return new SseItem<AnalysisStatusResponse>(payload, isTerminal ? "result" : "progress");
            if (isTerminal)
            {
                yield break;
            }

            // Odpojení klienta (zavřený tab, zrušený request) je běžný konec SSE streamu, ne chyba.
            // Zrušení se proto nepropaguje výjimkou z uživatelského kódu — pod debuggerem by
            // s "Just My Code" zastavovala aplikaci jako user-unhandled exception.
            if (await DelayUntilNextSnapshotAsync(state, cancellationToken))
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Počká do dalšího snímku průběhu, nebo do terminálního stavu úlohy — co nastane dřív.
    /// Dokončení a zrušení se tak klientovi doručí okamžitě, ne až v příštím tiku.
    /// True = klient se mezitím odpojil.
    /// </summary>
    private static async Task<bool> DelayUntilNextSnapshotAsync(AnalysisState state, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken), state.Terminal);
            return cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}
