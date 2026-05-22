using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Stages;

namespace Eryph.GuestServices.Provisioning.Tests.Hosting;

/// <summary>
/// Guards the <see cref="ModuleRegistry"/> against someone adding a new
/// <see cref="IModule"/> implementation to the production assembly without
/// also adding it to the registry. Tests use reflection (fine in tests — not
/// trimmed); production code does not.
/// </summary>
public sealed class ModuleRegistryTests
{
    [Fact]
    public void ModuleRegistry_contains_every_concrete_IModule_in_the_production_assembly()
    {
        // Discover all concrete IModule types that carry [Stage] — the same
        // filter the old ModuleDiscovery.DiscoverModules applied.
        var discoveredTypes = typeof(IModule).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => typeof(IModule).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes(typeof(StageAttribute), inherit: false).Length > 0)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        var registeredTypes = ModuleRegistry.ModuleTypes
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        registeredTypes.Should().BeEquivalentTo(discoveredTypes,
            "every concrete IModule decorated with [Stage] must appear in ModuleRegistry");
    }
}
