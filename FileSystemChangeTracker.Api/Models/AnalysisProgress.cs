namespace FileSystemChangeTracker.Api.Models;

/// <summary>
/// Průběžné čítače běžící analýzy. Zapisuje je vlákno skeneru, čtou je GET requesty,
/// proto jsou aktualizace přes <see cref="Interlocked"/> a čtení vrací konzistentní snímek.
/// Procenta se nehlásí záměrně: celkový počet souborů není bez druhého průchodu znám.
/// </summary>
public sealed class AnalysisProgress
{
    private long _directoriesScanned;
    private long _filesHashed;
    private long _bytesHashed;
    private volatile string? _currentFile;
    private volatile AnalysisResult? _liveResult;

    /// <summary>
    /// Průběžně detekované změny: výstup porovnání nad mezistavem skenu (aktualizace ~1×/s).
    /// Čtou je GET/SSE během běhu; po dokončení má přednost finální výsledek.
    /// </summary>
    public AnalysisResult? LiveResult => _liveResult;

    public void PublishLiveResult(AnalysisResult result) => _liveResult = result;

    public void DirectoryScanned() => Interlocked.Increment(ref _directoriesScanned);

    public void FileHashed() => Interlocked.Increment(ref _filesHashed);

    /// <summary>Přičte průběžně přečtené bajty — hlásí se po blocích už během hashování souboru.</summary>
    public void BytesRead(int byteCount) => Interlocked.Add(ref _bytesHashed, byteCount);

    /// <summary>Relativní cesta právě zpracovávaného souboru; null = žádný (fronta/dokončeno).</summary>
    public void SetCurrentFile(string? relativePath) => _currentFile = relativePath;

    public AnalysisProgressSnapshot Snapshot() => new(
        Interlocked.Read(ref _directoriesScanned),
        Interlocked.Read(ref _filesHashed),
        Interlocked.Read(ref _bytesHashed),
        _currentFile);
}

/// <summary>Konzistentní snímek průběhu pro odpověď API.</summary>
public sealed record AnalysisProgressSnapshot(
    long DirectoriesScanned,
    long FilesHashed,
    long BytesHashed,
    string? CurrentFile);
