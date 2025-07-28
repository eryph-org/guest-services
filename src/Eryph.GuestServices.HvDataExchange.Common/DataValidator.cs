using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Eryph.GuestServices.HvDataExchange.Common;

/// <summary>
/// Validates keys and values for the Hyper-V key-value data exchange.
/// We need to validate both the length in characters and the size in bytes
/// as we are limited by both.
/// </summary>
public static class DataValidator
{
    private const int MaxKeyLength = 255;

    /// <summary>
    /// The maximum size of a key excluding the null terminator.
    /// Defined by <c>HV_KVP_EXCHANGE_MAX_KEY_SIZE</c>.
    /// </summary>
    private const int MaxKeySize = 511;

    private const int MaxValueLength = 1023;

    /// <summary>
    /// The maximum size of a key excluding the null terminator.
    /// Defined by <c>HV_KVP_EXCHANGE_MAX_VALUE_SIZE</c>.
    /// </summary>
    private const int MaxValueSize = 2047;


    public static bool IsKeyValid(string key, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            error = "The key must not be null or empty.";
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(key);
        if (key.Length <= MaxKeyLength && bytes.Length <= MaxKeySize)
            return true;

        error = $"The key must be at most {MaxKeyLength} characters long and {MaxKeySize} bytes in size.";
        return false;
    }
    
    public static bool IsValueValid(string? value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(value))
            return true;

        var bytes = Encoding.UTF8.GetBytes(value);
        if (value.Length <= MaxValueLength && bytes.Length <= MaxValueSize)
            return true;

        error = $"The value must be at most {MaxValueLength} characters long and {MaxValueSize} bytes in size.";
        return false;
    }
}
