using System.Buffers;
using System.Security.Cryptography;

namespace FileSystemChangeTracker.Api.Services;

/// <summary>
/// Spočítá hash obsahu souboru. <c>reportBytesReadAsync</c> se volá průběžně po každém přečteném
/// bloku, aby se dal sledovat postup i uvnitř velkého souboru (jinak by čítače i živé změny
/// u mnoha-GB souboru vypadaly minuty zamrzle). Callback je asynchronní, aby volající mohl
/// z bloku publikovat mezistav.
/// </summary>
public interface IFileHasher
{
    Task<string> ComputeHashAsync(string fullPath, Func<int, ValueTask>? reportBytesReadAsync, CancellationToken cancellationToken);
}

/// <summary>
/// SHA-256 počítaný streamově po blocích – obsah souboru se nikdy nenačítá celý do paměti
/// a každý přečtený blok se hlásí volajícímu.
/// </summary>
public sealed class Sha256FileHasher : IFileHasher
{
    private const int BufferSizeBytes = 1024 * 1024;

    public async Task<string> ComputeHashAsync(string fullPath, Func<int, ValueTask>? reportBytesReadAsync, CancellationToken cancellationToken)
    {
        // ReadWrite | Delete: soubor právě otevřený jiným procesem pro zápis (aktivní log,
        // otevřený dokument, databázový soubor) musí jít přečíst. S pouhým FileShare.Read by
        // každé otevření skončilo sharing violation a soubor by byl při každém běhu trvale
        // "nečitelný" — bez verze a bez detekce změn. Hash rozepsaného souboru je momentka,
        // příští běh ho porovná znovu.
        FileStreamOptions options = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        await using FileStream stream = new(fullPath, options);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSizeBytes);
        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, BufferSizeBytes), cancellationToken)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, bytesRead));
                if (reportBytesReadAsync is not null)
                {
                    await reportBytesReadAsync(bytesRead);
                }
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
