using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Service.Services;

public interface IHyperVKeyValueStore
{
    public Task<string?> GetValueAsync(string key);
    
    public Task SetValueAsync(string key, string value);
}
