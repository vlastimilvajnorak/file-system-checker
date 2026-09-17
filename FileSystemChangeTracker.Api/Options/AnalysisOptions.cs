namespace FileSystemChangeTracker.Api.Options;

/// <summary>
/// Konfigurace analýzy, načítaná přes <c>IOptions&lt;AnalysisOptions&gt;</c> ze sekce "Analysis"
/// v appsettings.json — tam jsou i výchozí hodnoty. Hodnoty zde v kódu jsou jen pojistka
/// pro případ, že klíč v konfiguraci chybí.
/// </summary>
public sealed class AnalysisOptions
{
    public const string SectionName = "Analysis";

    /// <summary>
    /// Adresář pro ukládání snapshotů. Výchozí hodnota je tady v kódu (ne v appsettings.json),
    /// protože jedině <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> dá
    /// použitelné umístění na všech platformách — "%LOCALAPPDATA%" se na Linuxu nerozbalí
    /// a vznikl by adresář s tímto doslovným názvem. Umístění záměrně NEzávisí na
    /// ContentRoot/cwd, protože ten se liší podle způsobu spuštění (VS/F5, dotnet run, přímé
    /// exe) a snapshoty by se rozpadly do více úložišť. Přepsaná hodnota smí obsahovat
    /// proměnné prostředí; relativní se vztahuje k ContentRoot aplikace.
    /// </summary>
    public string SnapshotsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileSystemChangeTracker",
        "snapshots");

    /// <summary>
    /// Maximální počet souběžně běžících analýz na pozadí (hlavním limitem bývá disk).
    /// </summary>
    public int MaxConcurrentAnalyses { get; set; } = Math.Min(Environment.ProcessorCount, 8);

    /// <summary>
    /// Jak často se během skenu ukládá průběžný checkpoint snapshotu. Ukládá se jen tehdy,
    /// když rozšiřuje neúplnou nebo chybějící baseline (první nedokončený běh) — pád procesu
    /// tak ztratí nejvýše takto starou práci. Nad kompletní baseline se checkpoint neukládá:
    /// neušetřil by práci a pohltil by nenahlášené změny. Nula nebo záporná hodnota
    /// checkpointy vypne.
    /// </summary>
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(30);
}
