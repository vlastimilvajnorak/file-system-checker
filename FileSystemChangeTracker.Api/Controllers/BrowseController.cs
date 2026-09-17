using FileSystemChangeTracker.Api.Contracts;
using FileSystemChangeTracker.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileSystemChangeTracker.Api.Controllers;

/// <summary>
/// Procházení adresářů na serveru — podklad pro výběr cesty v UI (webová stránka nemá přístup
/// ke skutečným absolutním cestám). Vrací disky a podadresáře, nikdy soubory.
/// </summary>
[ApiController]
[Route("api/browse")]
public sealed class BrowseController(IPathPolicy pathPolicy) : ControllerBase
{
    private static readonly EnumerationOptions _enumerationOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// Vrátí obsah adresáře pro navigaci. Prázdná cesta = kořen: na Windows seznam disků,
    /// jinde kořenový adresář „/".
    /// </summary>
    [HttpGet]
    [ProducesResponseType<BrowseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public IActionResult Browse([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (OperatingSystem.IsWindows())
            {
                var drives = DriveInfo.GetDrives()
                    .Where(drive => drive.IsReady)
                    .Select(drive => new DirectoryEntry(drive.Name, drive.RootDirectory.FullName))
                    .ToList();
                return Ok(new BrowseResponse(CurrentPath: null, ParentPath: null, IsDriveList: true, drives));
            }

            path = "/";
        }

        var trimmedPath = path.Trim();

        // Stejná sémantika jako u POST /api/analyses: samotné označení disku = jeho kořen.
        if ((trimmedPath.Length == 2) && char.IsAsciiLetter(trimmedPath[0]) && (trimmedPath[1] == ':'))
        {
            trimmedPath += Path.DirectorySeparatorChar;
        }

        if (!Path.IsPathFullyQualified(trimmedPath))
        {
            return Problem("Cesta není absolutní.", statusCode: StatusCodes.Status400BadRequest);
        }

        string normalized;
        try
        {
            normalized = pathPolicy.NormalizeRoot(trimmedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Problem("Zadaná cesta není platná.", statusCode: StatusCodes.Status400BadRequest);
        }

        List<DirectoryEntry> directories = [];
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(normalized, "*", _enumerationOptions))
            {
                directories.Add(new DirectoryEntry(Path.GetFileName(directory), directory));
            }
        }
        catch (DirectoryNotFoundException)
        {
            return Problem($"Adresář '{normalized}' neexistuje.", statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UnauthorizedAccessException)
        {
            return Problem($"K adresáři '{normalized}' nemá aplikace přístup.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (IOException)
        {
            return Problem($"Cesta '{normalized}' není platný adresář.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Ordinal: s InvariantGlobalization=true stejně neexistují kulturní pravidla řazení.
        directories.Sort((first, second) => string.Compare(first.Name, second.Name, StringComparison.OrdinalIgnoreCase));

        // Rodič kořene disku je null → o úroveň výš se UI vrátí na seznam disků.
        var parent = Directory.GetParent(normalized)?.FullName;
        return Ok(new BrowseResponse(normalized, parent, IsDriveList: false, directories));
    }
}
