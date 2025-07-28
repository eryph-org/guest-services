using System.Buffers;
using System.Runtime.Versioning;
using System.Text;

namespace Eryph.GuestServices.HvDataExchange.Guest;

[SupportedOSPlatform("linux")]
public class LinuxGuestDataExchange : IGuestDataExchange
{
    /// <summary>
    /// The maximum size of a key. Defined by <c>HV_KVP_EXCHANGE_MAX_KEY_SIZE</c>.
    /// </summary>
    private const int MaxKeySize = 512;

    /// <summary>
    /// The maximum size of a value. Defined by <c>HV_KVP_EXCHANGE_MAX_VALUE_SIZE</c>.
    /// </summary>
    private const int MaxValueSize = 2048;
    
    private const int MaxKvpSize = MaxKeySize + MaxValueSize;

    private const string ExternalPoolFilePath = "/var/lib/hyperv/.kvp_pool_0";
    private const string GuestPoolFilePath = "/var/lib/hyperv/.kvp_pool_1";

    public async Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
    {
        await using var fileStream = File.Open(ExternalPoolFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var values = await ReadValues(fileStream);
        return values;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
    {
        await using var fileStream = File.Open(GuestPoolFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var values = await ReadValues(fileStream);
        return values;
    }

    public async Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values)
    {
        await using var fileStream = File.Open(GuestPoolFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var currentValues = await ReadValues(fileStream);

        // TODO add validation

        foreach (var kvp in values)
        {
            if (kvp.Value is null)
            {
                currentValues.Remove(kvp.Key);
            }
            else
            {
                currentValues[kvp.Key] = kvp.Value;
            }
        }

        await WriteValues(fileStream, currentValues);
    }

    private async Task<Dictionary<string, string>> ReadValues(FileStream fileStream)
    {
        var values = new Dictionary<string, string>();
        const int maxSize = MaxKeySize + MaxValueSize;
        using var bufferOwner = MemoryPool<byte>.Shared.Rent(maxSize);
        var buffer = bufferOwner.Memory;
        
        while (fileStream.Position < fileStream.Length)
        {
            await fileStream.ReadExactlyAsync(buffer);
            var keySpan = buffer.Span[..MaxKeySize];
            var valueSpan = buffer.Span[MaxKeySize..];

            // The strings are null-terminated.
            var key = Encoding.UTF8.GetString(keySpan[..keySpan.IndexOf((byte)0)]);
            var value = Encoding.UTF8.GetString(valueSpan[..valueSpan.IndexOf((byte)0)]);
            
            values[key] = value;
        }

        return values;
    }

    private async Task WriteValues(FileStream fileStream, IDictionary<string, string> values)
    {
        fileStream.Seek(0, SeekOrigin.Begin);
        fileStream.SetLength(values.Count * MaxKvpSize);

        using var bufferOwner = MemoryPool<byte>.Shared.Rent(MaxKvpSize);
        var buffer = bufferOwner.Memory;

        foreach (var kvp in values)
        {
            buffer.Span.Clear();
            Encoding.UTF8.GetBytes(kvp.Key, buffer.Span[..MaxKeySize]);
            Encoding.UTF8.GetBytes(kvp.Value, buffer.Span[MaxKeySize..]);
            await fileStream.WriteAsync(buffer);
        }
    }
}
