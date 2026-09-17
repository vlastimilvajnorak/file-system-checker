using FileSystemChangeTracker.Api.Services;

namespace FileSystemChangeTracker.Tests;

/// <summary>Testy normalizace cest a platformního chování klíče snapshotu.</summary>
public class PathPolicyTests
{
    private readonly PathPolicy _pathPolicy = new();

    [Fact]
    public void NormalizeRoot_TrimsTrailingSeparator()
    {
        var root = Path.Combine(Path.GetTempPath(), "watched");

        var normalized = _pathPolicy.NormalizeRoot(root + Path.DirectorySeparatorChar);

        Assert.Equal(root, normalized);
    }

    [Fact]
    public void NormalizeRoot_StripsExtendedLengthPrefixOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // \\?\ prefix je čistě windowsovský
        }

        // \\?\C:\Data musí dát stejný normalizovaný kořen (a tedy klíč snapshotu) jako C:\Data.
        Assert.Equal(@"C:\Data", _pathPolicy.NormalizeRoot(@"\\?\C:\Data"));
        Assert.Equal(@"\\server\share\data", _pathPolicy.NormalizeRoot(@"\\?\UNC\server\share\data"));
    }

    [Fact]
    public void SnapshotKey_IsCaseInsensitiveOnWindowsOnly()
    {
        var lower = Path.Combine(Path.GetTempPath(), "data");
        var upper = Path.Combine(Path.GetTempPath(), "DATA");

        var sameKey = _pathPolicy.SnapshotKey(lower) == _pathPolicy.SnapshotKey(upper);

        // Windows je case-insensitive (C:\Data a c:\data je tentýž adresář), Linux ne.
        Assert.Equal(OperatingSystem.IsWindows(), sameKey);
    }

    [Fact]
    public void SnapshotKey_DiffersForDifferentPaths()
    {
        var first = Path.Combine(Path.GetTempPath(), "alpha");
        var second = Path.Combine(Path.GetTempPath(), "beta");

        Assert.NotEqual(_pathPolicy.SnapshotKey(first), _pathPolicy.SnapshotKey(second));
    }

    [Fact]
    public void ToRelative_UsesForwardSlashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        var nested = Path.Combine(root, "sub", "file.txt");

        Assert.Equal("sub/file.txt", _pathPolicy.ToRelative(root, nested));
    }
}
