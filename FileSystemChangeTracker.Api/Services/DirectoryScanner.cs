using System.Diagnostics;
using FileSystemChangeTracker.Api.Models;

namespace FileSystemChangeTracker.Api.Services;

/// <summary>
/// Rekurzivně projde adresářový strom a spočítá hashe souborů. Volitelný <c>checkpointAsync</c>
/// je příjemce průběžných mezistavů (~1×/s): dostává konzistentní částečný
/// <see cref="ScanResult"/> se stejnou sémantikou jako zrušený průchod — volající z něj
/// publikuje živě detekované změny a průběžně ukládá odvedenou práci.
/// </summary>
public interface IDirectoryScanner
{
    Task<ScanResult> ScanAsync(
        string normalizedRoot,
        AnalysisProgress progress,
        Func<ScanResult, CancellationToken, Task>? checkpointAsync,
        CancellationToken cancellationToken);
}

/// <summary>
/// Ruční průchod stromem do šířky, adresář po adresáři. Oproti rekurzivní enumeraci
/// (<c>RecurseSubdirectories</c>) přežije selhání uvnitř kteréhokoli podadresáře — enumerace
/// umí vyhodit např. sharing violation na zamčené položce a shodila by celou analýzu.
/// Neprojditelný adresář se jen zaznamená a porovnání pro jeho podstrom přenese poslední
/// známý stav. Reparse pointy (symlinky, junctions) se přeskakují kvůli riziku cyklů.
/// Soubory, které selžou při čtení, se evidují zvlášť (viz <see cref="ScanResult"/>).
/// Zrušení nevyhazuje výjimku: průchod se zastaví a co se nestihlo, vrátí se
/// v <see cref="ScanResult.UnscannedFiles"/>/<see cref="ScanResult.UnscannedDirectories"/>,
/// aby porovnání mohlo zpracovanou část uložit a zbytek převzít z minulého stavu.
/// </summary>
public sealed class DirectoryScanner(
    IPathPolicy pathPolicy,
    IFileHasher fileHasher,
    ISnapshotStore snapshotStore) : IDirectoryScanner
{
    /// <summary>Jak často se volajícímu předává mezistav (živé změny; ukládání throttluje volající).</summary>
    private static readonly TimeSpan _publishInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Horní mez podílu času, který smí předávání mezistavů (kopie seznamů + porovnání se
    /// snapshotem) spotřebovat: příští mezistav se odloží nejméně na tolikanásobek trvání
    /// toho minulého. Nad stromem se statisíci soubory trvá jedno porovnání i stovky ms —
    /// bez meze by skener strávil většinu času porovnáváním místo hashováním.
    /// </summary>
    private const int MaxPublishOverheadRatio = 10;

    /// <summary>
    /// Vlastní úložiště snapshotů se při průchodu přeskakuje — při analýze stromu, který ho
    /// obsahuje (např. celý disk), by jinak každý běh hlásil změny vlastních stavových souborů.
    /// </summary>
    private readonly string _storageDirectory =
        Path.TrimEndingDirectorySeparator(snapshotStore.StorageDirectory);

    public async Task<ScanResult> ScanAsync(
        string normalizedRoot,
        AnalysisProgress progress,
        Func<ScanResult, CancellationToken, Task>? checkpointAsync,
        CancellationToken cancellationToken)
    {
        List<string> directories = [];
        List<ScannedFile> files = [];
        List<string> unreadableFiles = [];
        List<string> unreadableDirectories = [];
        List<string> unscannedFiles = [];
        List<string> unscannedDirectories = [];
        List<string> warnings = [];
        var wasCancelled = false;

        Queue<string> pendingDirectories = new();
        pendingDirectories.Enqueue(normalizedRoot);

        var publishEnabled = checkpointAsync is not null;
        var sinceLastPublish = Stopwatch.StartNew();
        var nextPublishAfter = _publishInterval;

        // Průběžné předání mezistavu: má stejnou merge sémantiku jako zrušený průchod
        // (zpracováno čerstvé, zbytek se převezme z minula). Volající z něj publikuje živě
        // detekované změny a v intervalu CheckpointInterval ukládá snapshot na disk.
        // Selhání předání sken nezastaví.
        async Task TryCheckpointAsync(IEnumerable<string> remainingFilesOfCurrentDirectory)
        {
            if (!publishEnabled || (sinceLastPublish.Elapsed < nextPublishAfter))
            {
                return;
            }

            var publishStarted = Stopwatch.GetTimestamp();
            List<string> unscannedDirectoriesNow =
                [.. pendingDirectories.Select(directory => ToRelativeDirectory(normalizedRoot, directory))];
            ScanResult checkpoint = new()
            {
                Files = [.. files],
                UnreadableFiles = [.. unreadableFiles],
                UnreadableDirectories = [.. unreadableDirectories],
                WasCancelled = true,
                // Vč. dříve nasbíraných nestihnutých souborů (zrušení uprostřed hashování) —
                // jinak by je mezistav krátce hlásil jako smazané.
                UnscannedFiles = [.. unscannedFiles, .. remainingFilesOfCurrentDirectory.Select(file => pathPolicy.ToRelative(normalizedRoot, file))],
                UnscannedDirectories = unscannedDirectoriesNow,
                Directories = [.. directories, .. unscannedDirectoriesNow],
                Warnings = [.. warnings],
            };

            try
            {
                await checkpointAsync!(checkpoint, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Zrušení řeší hlavní smyčka; rozpracovaný checkpoint není potřeba.
            }
            catch (Exception exception)
            {
                warnings.Add($"Průběžné uložení mezistavu se nepodařilo: {exception.Message}");
            }
            finally
            {
                var publishDuration = Stopwatch.GetElapsedTime(publishStarted);
                var overheadBound = publishDuration * MaxPublishOverheadRatio;
                nextPublishAfter = (overheadBound > _publishInterval) ? overheadBound : _publishInterval;
                sinceLastPublish.Restart();
            }
        }

        while (pendingDirectories.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
                break;
            }

            var currentDirectory = pendingDirectories.Dequeue();
            var relativeDirectory = ToRelativeDirectory(normalizedRoot, currentDirectory);

            // Nejdřív celý adresář vyjmenovat, teprve pak výsledky použít: když enumerace selže
            // uprostřed (zamčená položka, odebraná práva), nepoužije se z adresáře nic a porovnání
            // pro celý jeho podstrom přenese poslední známý stav ze snapshotu.
            List<string> childDirectories = [];
            List<string> childFiles = [];
            try
            {
                foreach (var entry in new DirectoryInfo(currentDirectory).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        if (pathPolicy.PathComparer.Equals(Path.TrimEndingDirectorySeparator(entry.FullName), _storageDirectory))
                        {
                            continue; // vlastní úložiště snapshotů se nesleduje
                        }

                        childDirectories.Add(entry.FullName);
                    }
                    else
                    {
                        childFiles.Add(entry.FullName);
                    }
                }
            }
            catch (DirectoryNotFoundException) when (relativeDirectory.Length > 0)
            {
                // Adresář zmizel mezi vyjmenováním rodičem a vlastním zpracováním – je skutečně
                // pryč, do snapshotu se nezařadí a jeho podstrom se nahlásí jako odstraněný.
                continue;
            }
            // Pozor: DirectoryNotFoundException dědí z IOException — bez vyloučení by následující
            // catch spolkl i zmizelý KOŘEN a analýza by tiše skončila Completed s carry-over celého
            // minulého stavu. Zmizelý kořen má vybublat: analýza selže a snapshot zůstane.
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)
                && exception is not DirectoryNotFoundException)
            {
                unreadableDirectories.Add(relativeDirectory);
                warnings.Add($"Adresář '{(relativeDirectory.Length == 0 ? "." : relativeDirectory)}' nelze projít: {exception.Message}");
                if (relativeDirectory.Length > 0)
                {
                    // Existuje (rodič ho vyjmenoval), jen je nedostupný – ve snapshotu zůstává.
                    directories.Add(relativeDirectory);
                }

                progress.DirectoryScanned();
                continue;
            }

            if (relativeDirectory.Length > 0)
            {
                directories.Add(relativeDirectory);
            }

            progress.DirectoryScanned();

            foreach (var childDirectory in childDirectories)
            {
                pendingDirectories.Enqueue(childDirectory);
            }

            for (var index = 0; index < childFiles.Count; index++)
            {
                var relative = pathPolicy.ToRelative(normalizedRoot, childFiles[index]);

                if (cancellationToken.IsCancellationRequested)
                {
                    // Zbytek souborů tohoto adresáře se už nestihl — přenese se z minulého stavu.
                    wasCancelled = true;
                    for (var remaining = index; remaining < childFiles.Count; remaining++)
                    {
                        unscannedFiles.Add(pathPolicy.ToRelative(normalizedRoot, childFiles[remaining]));
                    }

                    break;
                }

                progress.SetCurrentFile(relative);

                // Publish tick musí běžet i uvnitř dlouhého hashování (po 1MB blocích), jinak by
                // živé změny během mnoha-GB souboru stály. Rozečtený soubor patří do "nestihnutých".
                var currentIndex = index;
                async ValueTask ReportBlockAsync(int byteCount)
                {
                    progress.BytesRead(byteCount);
                    await TryCheckpointAsync(childFiles.Skip(currentIndex));
                }

                try
                {
                    var hash = await fileHasher.ComputeHashAsync(childFiles[index], ReportBlockAsync, cancellationToken);
                    files.Add(new ScannedFile
                    {
                        RelativePath = relative,
                        Hash = hash,
                    });
                    progress.FileHashed();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Zrušení uprostřed hashování: rozečtený soubor je "nestihnutý"; zbytek
                    // adresáře doplní kontrola na začátku další iterace. Příznak se ale musí
                    // nastavit hned tady — u posledního souboru už žádná další iterace není
                    // a výsledek by se jinak tvářil jako dokončený sken.
                    wasCancelled = true;
                    unscannedFiles.Add(relative);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Soubor zmizel mezi enumerací a čtením – je skutečně pryč, nahlásí se jako odstraněný.
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Soubor existuje, ale nejde přečíst (zámek, oprávnění). Eviduje se zvlášť, aby ho
                    // porovnání nepovažovalo za smazaný a nezahodilo jeho historii verzí.
                    unreadableFiles.Add(relative);
                    warnings.Add($"Soubor '{relative}' nelze přečíst: {exception.Message}");
                }

                await TryCheckpointAsync(childFiles.Skip(index + 1));
            }

            await TryCheckpointAsync([]);
        }

        if (wasCancelled)
        {
            // Nezpracovaný zbytek fronty: adresáře existují (rodiče je vyjmenovali), jen se do
            // nich nedošlo — do snapshotu patří a jejich podstromy převezme porovnání z minula.
            // Kořen (prázdná relativní cesta) do Directories nepatří nikdy; jako "nestihnutý"
            // prefix ale nese sémantiku "převzít vše".
            while (pendingDirectories.Count > 0)
            {
                var relativeDirectory = ToRelativeDirectory(normalizedRoot, pendingDirectories.Dequeue());
                if (relativeDirectory.Length > 0)
                {
                    directories.Add(relativeDirectory);
                }

                unscannedDirectories.Add(relativeDirectory);
            }

            // Co se se zpracovanou částí stane (rozšíření neúplné baseline vs. nic), rozhoduje
            // až AnalysisService — tady jen fakt, že výsledek nepokrývá celý strom.
            warnings.Add("Analýza byla zrušena — výsledek pokrývá jen zpracovanou část stromu.");
        }

        // Sken skončil – žádný „právě zpracovávaný" soubor. Při selhání uprostřed naopak
        // poslední hodnota zůstává, aby bylo vidět, na kterém souboru analýza skončila.
        progress.SetCurrentFile(null);

        return new ScanResult
        {
            Files = files,
            UnreadableFiles = unreadableFiles,
            UnreadableDirectories = unreadableDirectories,
            WasCancelled = wasCancelled,
            UnscannedFiles = unscannedFiles,
            UnscannedDirectories = unscannedDirectories,
            Directories = directories,
            Warnings = warnings,
        };
    }

    /// <summary>Relativní cesta adresáře; kořen se normalizuje z "." na prázdný prefix.</summary>
    private string ToRelativeDirectory(string normalizedRoot, string fullPath)
    {
        var relative = pathPolicy.ToRelative(normalizedRoot, fullPath);
        return (relative == ".") ? string.Empty : relative;
    }
}
