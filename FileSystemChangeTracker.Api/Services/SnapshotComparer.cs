using FileSystemChangeTracker.Api.Models;

namespace FileSystemChangeTracker.Api.Services;

/// <summary>Porovná aktuální průchod s předchozím snapshotem. Čistá logika bez I/O.</summary>
public interface ISnapshotComparer
{
    ComparisonOutcome Compare(string normalizedRoot, DirectorySnapshot? previous, ScanResult current);
}

/// <inheritdoc />
public sealed class SnapshotComparer(IPathPolicy pathPolicy) : ISnapshotComparer
{
    public ComparisonOutcome Compare(string normalizedRoot, DirectorySnapshot? previous, ScanResult current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var comparer = pathPolicy.PathComparer;
        var isInitial = previous is null;

        var previousFiles = previous is null
            ? new Dictionary<string, FileRecord>(comparer)
            : previous.Files.ToDictionary(file => file.RelativePath, comparer);

        List<FileRecord> snapshotFiles = new(current.Files.Count);
        List<NewFile> newFiles = [];
        List<ModifiedFile> modifiedFiles = [];

        foreach (var file in current.Files)
        {
            if (!previousFiles.TryGetValue(file.RelativePath, out var prior))
            {
                // Nový soubor: verze 1. Při prvním běhu (baseline) se ale nehlásí jako změna.
                snapshotFiles.Add(ToRecord(file, version: 1));
                if (!isInitial)
                {
                    newFiles.Add(new NewFile(file.RelativePath, 1));
                }
            }
            else if (!string.Equals(prior.Hash, file.Hash, StringComparison.Ordinal))
            {
                var version = prior.Version + 1;
                snapshotFiles.Add(ToRecord(file, version));
                modifiedFiles.Add(new ModifiedFile(file.RelativePath, version));
            }
            else
            {
                snapshotFiles.Add(ToRecord(file, prior.Version));
            }
        }

        // Nečitelné (zámek, oprávnění) i nestihnuté (zrušený průchod) položky mají stejnou
        // sémantiku: existují, jen o nich teď nejsou čerstvá data. Poslední známý záznam se
        // přenese beze změny, aby nevzniklo falešné "smazáno" a reset verze na 1.
        HashSet<string> carriedFilePaths = new(current.UnreadableFiles, comparer);
        carriedFilePaths.UnionWith(current.UnscannedFiles);
        CarriedDirectorySet carriedDirectories = new([.. current.UnreadableDirectories, .. current.UnscannedDirectories], comparer);

        List<DeletedFile> deletedFiles = [];
        if (previous is not null)
        {
            HashSet<string> currentPaths = new(current.Files.Select(file => file.RelativePath), comparer);
            foreach (var prior in previous.Files)
            {
                if (currentPaths.Contains(prior.RelativePath))
                {
                    continue;
                }

                if (carriedFilePaths.Contains(prior.RelativePath)
                    || carriedDirectories.ContainsAncestorOf(prior.RelativePath))
                {
                    snapshotFiles.Add(prior);
                }
                else
                {
                    deletedFiles.Add(new DeletedFile(prior.RelativePath, prior.Version));
                }
            }
        }

        (var newDirectories, var deletedDirectories, var snapshotDirectories) =
            CompareDirectories(previous, current, comparer, isInitial, carriedDirectories);

        DirectorySnapshot snapshot = new()
        {
            Root = normalizedRoot,
            Files = snapshotFiles,
            Directories = snapshotDirectories,
        };

        AnalysisResult result = new()
        {
            Path = normalizedRoot,
            InitialScan = isInitial,
            Partial = current.WasCancelled,
            NewFiles = newFiles,
            NewDirectories = newDirectories,
            ModifiedFiles = modifiedFiles,
            DeletedFiles = deletedFiles,
            DeletedDirectories = deletedDirectories,
            Warnings = current.Warnings,
        };

        return new ComparisonOutcome(snapshot, result);
    }

    private static (List<DirectoryChange> New, List<DirectoryChange> Deleted, IReadOnlyList<string> Snapshot) CompareDirectories(
        DirectorySnapshot? previous,
        ScanResult current,
        StringComparer comparer,
        bool isInitial,
        CarriedDirectorySet carriedDirectories)
    {
        List<DirectoryChange> newDirectories = [];
        List<DirectoryChange> deletedDirectories = [];
        if (isInitial || previous is null)
        {
            return (newDirectories, deletedDirectories, current.Directories);
        }

        HashSet<string> previousDirs = new(previous.Directories, comparer);
        HashSet<string> currentDirs = new(current.Directories, comparer);

        foreach (var directory in current.Directories)
        {
            if (!previousDirs.Contains(directory))
            {
                newDirectories.Add(new DirectoryChange(directory));
            }
        }

        // Podadresáře neprojitého (nečitelného či nestihnutého) adresáře nebyly vyjmenovány —
        // nejsou smazané, jen o nich nejsou čerstvá data. Do snapshotu se přenesou z minula,
        // aby se po zprůchodnění nehlásily falešně jako nové.
        List<string> snapshotDirectories = [.. current.Directories];
        foreach (var directory in previous.Directories)
        {
            if (currentDirs.Contains(directory))
            {
                continue;
            }

            if (carriedDirectories.ContainsAncestorOf(directory))
            {
                snapshotDirectories.Add(directory);
            }
            else
            {
                deletedDirectories.Add(new DirectoryChange(directory));
            }
        }

        return (newDirectories, deletedDirectories, snapshotDirectories);
    }

    /// <summary>
    /// Množina neprojitých (nečitelných či nestihnutých) adresářů s dotazem "leží cesta uvnitř
    /// některého z nich?". Dotaz jde po rodičích cesty (O(hloubka)), ne přes celý seznam:
    /// při zrušení BFS nad velkým stromem má fronta nestihnutých adresářů desítky tisíc položek
    /// a lineární prefixové porovnání pro každý soubor minulého snapshotu by porovnání
    /// (živé i finální) prodloužilo na hodiny — skener by uvízl a nešel ani zrušit.
    /// </summary>
    private sealed class CarriedDirectorySet
    {
        private readonly HashSet<string> _directories;
        private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;
        private readonly bool _includesRoot;

        public CarriedDirectorySet(IEnumerable<string> directories, StringComparer comparer)
        {
            _directories = new HashSet<string>(comparer);
            foreach (var directory in directories)
            {
                // Prázdná relativní cesta = kořen: nic se neprojelo, převzít vše.
                if (directory.Length == 0)
                {
                    _includesRoot = true;
                }
                else
                {
                    _directories.Add(directory);
                }
            }

            _lookup = _directories.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        /// <summary>Je některý z (vlastních) rodičů cesty neprojitý? Sama cesta se nepočítá.</summary>
        public bool ContainsAncestorOf(string relativePath)
        {
            if (_includesRoot)
            {
                return true;
            }

            if (_directories.Count == 0)
            {
                return false;
            }

            var end = relativePath.LastIndexOf('/');
            while (end > 0)
            {
                if (_lookup.Contains(relativePath.AsSpan(0, end)))
                {
                    return true;
                }

                end = relativePath.LastIndexOf('/', end - 1);
            }

            return false;
        }
    }

    private static FileRecord ToRecord(ScannedFile file, int version) => new()
    {
        RelativePath = file.RelativePath,
        Hash = file.Hash,
        Version = version,
    };
}
