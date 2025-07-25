using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Eryph.GuestServices.HvDataExchange.Guest;

[SupportedOSPlatform("windows")]
public class WindowsGuestDataExchange : IGuestDataExchange
{
    private const string DxRegistryKey = @"SOFTWARE\Microsoft\Virtual Machine\Guest";

    public Task<IReadOnlyDictionary<string, string>> GetExternalData()
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyDictionary<string, string>> GetGuestData()
    {
        var result = new Dictionary<string, string>();
        var guestKey = Registry.LocalMachine.CreateSubKey(DxRegistryKey);
        foreach (var valueName in guestKey.GetValueNames())
        {
            var value = guestKey.GetValue(valueName);
            if (value is string s)
            {
                result[valueName] = s;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>> (result);
    }

    public Task SetGuestValues(IReadOnlyDictionary<string, string?> values)
    {
        // TODO validation

        var guestKey = Registry.LocalMachine.CreateSubKey(DxRegistryKey);
        foreach (var kvp in values)
        {
            if (kvp.Value is null)
            {
                guestKey.DeleteValue(kvp.Key, false);
                
            }
            else
            {
                guestKey.SetValue(kvp.Key, kvp.Value);
            }
        }

        return Task.CompletedTask;
    }
}
