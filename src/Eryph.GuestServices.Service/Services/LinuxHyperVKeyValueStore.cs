using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Service.Services;

public class LinuxHyperVKeyValueStore : IHyperVKeyValueStore
{
    private const int HV_KVP_EXCHANGE_MAX_VALUE_SIZE = 2048;
    private const int HV_KVP_EXCHANGE_MAX_KEY_SIZE = 512;
    private const int HV_KVP_EXCHANGE_MAX_KVP_SIZE = HV_KVP_EXCHANGE_MAX_KEY_SIZE + HV_KVP_EXCHANGE_MAX_VALUE_SIZE;

    public async Task<string?> GetValueAsync(string key)
    {
        // TODO validation
        await using var fileStream = File.Open("/var/lib/hyperv/.kvp_pool_1", FileMode.Open, FileAccess.ReadWrite);
        var values = await ReadValues(fileStream);
        return values.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetValueAsync(string key, string value)
    {
        // TODO validation
        await using var fileStream = File.Open("/var/lib/hyperv/.kvp_pool_1", FileMode.Open, FileAccess.ReadWrite);
        var values = await ReadValues(fileStream);
        values[key] = value;
        await WriteValues(fileStream, values);
    }

    private async Task<IDictionary<string, string>> ReadValues(FileStream fileStream)
    {
        var values = new Dictionary<string, string>();
        const int maxSize = HV_KVP_EXCHANGE_MAX_KEY_SIZE + HV_KVP_EXCHANGE_MAX_VALUE_SIZE;
        using var bufferOwner = MemoryPool<byte>.Shared.Rent(maxSize);
        var buffer = bufferOwner.Memory;
        while (fileStream.Position < fileStream.Length)
        {
            await fileStream.ReadExactlyAsync(buffer);
            var keySpan = buffer.Span[..HV_KVP_EXCHANGE_MAX_KEY_SIZE];
            var key = Encoding.UTF8.GetString(keySpan[..keySpan.IndexOf((byte)0)]);
            var valueSpan = buffer.Span[HV_KVP_EXCHANGE_MAX_KEY_SIZE..];
            var value = Encoding.UTF8.GetString(valueSpan[..valueSpan.IndexOf((byte)0)]);
            values[key] = value;
        }

        return values;
    }

    private async Task WriteValues(FileStream fileStream, IDictionary<string, string> values)
    {
        // TODO Validation

        fileStream.Seek(0, SeekOrigin.Begin);
        fileStream.SetLength(values.Count * HV_KVP_EXCHANGE_MAX_KVP_SIZE);

        using var bufferOwner = MemoryPool<byte>.Shared.Rent(HV_KVP_EXCHANGE_MAX_KVP_SIZE);
        var buffer = bufferOwner.Memory;

        foreach (var kvp in values)
        {
            buffer.Span.Clear();
            Encoding.UTF8.GetBytes(kvp.Key, buffer.Span[..HV_KVP_EXCHANGE_MAX_KEY_SIZE]);
            Encoding.UTF8.GetBytes(kvp.Value, buffer.Span[HV_KVP_EXCHANGE_MAX_KEY_SIZE..]);
            await fileStream.WriteAsync(buffer);
        }
    }
}
