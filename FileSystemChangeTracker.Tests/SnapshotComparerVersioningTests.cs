namespace FileSystemChangeTracker.Tests;

/// <summary>Skupina: pravidla verzování — baseline, nové, změněné, nezměněné a smazané soubory.</summary>
public class SnapshotComparerVersioningTests : SnapshotComparerTestBase
{
    [Fact]
    public void FirstRun_IsBaseline_NoChanges_AllFilesVersion1()
    {
        var current = Scan(File("a.txt", "h1"), File("sub/b.txt", "h2"));

        var outcome = Comparer.Compare(Root, previous: null, current);

        Assert.True(outcome.Result.InitialScan);
        Assert.Empty(outcome.Result.NewFiles);
        Assert.Empty(outcome.Result.ModifiedFiles);
        Assert.Empty(outcome.Result.DeletedFiles);
        Assert.All(outcome.Snapshot.Files, file => Assert.Equal(1, file.Version));
    }

    [Fact]
    public void NewFile_IsReported_WithVersion1()
    {
        var previous = Snapshot(Record("a.txt", "h1", version: 1));
        var current = Scan(File("a.txt", "h1"), File("new.txt", "h2"));

        var outcome = Comparer.Compare(Root, previous, current);

        var added = Assert.Single(outcome.Result.NewFiles);
        Assert.Equal("new.txt", added.Path);
        Assert.Equal(1, added.Version);
        Assert.False(outcome.Result.InitialScan);
    }

    [Fact]
    public void ModifiedFile_IncrementsVersion()
    {
        var previous = Snapshot(Record("a.txt", "old", version: 2));
        var current = Scan(File("a.txt", "new"));

        var outcome = Comparer.Compare(Root, previous, current);

        var changed = Assert.Single(outcome.Result.ModifiedFiles);
        Assert.Equal("a.txt", changed.Path);
        Assert.Equal(3, changed.Version);
        Assert.Equal(3, Assert.Single(outcome.Snapshot.Files).Version);
    }

    [Fact]
    public void UnchangedFile_KeepsVersion_AndIsNotReported()
    {
        var previous = Snapshot(Record("a.txt", "h1", version: 5));
        var current = Scan(File("a.txt", "h1"));

        var outcome = Comparer.Compare(Root, previous, current);

        Assert.Empty(outcome.Result.NewFiles);
        Assert.Empty(outcome.Result.ModifiedFiles);
        Assert.Equal(5, Assert.Single(outcome.Snapshot.Files).Version);
    }

    [Fact]
    public void DeletedFile_IsReported_WithLastKnownVersion()
    {
        var previous = Snapshot(Record("gone.txt", "h1", version: 4));
        var current = Scan();

        var outcome = Comparer.Compare(Root, previous, current);

        var deleted = Assert.Single(outcome.Result.DeletedFiles);
        Assert.Equal("gone.txt", deleted.Path);
        Assert.Equal(4, deleted.LastKnownVersion);
    }

    [Fact]
    public void RecreatedFile_AfterDeletion_StartsAtVersion1()
    {
        // Předchozí snapshot soubor neobsahuje (byl dříve smazán), teď se objeví znovu → nový, verze 1.
        var previous = Snapshot(Record("other.txt", "h1", version: 1));
        var current = Scan(File("other.txt", "h1"), File("phoenix.txt", "h2"));

        var outcome = Comparer.Compare(Root, previous, current);

        var recreated = Assert.Single(outcome.Result.NewFiles);
        Assert.Equal("phoenix.txt", recreated.Path);
        Assert.Equal(1, recreated.Version);
    }

    [Fact]
    public void RenamedFile_IsReportedAsDeletedPlusNew_WithVersionReset()
    {
        // Přejmenování se bez sledování file-id nepozná: stejný obsah (hash) na nové cestě.
        // Hlásí se jako smazání staré cesty (s poslední známou verzí) + nový soubor s verzí 1
        // a stará cesta ze snapshotu mizí — historie verzí se přes přejmenování nepřenáší.
        var previous = Snapshot(Record("stary.txt", "h1", version: 3));
        var current = Scan(File("novy.txt", "h1"));

        var outcome = Comparer.Compare(Root, previous, current);

        var deleted = Assert.Single(outcome.Result.DeletedFiles);
        Assert.Equal("stary.txt", deleted.Path);
        Assert.Equal(3, deleted.LastKnownVersion);
        var added = Assert.Single(outcome.Result.NewFiles);
        Assert.Equal("novy.txt", added.Path);
        Assert.Equal(1, added.Version);
        var record = Assert.Single(outcome.Snapshot.Files);
        Assert.Equal("novy.txt", record.RelativePath);
        Assert.Equal(1, record.Version);
    }
}
