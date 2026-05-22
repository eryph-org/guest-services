using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class ApplyNetworkConfigModuleTests
{
    private static string LoadFixtureText(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "network-config", name);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Network-config fixture '{name}' was not copied to the test output directory.",
                path);
        return File.ReadAllText(path);
    }

    private static TestModuleContext BuildContext(IWindowsOs os, NetworkConfig? network)
    {
        var dataSource = new DataSourceResult
        {
            SourceName = "test",
            InstanceId = "test-instance",
            StructuredNetworkConfig = network,
        };
        return new TestModuleContext(os, dataSource);
    }

    private static NetworkAdapterInfo Physical(string alias, int ifIndex, string mac) =>
        new()
        {
            InterfaceAlias = alias,
            InterfaceIndex = ifIndex,
            MacAddress = mac,
            IsPhysical = true,
        };

    [Fact]
    public async Task Empty_network_config_makes_no_OS_writes()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network: null),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceiveWithAnyArgs().GetNetworkAdaptersAsync(default);
        await os.DidNotReceiveWithAnyArgs().DisableDhcpAsync(default, default);
        await os.DidNotReceiveWithAnyArgs().SetStaticIpv4AddressesAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetIpv4DefaultGatewayAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetDnsServersAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetInterfaceMtuAsync(default, default, default);
    }

    [Fact]
    public async Task Real_world_eryph_v1_dhcp_fixture_only_ensures_DHCP_on_for_matched_adapter()
    {
        // This is the exact shape eryph-zero emits today: v1 + single physical
        // adapter, MAC-matched, subnets=[type: dhcp]. The applier MUST NOT
        // touch static configuration on a pure-DHCP entry; the only positive
        // action is to (re)enable DHCP so a previous static run is cleared.
        var yaml = LoadFixtureText("eryph-v1-dhcp.yaml");
        var network = NetworkConfigYamlSerializer.Deserialize(yaml);

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Physical("Ethernet", 12, "d2:ab:04:5a:29:47")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();

        await os.Received(1).EnableDhcpAsync(12, Arg.Any<CancellationToken>());
        await os.DidNotReceiveWithAnyArgs().DisableDhcpAsync(default, default);
        await os.DidNotReceiveWithAnyArgs().SetStaticIpv4AddressesAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetIpv4DefaultGatewayAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetDnsServersAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetInterfaceMtuAsync(default, default, default);
    }

    [Fact]
    public async Task Static_v1_fixture_applies_address_gateway_dns_and_mtu_in_order()
    {
        // The order matters: DisableDhcp -> SetStaticIp -> SetGateway. DNS and
        // MTU can be applied at any point after, but the test pins the call
        // sequence so a future refactor that, say, sets the gateway before
        // the address is caught.
        var yaml = LoadFixtureText("synthetic-v1-static.yaml");
        var network = NetworkConfigYamlSerializer.Deserialize(yaml);

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Physical("Ethernet", 7, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();

        Received.InOrder(() =>
        {
            os.DisableDhcpAsync(7, Arg.Any<CancellationToken>());
            os.SetStaticIpv4AddressesAsync(
                7,
                Arg.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "10.0.0.5/24"),
                Arg.Any<CancellationToken>());
            os.SetIpv4DefaultGatewayAsync(7, "10.0.0.1", Arg.Any<CancellationToken>());
        });

        await os.Received().SetDnsServersAsync(
            7,
            Arg.Is<IReadOnlyList<string>>(d => d.Count == 2 && d[0] == "1.1.1.1" && d[1] == "8.8.8.8"),
            Arg.Any<CancellationToken>());
        await os.Received().SetInterfaceMtuAsync(7, 1400, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Static_v2_fixture_applies_same_writes_as_v1_equivalent()
    {
        // The MAC, addresses, gateway, DNS and MTU all match the v1 static
        // fixture above; the only change is the schema. The applier must
        // produce the same set of OS writes regardless of the input schema.
        var yaml = LoadFixtureText("synthetic-v2-static.yaml");
        var network = NetworkConfigYamlSerializer.Deserialize(yaml);
        network.Version.Should().Be(2);

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Physical("Ethernet", 9, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).DisableDhcpAsync(9, Arg.Any<CancellationToken>());
        await os.Received(1).SetStaticIpv4AddressesAsync(
            9,
            Arg.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "10.0.0.5/24"),
            Arg.Any<CancellationToken>());
        await os.Received(1).SetIpv4DefaultGatewayAsync(9, "10.0.0.1", Arg.Any<CancellationToken>());
        await os.Received(1).SetDnsServersAsync(
            9,
            Arg.Is<IReadOnlyList<string>>(d => d.Count == 2 && d[0] == "1.1.1.1" && d[1] == "8.8.8.8"),
            Arg.Any<CancellationToken>());
        await os.Received(1).SetInterfaceMtuAsync(9, 1400, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adapter_unmatched_by_mac_is_skipped_without_writes()
    {
        // The cloud-init entry references a MAC that doesn't exist on the
        // host. We mirror cloud-init's "skip unmatched" semantics — no writes,
        // no failure.
        var network = NetworkConfigYamlSerializer.Deserialize("""
            version: 2
            ethernets:
              eth0:
                macaddress: "aa:bb:cc:dd:ee:ff"
                addresses:
                  - 10.0.0.5/24
            """);

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Physical("Ethernet", 1, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceiveWithAnyArgs().DisableDhcpAsync(default, default);
        await os.DidNotReceiveWithAnyArgs().SetStaticIpv4AddressesAsync(default, default!, default);
    }

    [Fact]
    public async Task Entries_without_macaddress_are_skipped_with_warning_log()
    {
        // Windows applier requires MAC matching — entries with no MAC have no
        // way to bind to a specific adapter.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    Addresses = ["10.0.0.5/24"],
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Physical("Ethernet", 1, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceiveWithAnyArgs().DisableDhcpAsync(default, default);
        await os.DidNotReceiveWithAnyArgs().SetStaticIpv4AddressesAsync(default, default!, default);
    }

    [Fact]
    public async Task Re_running_with_same_static_input_makes_same_calls_each_time()
    {
        // Idempotency at the module surface: applying the same fixture twice
        // must produce the same sequence of calls each time. Idempotency at
        // the CIM layer (skip if equal) is exercised separately at the
        // CimNetworking layer, which the module relies on.
        var yaml = LoadFixtureText("synthetic-v1-static.yaml");
        var network = NetworkConfigYamlSerializer.Deserialize(yaml);

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Physical("Ethernet", 7, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);
        var context = BuildContext(os, network);

        var first = await module.ApplyAsync(ResolvedUserData.Empty(new CloudConfigModel()), context, CancellationToken.None);
        var second = await module.ApplyAsync(ResolvedUserData.Empty(new CloudConfigModel()), context, CancellationToken.None);

        first.Should().BeOfType<ModuleOutcome.Completed>();
        second.Should().BeOfType<ModuleOutcome.Completed>();

        await os.Received(2).SetStaticIpv4AddressesAsync(
            7,
            Arg.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "10.0.0.5/24"),
            Arg.Any<CancellationToken>());
        await os.Received(2).SetIpv4DefaultGatewayAsync(7, "10.0.0.1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MAC_match_is_case_insensitive_and_separator_insensitive()
    {
        // The cloud-init MAC uses lowercase colons; the adapter MAC could be
        // reported in any normalisation. Our applier canonicalises both sides
        // before comparing so "AA-BB-CC-DD-EE-FF" matches "aa:bb:cc:dd:ee:ff".
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    MacAddress = "AA-BB-CC-DD-EE-FF",
                    Dhcp4 = true,
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Physical("Ethernet", 5, "aa:bb:cc:dd:ee:ff")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).EnableDhcpAsync(5, Arg.Any<CancellationToken>());
    }
}
