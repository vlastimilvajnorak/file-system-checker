namespace FileSystemChangeTracker.Api.Models;

/// <summary>
/// Výsledek jedné analýzy: změny oproti poslednímu snapshotu.
/// Při prvním běhu je <see cref="InitialScan"/> = true a seznamy změn jsou prázdné (baseline).
/// </summary>
public sealed record AnalysisResult
{
    public required string Path { get; init; }
    public required bool InitialScan { get; init; }

    /// <summary>
    /// Analýza byla zrušena a výsledek pokrývá jen zpracovanou část stromu. U prvního běhu
    /// (nebo neúplné baseline) se zpracovaná část uloží jako výchozí stav; nad kompletní
    /// baseline se nic neukládá a příští dokončený běh nahlásí změny celého stromu znovu.
    /// U živého (průběžného) výsledku běžící analýzy je vždy false.
    /// </summary>
    public bool Partial { get; init; }
    public IReadOnlyList<NewFile> NewFiles { get; init; } = [];
    public IReadOnlyList<DirectoryChange> NewDirectories { get; init; } = [];
    public IReadOnlyList<ModifiedFile> ModifiedFiles { get; init; } = [];
    public IReadOnlyList<DeletedFile> DeletedFiles { get; init; } = [];
    public IReadOnlyList<DirectoryChange> DeletedDirectories { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
