using System.Security.Cryptography;
using System.Text;

namespace FileSystemChangeTracker.Api.Services;

/// <summary>
/// Normalizace cest a chování závislé na platformě (case sensitivity, separátory).
/// </summary>
public interface IPathPolicy
{
    /// <summary>Comparer pro porovnávání relativních cest podle aktuální platformy.</summary>
    StringComparer PathComparer { get; }

    /// <summary><see cref="StringComparison"/> odpovídající <see cref="PathComparer"/> (pro prefixová porovnání).</summary>
    StringComparison PathComparison { get; }

    /// <summary>Vstupní cestu převede na normalizovanou absolutní cestu bez koncového separátoru.</summary>
    string NormalizeRoot(string inputPath);

    /// <summary>Stabilní klíč (hex hash) pro název snapshot souboru. Na Windows case-insensitivní.</summary>
    string SnapshotKey(string normalizedRoot);

    /// <summary>Relativní cesta vůči kořeni s "/" jako separátorem.</summary>
    string ToRelative(string normalizedRoot, string fullPath);
}

/// <inheritdoc />
public sealed class PathPolicy : IPathPolicy
{
    public StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string NormalizeRoot(string inputPath)
    {
        // Extended-length prefixy (\\?\C:\..., \\?\UNC\server\...) GetFullPath záměrně nechává
        // beze změny — bez sjednocení by tentýž adresář dostal jiný klíč snapshotu a vlastní
        // baseline, což by rozbilo idempotenci i navazování na minulý stav.
        if (OperatingSystem.IsWindows())
        {
            if (inputPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                inputPath = @"\\" + inputPath[@"\\?\UNC\".Length..];
            }
            else if (inputPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                inputPath = inputPath[@"\\?\".Length..];
            }
        }

        var fullPath = Path.GetFullPath(inputPath);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public string SnapshotKey(string normalizedRoot)
    {
        // Na Windows je filesystem case-insensitivní, takže "C:\Data" a "c:\data" musí dát stejný klíč.
        // Normalizujeme na velká písmena (CA1308 doporučuje ToUpperInvariant).
        var keyText = OperatingSystem.IsWindows() ? normalizedRoot.ToUpperInvariant() : normalizedRoot;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyText));
        return Convert.ToHexStringLower(hash);
    }

    public string ToRelative(string normalizedRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(normalizedRoot, fullPath);

        // Jen na Windows: tam je '\' oddělovač. Na Linuxu je '\' legální znak v názvu souboru
        // a GetRelativePath už vrací '/' — plošná náhrada by názvy poškodila.
        return OperatingSystem.IsWindows() ? relative.Replace('\\', '/') : relative;
    }
}
