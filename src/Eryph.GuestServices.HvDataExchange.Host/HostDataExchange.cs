using System.Management;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Eryph.GuestServices.HvDataExchange.Host;

public class HostDataExchange : IHostDataExchange
{
    public IReadOnlyDictionary<string, string> GetGuestData(Guid vmId)
    {
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(@"root\virtualization\v2"),
            new ObjectQuery("SELECT SystemName,GuestExchangeItems "
                            + "FROM Msvm_KvpExchangeComponent "
                            + $"WHERE SystemName = '{vmId.ToString().ToUpperInvariant()}'"));
        using var collection = searcher.Get();
        var managementObjects = collection.Cast<ManagementBaseObject>().ToList();
        try
        {
            var mo = managementObjects.SingleOrDefault();
            if (mo is null || mo["GuestExchangeItems"] is not string[] items)
                return new Dictionary<string, string>();

            var keyValuePairs = items
                .Select(item =>
                {
                    // The item itself is returned as XML. Hence, we use XPath to extract the
                    // actual key and value.
                    var xml = XDocument.Parse(item);
                    var name = xml.XPathSelectElement("/INSTANCE/PROPERTY[@NAME='Name']/VALUE")!.Value;
                    var data = xml.XPathSelectElement("/INSTANCE/PROPERTY[@NAME='Data']/VALUE")?.Value;
                    return new KeyValuePair<string, string>(name, data ?? "");
                })
                .ToList();

            return new Dictionary<string, string>(keyValuePairs);
        }
        finally
        {
            // The ManagementBaseObjects must be explicitly disposed as they
            // hold COM objects. Furthermore, ManagementBaseObject.Dispose()
            // does only work correctly when being invoked directly.
            // The method is defined with the new keyword and will not be invoked
            // via the IDisposable interface (e.g. with a using statement).
            foreach (var managementObject in managementObjects)
            {
                managementObject.Dispose();
            }
        }
    }
}
