using FileSystemChangeTracker.Api.Services;

namespace FileSystemChangeTracker.Tests;

/// <summary>Testy hasheru: správnost SHA-256 a průběžné hlášení přečtených bajtů.</summary>
public class Sha256FileHasherTests
{
    [Fact]
    public async Task ComputesKnownSha256_AndReportsAllBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllTextAsync(path, "abc");
        try
        {
            var hasher = new Sha256FileHasher();
            long reportedBytes = 0;

            var hash = await hasher.ComputeHashAsync(
                path,
                byteCount => { reportedBytes += byteCount; return ValueTask.CompletedTask; },
                CancellationToken.None);

            // Známý testovací vektor SHA-256("abc").
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
            Assert.Equal(3, reportedBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadsFileHeldOpenForWritingByAnotherHandle()
    {
        // Aktivní log nebo otevřený dokument drží jiný proces pro zápis. Hasher ho musí přečíst
        // (FileShare.ReadWrite) — s pouhým FileShare.Read by otevření skončilo sharing violation
        // a soubor by byl při každém běhu trvale "nečitelný".
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllTextAsync(path, "abc");
        try
        {
            await using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                var hash = await new Sha256FileHasher().ComputeHashAsync(path, reportBytesReadAsync: null, CancellationToken.None);

                Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
