namespace FileSystemChangeTracker.Api.Models;

/// <summary>
/// Výstup porovnání: nový snapshot k uložení a výsledek analýzy k vrácení klientovi.
/// </summary>
public sealed record ComparisonOutcome(DirectorySnapshot Snapshot, AnalysisResult Result);
