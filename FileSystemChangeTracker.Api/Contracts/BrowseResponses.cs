namespace FileSystemChangeTracker.Api.Contracts;

/// <summary>Jedna položka v procházeči: název (pro zobrazení) a plná cesta (pro navigaci/výběr).</summary>
public sealed record DirectoryEntry(string Name, string Path);

/// <summary>
/// Odpověď procházeče adresářů. <see cref="CurrentPath"/> je null u seznamu disků (kořen);
/// <see cref="ParentPath"/> je null v kořeni disku (o úroveň výš je pak seznam disků).
/// </summary>
public sealed record BrowseResponse(
    string? CurrentPath,
    string? ParentPath,
    bool IsDriveList,
    IReadOnlyList<DirectoryEntry> Directories);
