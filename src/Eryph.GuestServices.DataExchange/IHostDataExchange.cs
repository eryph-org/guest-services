using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.DataExchange;

public interface IHostDataExchange
{
    public IReadOnlyDictionary<string, string> GetGuestData(Guid vmId);
}
