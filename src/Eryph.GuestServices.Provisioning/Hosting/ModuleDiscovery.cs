using System.Reflection;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Stages;

namespace Eryph.GuestServices.Provisioning.Hosting;

internal static class ModuleDiscovery
{
    public static IReadOnlyList<Type> DiscoverModules(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => typeof(IModule).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<StageAttribute>() is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
}
