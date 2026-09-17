using FileSystemChangeTracker.Api.Models;

namespace FileSystemChangeTracker.Api.Contracts;

/// <summary>Odpověď na založení analýzy (HTTP 202).</summary>
public sealed record AnalysisAcceptedResponse(Guid AnalysisId, string Status);

/// <summary>
/// Informace, zda pro danou cestu existuje výchozí stav (baseline) z dřívější analýzy.
/// UI podle ní rozlišuje „první analýza založí baseline" vs. „nahlásí změny od minula".
/// <see cref="PartialReason"/> ("Cancelled" / "Interrupted") značí neúplnou baseline —
/// první běh nedoběhl a UI nabízí volbu pokračovat / resetovat.
/// <see cref="AnalysisRunning"/> odliší právě probíhající analýzu: její checkpointy nesou
/// "Interrupted" (kdyby proces spadl), což by se jinak tvářilo jako nedoběhnutý běh.
/// </summary>
public sealed record BaselineResponse(bool Exists, DateTime? LastAnalysisUtc, int? TrackedFileCount, string? PartialReason, bool AnalysisRunning);

/// <summary>
/// Odpověď na dotaz na stav analýzy (HTTP 200). <see cref="Path"/> a <see cref="Progress"/>
/// jsou k dispozici i během běhu, aby klient viděl, co a jak rychle se zpracovává.
/// <see cref="Result"/> během běhu obsahuje průběžně detekované změny (aktualizace ~1×/s,
/// vždy vůči snapshotu ze startu analýzy, <c>partial</c> = false); po dokončení finální výsledek.
/// </summary>
public sealed record AnalysisStatusResponse(
    Guid AnalysisId,
    string Status,
    string Path,
    AnalysisProgressSnapshot Progress,
    AnalysisResult? Result,
    string? Error);
