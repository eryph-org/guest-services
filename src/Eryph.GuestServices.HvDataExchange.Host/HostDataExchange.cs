using System.Diagnostics;
using System.Management;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Eryph.GuestServices.HvDataExchange.Host;

public class HostDataExchange : IHostDataExchange
{
    private const string Scope = @"root\virtualization\v2";
    private static readonly TimeSpan TimeOut = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync(Guid vmId)
    {
        return await Task.Run(() =>
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(Scope),
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

                return ParseItems(items);
            }
            finally
            {
                DisposeAll(managementObjects);
            }
        });
    }

    public async Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync(Guid vmId)
    {
        return await Task.Run(() =>
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(Scope),
                new ObjectQuery("SELECT InstanceID,HostExchangeItems "
                                + "FROM Msvm_KvpExchangeComponentSettingData "
                                + @$"WHERE InstanceID LIKE 'Microsoft:{vmId.ToString().ToUpperInvariant()}\\%'"));

            using var collection = searcher.Get();
            var managementObjects = collection.Cast<ManagementBaseObject>().ToList();
            try
            {
                var mo = managementObjects.SingleOrDefault();
                if (mo is null || mo["HostExchangeItems"] is not string[] items)
                    return new Dictionary<string, string>();

                return ParseItems(items);
            }
            finally
            {
                DisposeAll(managementObjects);
            }
        });
    }
    
    private IReadOnlyDictionary<string, string> ParseItems(string[] items)
    {
        var parsed = items.Select(item =>
        {
            // The item itself is returned as XML. Hence, we use XPath to extract the
            // actual key and value.
            var xml = XDocument.Parse(item);
            var name = xml.XPathSelectElement("/INSTANCE/PROPERTY[@NAME='Name']/VALUE")!.Value;
            var data = xml.XPathSelectElement("/INSTANCE/PROPERTY[@NAME='Data']/VALUE")?.Value;
            return new KeyValuePair<string, string>(name, data ?? "");
        });

        return new Dictionary<string, string>(parsed);
    }

    public async Task SetExternalDataAsync(Guid vmId, IReadOnlyDictionary<string, string?> values)
    {
        await Task.Run(async () =>
        {
            var existingValues = await GetExternalDataAsync(vmId);

            var missing = values
                .Where(kvp => !existingValues.ContainsKey(kvp.Key) && kvp.Value is not null)
                .ToList();

            var changed = values
                .Where(kvp => existingValues.ContainsKey(kvp.Key) && kvp.Value is not null)
                .ToList();

            var removed = values
                .Where(kvp => existingValues.ContainsKey(kvp.Key) && kvp.Value is null)
                .ToList();

            ManagementObject? vmms = null;
            ManagementBaseObject? vm = null;
            try
            {
                vmms = GetVmms();
                vm = GetVm(vmId);
                if (vm is null)
                    throw new Exception($"The virtual machine with ID '{vmId}' does not exist.");

                // The WMI API of Hyper-V for the KVP exchange requires us to explicitly
                // split create, update and delete operations into three separate method calls.
                await InvokeMethod(vmms, "AddKvpItems", vm, missing);
                await InvokeMethod(vmms, "ModifyKvpItems", vm, changed);
                await InvokeMethod(vmms, "RemoveKvpItems", vm, removed);
            }
            finally
            {
                vmms?.Dispose();
                vm?.Dispose();
            }
        });
    }

    private async Task InvokeMethod(
        ManagementObject vmms,
        string method,
        ManagementBaseObject vm,
        IList<KeyValuePair<string, string?>> values)
    {
        using var parameters = vmms.GetMethodParameters(method);
        parameters["TargetSystem"] = vm;
        parameters["DataItems"] = CreateItems(vmms, values);
        var result = vmms.InvokeMethod(method, parameters, null);
        var returnValue = (uint)result["ReturnValue"];

        if (returnValue == 0)
            return;

        if (returnValue != 4096)
            throw new Exception($"{method} failed with result '{ConvertReturnValue(returnValue)}'");

        var jobPath = (string)result["Job"];
        await WaitForJob(jobPath);
    }

    private string[] CreateItems(
        ManagementObject vmms,
        IEnumerable<KeyValuePair<string, string?>> values)
    {
        using var @class = new ManagementClass(
            @$"\\{vmms.ClassPath.Server}\{vmms.ClassPath.NamespacePath}:Msvm_KvpExchangeDataItem");

        var result = new List<string>();
        foreach (var kvp in values)
        {
            var kvpItem = @class.CreateInstance();
            try
            {
                kvpItem["Name"] = kvp.Key;
                // The actual data cannot be null, but we allow the user to specify
                // null to indicate that the key should be removed. Hyper-V knows based
                // on the invoked method whether the key is updated or removed.
                // Hence, we can just change null to an empty string here.
                kvpItem["Data"] = kvp.Value ?? "";
                kvpItem["Source"] = 0;
                result.Add(kvpItem.GetText(TextFormat.WmiDtd20));
            }
            finally
            {
                kvpItem.Dispose();
            }
        }

        return result.ToArray();
    }

    private ManagementObject GetVmms()
    {
        using var @class = new ManagementClass(
            new ManagementScope(Scope),
            new ManagementPath("Msvm_VirtualSystemManagementService"),
            null);
        using var instances = @class.GetInstances();
        var managementObjects = instances.Cast<ManagementBaseObject>().ToList();
        try
        {
            return (ManagementObject)managementObjects.Single();
        }
        finally
        {
            DisposeAll(managementObjects);
        }
    }

    private ManagementBaseObject? GetVm(Guid vmId)
    {
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(Scope),
            new ObjectQuery("SELECT * "
                            + "FROM Msvm_ComputerSystem "
                            + $"WHERE Name = '{vmId.ToString().ToUpperInvariant()}'"));
        using var collection = searcher.Get();
        var managementObjects = collection.Cast<ManagementBaseObject>().ToList();
        try
        {
            return managementObjects.SingleOrDefault();
        }
        finally
        {
            DisposeAll(managementObjects);
        }
    }

    private async Task WaitForJob(string jobPath)
    {
        var stopwatch = Stopwatch.StartNew(); 
        var job = new ManagementObject(jobPath);
        try
        {
            job.Get();
            while (IsJobRunning((ushort)job["JobState"]) && stopwatch.Elapsed <= TimeOut)
            {
                await Task.Delay(PollingInterval).ConfigureAwait(false);
                job.Get();
            }

            if (!IsJobCompleted((ushort)job["JobState"]))
                throw new Exception("The job did not complete successfully within the allotted time."
                                 + $"The last reported state was {ConvertJobState((ushort)job["JobState"])}.");
        }
        finally
        {
            job.Dispose();
        }
    }

    /// <summary>
    /// Disposes the given <paramref name="managementObjects"/>.
    /// </summary>
    /// <remarks>
    /// The <see cref="ManagementBaseObject"/>s must be explicitly disposed as they
    /// hold COM objects. Furthermore, <see cref="ManagementBaseObject.Dispose"/>
    /// does only work correctly when being invoked directly.
    /// The method is defined with the new keyword and will not be invoked
    /// via the <see cref="IDisposable"/> interface (e.g. with a using statement).
    /// </remarks>
    private static void DisposeAll(IList<ManagementBaseObject> managementObjects)
    {
        foreach (var managementObject in managementObjects)
        {
            managementObject.Dispose();
        }
    }

    private static string ConvertReturnValue(uint returnValue) =>
        returnValue switch
        {
            0 => "Completed",
            1 => "Not Supported",
            2 => "Failed",
            3 => "Timeout",
            4 => "Invalid Parameter",
            5 => "Invalid State",
            6 => "Incompatible Parameters",
            4096 => "Job Started",
            _ => $"Other ({returnValue})",
        };

    private static string ConvertJobState(ushort jobState) =>
        jobState switch
        {
            2 => "New",
            3 => "Starting",
            4 => "Running",
            5 => "Suspended",
            6 => "Shutting Down",
            7 => "Completed",
            8 => "Terminated",
            9 => "Killed",
            10 => "Exception",
            11 => "Service",
            _ => $"Other ({jobState})",
        };

    private static bool IsJobRunning(ushort jobState) =>
        jobState is 2 or 3 or 4 or 5 or 6;

    private static bool IsJobCompleted(ushort jobState) =>
        jobState is 7;
}
