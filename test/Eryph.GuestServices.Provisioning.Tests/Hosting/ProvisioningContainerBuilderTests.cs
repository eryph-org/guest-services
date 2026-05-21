using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Hosting;
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
}
