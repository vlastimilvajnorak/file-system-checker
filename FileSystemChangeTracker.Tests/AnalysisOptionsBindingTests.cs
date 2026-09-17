using FileSystemChangeTracker.Api.Options;
using Microsoft.Extensions.Configuration;

namespace FileSystemChangeTracker.Tests;

/// <summary>
/// Vazba konfigurace: výchozí hodnoty žijí v appsettings.json a musí se korektně navázat
/// na <see cref="AnalysisOptions"/>. Test čte skutečný appsettings.json z Api projektu,
/// takže odhalí rozjetí dokumentovaných defaultů a reality.
/// </summary>
public class AnalysisOptionsBindingTests
{
    private static string ApiProjectPath(string fileName) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "FileSystemChangeTracker.Api", fileName));

    [Fact]
    public void AppSettings_ContainsAnalysisSection_WithDocumentedDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(ApiProjectPath("appsettings.json"))
            .Build();

        var analysisOptions = configuration.GetSection(AnalysisOptions.SectionName).Get<AnalysisOptions>();

        Assert.NotNull(analysisOptions);
        Assert.Equal(TimeSpan.FromSeconds(30), analysisOptions.CheckpointInterval);
        Assert.Equal(8, analysisOptions.MaxConcurrentAnalyses);

        // Umístění snapshotů appsettings.json záměrně nenastavuje — "%LOCALAPPDATA%" by na
        // Linuxu zůstalo doslovným názvem adresáře. Platí multiplatformní default z kódu.
        Assert.Null(configuration["Analysis:SnapshotsDirectory"]);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileSystemChangeTracker", "snapshots"),
            analysisOptions.SnapshotsDirectory);
    }
}
