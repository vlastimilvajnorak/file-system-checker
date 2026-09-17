namespace FileSystemChangeTracker.Tests;

/// <summary>Skupina: detekce nových a odstraněných adresářů (adresáře nemají verzi).</summary>
public class SnapshotComparerDirectoryTests : SnapshotComparerTestBase
{
    [Fact]
    public void Directories_NewAndDeleted_AreReported()
    {
        var previous = Snapshot(["logs", "temp"]);
        var current = Scan(["logs", "logs/2026"]);

        var outcome = Comparer.Compare(Root, previous, current);

        Assert.Equal("logs/2026", Assert.Single(outcome.Result.NewDirectories).Path);
        Assert.Equal("temp", Assert.Single(outcome.Result.DeletedDirectories).Path);
    }
}
