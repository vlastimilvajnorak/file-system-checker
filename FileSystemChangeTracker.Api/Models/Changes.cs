namespace FileSystemChangeTracker.Api.Models;

/// <summary>Nově vzniklý soubor (verze 1).</summary>
public sealed record NewFile(string Path, int Version);

/// <summary>Soubor se změněným obsahem (verze navýšena o 1).</summary>
public sealed record ModifiedFile(string Path, int Version);

/// <summary>Odstraněný soubor, hlášený s poslední známou verzí.</summary>
public sealed record DeletedFile(string Path, int LastKnownVersion);

/// <summary>Nově vzniklý nebo odstraněný adresář (bez verze).</summary>
public sealed record DirectoryChange(string Path);
