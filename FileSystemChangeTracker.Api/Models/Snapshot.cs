using System.Text.Json.Serialization;

namespace FileSystemChangeTracker.Api.Models;

/// <summary>
/// Záznam o jednom souboru v uloženém snapshotu. Cesta je relativní vůči kořeni a používá "/".
/// Detekce změny stojí čistě na hashi obsahu — nic dalšího se záměrně neukládá.
/// </summary>
public sealed record FileRecord
{
    public required string RelativePath { get; init; }
    public required string Hash { get; init; }
    public required int Version { get; init; }
}

/// <summary>Proč výchozí stav nepokrývá celý strom. V JSON se ukládá jako text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PartialBaselineReason>))]
public enum PartialBaselineReason
{
    /// <summary>
    /// Běh ukončilo zrušení tokenu — typicky uživatel (DELETE /api/analyses/{id});
    /// stejně se uloží i běh, který stihl merge zápis při zastavení aplikace.
    /// </summary>
    Cancelled,

    /// <summary>Poslední zápis byl průběžný checkpoint — analýzu přerušil pád či ukončení procesu.</summary>
    Interrupted,
}

/// <summary>
/// Perzistovaný stav adresářového stromu z posledního zápisu — úspěšné dokončení analýzy,
/// průběžný checkpoint během skenu, nebo konzistentní merge po zrušení.
/// Adresáře se evidují kvůli detekci vytvoření/odstranění, verzi ale nemají.
/// </summary>
public sealed record DirectorySnapshot
{
    public required string Root { get; init; }
    public IReadOnlyList<FileRecord> Files { get; init; } = [];
    public IReadOnlyList<string> Directories { get; init; } = [];

    /// <summary>
    /// Ne-null = výchozí stav nepokrývá celý strom (první běh nedoběhl a nebylo z čeho přebírat).
    /// Nezahrnuté soubory nahlásí příští dokončený běh jako nové (verze 1) — UI na to upozorní
    /// a nabídne reset. Jakýkoli dokončený běh příznak smaže; ve starších snapshotech chybí (=null).
    /// </summary>
    public PartialBaselineReason? PartialBaseline { get; init; }
}
