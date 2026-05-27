using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
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

    // Regression: a partial egs-provisioning.json that pins only
    // dataSources.dataSourceList (as the OpenStack e2e does) must not break
    // container verification. The bug: the partial file deserialized with
    // UserData == null, and building the IDataSource collection during Verify()
    // constructed NoCloudDataSource -> UrlHelper, whose ctor dereferences
    // settings.UserData -> NullReferenceException. egs-service Verify()s its
    // container at startup, so this crash-looped the service on every boot and
    // the agent never came up. The unit suite never built the container with a
    // pinned datasource list, so it stayed green while the integrated binary
    // died. ProvisioningContainerBuilder.RegisterInto reads
    // ProvisioningSettings.LoadOrDefault() from AppContext.BaseDirectory, so we
    // drop the pin file there for the duration of the test.
    [Fact]
    public void Container_verifies_with_partial_pinned_datasource_list()
    {
        var pinPath = Path.Combine(AppContext.BaseDirectory, "egs-provisioning.json");
        var hadFile = File.Exists(pinPath);
        var backup = hadFile ? File.ReadAllText(pinPath) : null;
        File.WriteAllText(pinPath, """{ "dataSources": { "dataSourceList": ["OpenStack"] } }""");
        try
        {
            using var container = new Container();
            container.Options.ResolveUnregisteredConcreteTypes = true;
            ProvisioningContainerBuilder.RegisterInto(container);
            container.Register(typeof(ILogger<>), typeof(NullLogger<>), Lifestyle.Singleton);

            // Before the fix this threw SimpleInjector.ActivationException wrapping
            // a NullReferenceException from UrlHelper's ctor.
            container.Invoking(c => c.Verify()).Should().NotThrow();
            container.GetInstance<IDataSourceLocator>().Should().NotBeNull();
        }
        finally
        {
            if (backup is not null)
                File.WriteAllText(pinPath, backup);
            else
                File.Delete(pinPath);
        }
    }
}
