using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.Semaphores;
using Eryph.GuestServices.Provisioning.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleInjector;
using SimpleInjector.Lifestyles;

namespace Eryph.GuestServices.Provisioning.Tests.Hosting;

/// <summary>
/// Regression coverage for the container composition.
///
/// The original bug: <see cref="ProvisioningContainerBuilder.Build"/> set
/// <see cref="SimpleInjector.ContainerOptions.DefaultScopedLifestyle"/> on
/// the container it created, but <see cref="ProvisioningContainerBuilder.RegisterInto"/>
/// did not. <c>egs-service</c> calls <c>RegisterInto</c> on a container it
/// owns; without the lifestyle the SimpleInjector hosting integration crashes
/// on first scope resolve. We did not have a test that asserted the
/// post-condition that RegisterInto leaves the container ready for hosting
/// integration — the unit suite happily passed while the integrated
/// egs-service binary crashed at boot with .NET event Id 1026.
/// </summary>
[SupportedOSPlatform("windows")]
[RequiresUnreferencedCode("Tests ProvisioningContainerBuilder which uses Assembly.GetTypes().")]
public sealed class ProvisioningContainerBuilderTests
{
    [Fact]
    public void RegisterInto_AssignsDefaultScopedLifestyleWhenAbsent()
    {
        using var container = new Container();
        container.Options.DefaultScopedLifestyle.Should().BeNull();

        ProvisioningContainerBuilder.RegisterInto(container);

        container.Options.DefaultScopedLifestyle.Should().NotBeNull();
        container.Options.DefaultScopedLifestyle.Should().BeOfType<AsyncScopedLifestyle>();
    }

    [Fact]
    public void RegisterInto_PreservesPreconfiguredScopedLifestyle()
    {
        using var container = new Container();
        var preset = new AsyncScopedLifestyle();
        container.Options.DefaultScopedLifestyle = preset;

        ProvisioningContainerBuilder.RegisterInto(container);

        container.Options.DefaultScopedLifestyle.Should().BeSameAs(preset);
    }

    [Fact]
    public void Build_AssignsDefaultScopedLifestyle()
    {
        using var container = ProvisioningContainerBuilder.Build();
        container.Options.DefaultScopedLifestyle.Should().NotBeNull();
        container.Options.DefaultScopedLifestyle.Should().BeOfType<AsyncScopedLifestyle>();
    }

    // Regression: ISemaphoreStore + IBootSessionDetector + IBootClock must be
    // resolvable via the same RegisterInto path used by egs-service. The same
    // class of bug as the original DefaultScopedLifestyle regression — a
    // production-only path going untested. Cross-wire a fake ILogger<T> so
    // SimpleInjector can construct the concrete types even though we're not
    // running egs-service's full host.
    [Fact]
    public void RegisterInto_ResolvesSemaphoreAndBootSessionServices()
    {
        using var container = new Container();
        container.Options.DefaultScopedLifestyle = new AsyncScopedLifestyle();
        ProvisioningContainerBuilder.RegisterInto(container);
        // Cross-wire ILogger<T> AFTER RegisterInto, matching the host pattern
        // where logging integration is added by the SimpleInjector host bridge.
        container.Register(typeof(ILogger<>), typeof(NullLogger<>), Lifestyle.Singleton);

        container.GetInstance<ISemaphoreStore>().Should().BeOfType<FileSemaphoreStore>();
        container.GetInstance<IBootSessionDetector>().Should().BeOfType<BootSessionDetector>();
        container.GetInstance<IBootClock>().Should().BeOfType<Win32BootClock>();
    }
}
