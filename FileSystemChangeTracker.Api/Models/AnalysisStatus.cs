namespace FileSystemChangeTracker.Api.Models;

/// <summary>
/// Stav úlohy analýzy v in-memory registru.
/// </summary>
public enum AnalysisStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}
