using FileSystemChangeTracker.Api.Models;
using FileSystemChangeTracker.Api.Services;

namespace FileSystemChangeTracker.Tests;

/// <summary>
/// Sdílená báze testů porovnávací logiky: instance compareru a tovární helpery pro
/// snapshoty a výsledky skenu. Jednotlivé skupiny testů jsou v samostatných souborech.
/// </summary>
public abstract class SnapshotComparerTestBase
{
    protected const string Root = "/data/watched";

    protected SnapshotComparer Comparer { get; } = new(new PathPolicy());

    protected static ScannedFile File(string relativePath, string hash) => new()
    {
        RelativePath = relativePath,
        Hash = hash,
    };

    protected static FileRecord Record(string relativePath, string hash, int version) => new()
    {
        RelativePath = relativePath,
        Hash = hash,
        Version = version,
    };

    protected static ScanResult Scan(params ScannedFile[] files) =>
        new() { Files = files, Directories = [] };

    protected static ScanResult Scan(string[] directories, params ScannedFile[] files) =>
        new() { Files = files, Directories = directories };

    protected static ScanResult ScanWithUnreadable(string[] unreadableFiles, params ScannedFile[] files) =>
        new() { Files = files, UnreadableFiles = unreadableFiles, Directories = [] };

    protected static DirectorySnapshot Snapshot(params FileRecord[] files) =>
        new() { Root = Root, Files = files, Directories = [] };

    protected static DirectorySnapshot Snapshot(string[] directories, params FileRecord[] files) =>
        new() { Root = Root, Files = files, Directories = directories };
}
