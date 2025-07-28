using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Eryph.GuestServices.HvDataExchange.Guest;

[SupportedOSPlatform("windows")]
public class WindowsGuestDataExchange : IGuestDataExchange
{
    private const string ExternalPoolRegistryKey = @"SOFTWARE\Microsoft\Virtual Machine\External";
    private const string GuestPoolRegistryKey = @"SOFTWARE\Microsoft\Virtual Machine\Guest";

    public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
    {
        return GetData(ExternalPoolRegistryKey);
    }

    public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
    {
        return GetData(GuestPoolRegistryKey);
    }

    private async Task<IReadOnlyDictionary<string, string>> GetData(string pool)
    {
        return await Task.Run(() =>
        {
            var result = new Dictionary<string, string>();
            var guestKey = Registry.LocalMachine.CreateSubKey(pool);
            
            foreach (var valueName in guestKey.GetValueNames())
            {
                var value = guestKey.GetValue(valueName);
                if (value is string s)
                {
                    result[valueName] = s;
                }
            }

            return result;
        });
    }

    public Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values)
    {
        return SetData(GuestPoolRegistryKey, values);
    }

    private async Task SetData(
        string pool,
        IReadOnlyDictionary<string, string?> values)
    {
        await Task.Run(() =>
        {
            var poolKey = Registry.LocalMachine.CreateSubKey(pool);
            foreach (var kvp in values)
            {
                if (kvp.Value is null)
                {
                    poolKey.DeleteValue(kvp.Key, false);
                }
                else
                {
                    poolKey.SetValue(kvp.Key, kvp.Value);
                }
            }
        });
    }
}
