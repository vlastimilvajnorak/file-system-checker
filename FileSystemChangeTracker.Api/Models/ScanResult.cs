namespace FileSystemChangeTracker.Api.Models;

/// <summary>
/// Soubor zjištěný při aktuálním průchodu stromem (ještě bez verze – tu přiřadí porovnání).
/// </summary>
public sealed record ScannedFile
{
    public required string RelativePath { get; init; }
    public required string Hash { get; init; }
}

/// <summary>
/// Výsledek průchodu adresářovým stromem: soubory s hashem, relativní cesty adresářů a varování.
/// </summary>
public sealed record ScanResult
{
    public IReadOnlyList<ScannedFile> Files { get; init; } = [];

    /// <summary>
    /// Soubory, které existují, ale nešly přečíst (zámek, oprávnění). Porovnání pro ně
    /// zachová poslední známý záznam, aby se nehlásily falešně jako smazané.
    /// </summary>
    public IReadOnlyList<string> UnreadableFiles { get; init; } = [];

    /// <summary>
    /// Adresáře, jejichž obsah nešel vyjmenovat (odebraná práva, zamčená položka při enumeraci).
    /// Porovnání zachová poslední známý stav celého jejich podstromu. Prázdný řetězec = kořen.
    /// </summary>
    public IReadOnlyList<string> UnreadableDirectories { get; init; } = [];

    /// <summary>Průchod byl zrušen — <see cref="UnscannedFiles"/> a <see cref="UnscannedDirectories"/> popisují, kam už nedošel.</summary>
    public bool WasCancelled { get; init; }

    /// <summary>Soubory, na které při zrušení nedošlo. Porovnání zachová jejich poslední známý záznam.</summary>
    public IReadOnlyList<string> UnscannedFiles { get; init; } = [];

    /// <summary>Adresáře, jejichž podstrom se při zrušení nestihl projít. Porovnání zachová jejich poslední známý stav.</summary>
    public IReadOnlyList<string> UnscannedDirectories { get; init; } = [];

    public IReadOnlyList<string> Directories { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
