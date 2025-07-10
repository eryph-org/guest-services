using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Eryph.GuestServices.Service.Services;

[SupportedOSPlatform("windows")]
public class WindowsHyperVKeyValueStore : IHyperVKeyValueStore
{
    public Task<string?> GetValueAsync(string key)
    {
        var guestKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Virtual Machine\Guest");
        return Task.FromResult((string?)guestKey.GetValue(key));
    }

    public Task SetValueAsync(string key, string value)
    {
        var guestKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Virtual Machine\Guest");
        guestKey.SetValue(key, value);
        return Task.CompletedTask;
    }
}
