using System.Reflection;
using Eryph.GuestServices.Provisioning.Stages;

namespace Eryph.GuestServices.Provisioning.Hosting;

internal static class HandlerDiscovery
{
    public static IReadOnlyList<Type> DiscoverHandlers(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => typeof(IHandler).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<StageAttribute>() is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
}
