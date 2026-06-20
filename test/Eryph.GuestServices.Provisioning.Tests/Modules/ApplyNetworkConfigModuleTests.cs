using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Tests.Reporting;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
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

    private static NetworkAdapterInfo Adapter(string alias, int ifIndex, string mac) =>
        new()
        {
            InterfaceAlias = alias,
            InterfaceIndex = ifIndex,
            MacAddress = mac,
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
        await os.DidNotReceiveWithAnyArgs().SetDnsSearchSuffixesAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetInterfaceMtuAsync(default, default, default);
        await os.DidNotReceiveWithAnyArgs().EnableDhcp6Async(default, default);
        await os.DidNotReceiveWithAnyArgs().DisableDhcp6Async(default, default);
        await os.DidNotReceiveWithAnyArgs().SetStaticIpv6AddressesAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetIpv6DefaultGatewayAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetInterfaceRoutesAsync(default, default!, default);
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
                [Adapter("Ethernet", 12, "d2:ab:04:5a:29:47")],
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
                [Adapter("Ethernet", 7, "00:11:22:33:44:55")],
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
                [Adapter("Ethernet", 9, "00:11:22:33:44:55")],
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
    public async Task Match_macaddress_block_binds_adapter_and_applies_static()
    {
        // Regression for issue #59: the network-config binds the NIC via a v2
        // `match: {macaddress}` block (the standard netplan selector), not the
        // top-level `macaddress`. The applier must resolve the adapter from
        // match.macaddress and apply the static IPv4 config + DNS.
        var yaml = LoadFixtureText("issue-59-v2-match.yaml");
        var network = NetworkConfigYamlSerializer.Deserialize(yaml);
        network.Version.Should().Be(2);
        network.Ethernets.Should().ContainKey("eth0");

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 8, "02:00:00:ad:e2:71")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).DisableDhcpAsync(8, Arg.Any<CancellationToken>());
        await os.Received(1).SetStaticIpv4AddressesAsync(
            8,
            Arg.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "192.168.8.210/24"),
            Arg.Any<CancellationToken>());
        await os.Received(1).SetIpv4DefaultGatewayAsync(8, "192.168.8.1", Arg.Any<CancellationToken>());
        await os.Received(1).SetDnsServersAsync(
            8,
            Arg.Is<IReadOnlyList<string>>(d => d.Count == 1 && d[0] == "192.168.8.1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Match_macaddress_takes_precedence_over_top_level_for_binding()
    {
        // When both a match.macaddress (the selector) and a top-level
        // macaddress (the MAC to set/spoof) are present, the adapter is
        // selected by match — that is what netplan/cloud-init does.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    Match = new NetworkMatch { MacAddress = "02:00:00:ad:e2:71" },
                    MacAddress = "aa:bb:cc:dd:ee:ff",
                    Dhcp4 = true,
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 4, "02:00:00:ad:e2:71")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).EnableDhcpAsync(4, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Match_with_only_name_and_no_mac_is_skipped()
    {
        // Windows binds by MAC only. A match block that carries only a name /
        // driver gives us no MAC to resolve, so the entry is skipped (no
        // writes, no failure) — same semantics as a missing macaddress.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    Match = new NetworkMatch { Name = "en*" },
                    Addresses = ["10.0.0.5/24"],
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 1, "00:11:22:33:44:55")],
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
                [Adapter("Ethernet", 1, "00:11:22:33:44:55")],
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
                [Adapter("Ethernet", 1, "00:11:22:33:44:55")],
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
                [Adapter("Ethernet", 7, "00:11:22:33:44:55")],
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
    public async Task IPv6_StaticAddress_AppliesWithGateway()
    {
        // v6 static configuration: matched by MAC, single address + gateway6.
        // The applier must disable DHCPv6, set the address, then set the
        // default gateway. v4 must NOT be touched (no Dhcp4, no v4 addresses).
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    MacAddress = "00:11:22:33:44:55",
                    Addresses = ["2001:db8::1/64"],
                    Gateway6 = "2001:db8::254",
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 11, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();

        await os.Received(1).DisableDhcp6Async(11, Arg.Any<CancellationToken>());
        await os.Received(1).SetStaticIpv6AddressesAsync(
            11,
            Arg.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "2001:db8::1/64"),
            Arg.Any<CancellationToken>());
        await os.Received(1).SetIpv6DefaultGatewayAsync(11, "2001:db8::254", Arg.Any<CancellationToken>());

        // v4 path must stay untouched.
        await os.DidNotReceiveWithAnyArgs().EnableDhcpAsync(default, default);
        await os.DidNotReceiveWithAnyArgs().DisableDhcpAsync(default, default);
        await os.DidNotReceiveWithAnyArgs().SetStaticIpv4AddressesAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetIpv4DefaultGatewayAsync(default, default!, default);
    }

    [Fact]
    public async Task MixedV4AndV6_BothFamiliesApplied()
    {
        // A single ethernet entry carrying one v4 and one v6 address (plus
        // both gateways) must drive both families through the OS surface.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    MacAddress = "00:11:22:33:44:55",
                    Addresses = ["10.0.0.1/24", "2001:db8::1/64"],
                    Gateway4 = "10.0.0.254",
                    Gateway6 = "2001:db8::254",
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 13, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();

        // IPv4 path
        await os.Received(1).DisableDhcpAsync(13, Arg.Any<CancellationToken>());
        await os.Received(1).SetStaticIpv4AddressesAsync(
            13,
            Arg.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "10.0.0.1/24"),
            Arg.Any<CancellationToken>());
        await os.Received(1).SetIpv4DefaultGatewayAsync(13, "10.0.0.254", Arg.Any<CancellationToken>());

        // IPv6 path
        await os.Received(1).DisableDhcp6Async(13, Arg.Any<CancellationToken>());
        await os.Received(1).SetStaticIpv6AddressesAsync(
            13,
            Arg.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "2001:db8::1/64"),
            Arg.Any<CancellationToken>());
        await os.Received(1).SetIpv6DefaultGatewayAsync(13, "2001:db8::254", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dhcp6_NoAddresses_OnlyEnableDhcp6()
    {
        // dhcp6: true with no addresses → re-enable DHCPv6, do NOT issue any
        // static-v6 calls.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    MacAddress = "00:11:22:33:44:55",
                    Dhcp6 = true,
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 15, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();

        await os.Received(1).EnableDhcp6Async(15, Arg.Any<CancellationToken>());
        await os.DidNotReceiveWithAnyArgs().DisableDhcp6Async(default, default);
        await os.DidNotReceiveWithAnyArgs().SetStaticIpv6AddressesAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetIpv6DefaultGatewayAsync(default, default!, default);
    }

    [Fact]
    public async Task Routes_AppliedPerFamily()
    {
        // The OS-side helper infers family per route; the module just hands it
        // the full list. We assert SetInterfaceRoutesAsync is invoked once
        // with the original list (order and content preserved).
        var routes = new[]
        {
            new NetworkRoute { To = "192.168.10.0/24", Via = "10.0.0.254", Metric = 100 },
            new NetworkRoute { To = "2001:db8:1::/48", Via = "2001:db8::254" },
        };
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    MacAddress = "00:11:22:33:44:55",
                    Dhcp4 = true,
                    Routes = routes,
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 17, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();

        await os.Received(1).SetInterfaceRoutesAsync(
            17,
            Arg.Is<IReadOnlyList<NetworkRoute>>(r =>
                r.Count == 2
                && r[0].To == "192.168.10.0/24" && r[0].Via == "10.0.0.254" && r[0].Metric == 100
                && r[1].To == "2001:db8:1::/48" && r[1].Via == "2001:db8::254"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_AppliedToConnectionAndGlobalList()
    {
        // nameservers.search drives a single OS call that the implementation
        // splits across MSFT_DNSClient (first entry) and the global suffix list.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    MacAddress = "00:11:22:33:44:55",
                    Dhcp4 = true,
                    Nameservers = new NetworkNameservers
                    {
                        Search = ["a.com", "b.com"],
                    },
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 19, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();

        await os.Received(1).SetDnsSearchSuffixesAsync(
            19,
            Arg.Is<IReadOnlyList<string>>(s => s.Count == 2 && s[0] == "a.com" && s[1] == "b.com"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MatchedMacWithOnlySearch_NoIpModification()
    {
        // An ethernet entry that only carries a search list should drive ONLY
        // the search call. Neither v4 nor v6 paths may be touched.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    MacAddress = "00:11:22:33:44:55",
                    Nameservers = new NetworkNameservers
                    {
                        Search = ["search.local"],
                    },
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 21, "00:11:22:33:44:55")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();

        await os.Received(1).SetDnsSearchSuffixesAsync(
            21,
            Arg.Is<IReadOnlyList<string>>(s => s.Count == 1 && s[0] == "search.local"),
            Arg.Any<CancellationToken>());

        // No v4 modifications.
        await os.DidNotReceiveWithAnyArgs().EnableDhcpAsync(default, default);
        await os.DidNotReceiveWithAnyArgs().DisableDhcpAsync(default, default);
        await os.DidNotReceiveWithAnyArgs().SetStaticIpv4AddressesAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetIpv4DefaultGatewayAsync(default, default!, default);
        // No v6 modifications.
        await os.DidNotReceiveWithAnyArgs().EnableDhcp6Async(default, default);
        await os.DidNotReceiveWithAnyArgs().DisableDhcp6Async(default, default);
        await os.DidNotReceiveWithAnyArgs().SetStaticIpv6AddressesAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetIpv6DefaultGatewayAsync(default, default!, default);
        // No routes / DNS-server calls either.
        await os.DidNotReceiveWithAnyArgs().SetInterfaceRoutesAsync(default, default!, default);
        await os.DidNotReceiveWithAnyArgs().SetDnsServersAsync(default, default!, default);
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
                [Adapter("Ethernet", 5, "aa:bb:cc:dd:ee:ff")],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).EnableDhcpAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Duplicate_MAC_does_not_throw_and_lowest_index_wins()
    {
        // Two adapters can legitimately report the same MAC (a NIC team and a
        // member, a bridged pair). The matcher must not throw on the duplicate
        // and must pick deterministically: the lowest interface index.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    MacAddress = "00:11:22:33:44:55",
                    Dhcp4 = true,
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [
                    Adapter("Ethernet 2", 23, "00:11:22:33:44:55"),
                    Adapter("Ethernet", 12, "00:11:22:33:44:55"),
                ],
            }[0]);

        var module = new ApplyNetworkConfigModule(NullLogger<ApplyNetworkConfigModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).EnableDhcpAsync(12, Arg.Any<CancellationToken>());
        await os.DidNotReceive().EnableDhcpAsync(23, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unapplied_device_types_are_warned_not_silently_dropped()
    {
        // bonds / bridges / VLANs are parsed but never applied on Windows. The
        // operator must get a warning per group rather than a silent no-op.
        var network = new NetworkConfig
        {
            Version = 2,
            Bonds = new Dictionary<string, NetworkBondConfig>
            {
                ["bond0"] = new() { Interfaces = ["eth1", "eth2"] },
            },
            Bridges = new Dictionary<string, NetworkBridgeConfig>
            {
                ["br0"] = new() { Interfaces = ["eth3"] },
            },
            Vlans = new Dictionary<string, NetworkVlanConfig>
            {
                ["vlan100"] = new() { Id = 100, Link = "eth0" },
            },
            // deliberately no ethernets
        };

        var os = Substitute.For<IWindowsOs>();
        var logger = new CapturingLogger<ApplyNetworkConfigModule>();
        var module = new ApplyNetworkConfigModule(logger);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("bond"));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("bridge"));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("VLAN"));
        // Nothing applicable: we never even enumerate adapters.
        await os.DidNotReceiveWithAnyArgs().GetNetworkAdaptersAsync(default);
    }

    [Fact]
    public async Task Unsupported_ethernet_options_are_warned_but_ip_is_still_applied()
    {
        // An ethernet carrying options we don't honour (dhcp4-overrides,
        // routing-policy) must still get its DHCP/IP config applied, AND a
        // warning that lists the ignored options.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    MacAddress = "00:11:22:33:44:55",
                    Dhcp4 = true,
                    UnsupportedOptions = ["dhcp4-overrides", "routing-policy"],
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 6, "00:11:22:33:44:55")],
            }[0]);

        var logger = new CapturingLogger<ApplyNetworkConfigModule>();
        var module = new ApplyNetworkConfigModule(logger);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).EnableDhcpAsync(6, Arg.Any<CancellationToken>());
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("dhcp4-overrides")
            && e.Message.Contains("routing-policy"));
    }

    [Fact]
    public async Task Setting_a_new_adapter_mac_is_warned()
    {
        // A top-level macaddress that differs from the match selector is a
        // request to SET the MAC, which Windows side does not do. Warn.
        var network = new NetworkConfig
        {
            Version = 2,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    Match = new NetworkMatch { MacAddress = "00:11:22:33:44:55" },
                    MacAddress = "aa:bb:cc:dd:ee:ff",
                    Dhcp4 = true,
                },
            },
        };

        var os = Substitute.For<IWindowsOs>();
        os.GetNetworkAdaptersAsync(Arg.Any<CancellationToken>())
            .Returns(new IReadOnlyList<NetworkAdapterInfo>[]
            {
                [Adapter("Ethernet", 6, "00:11:22:33:44:55")],
            }[0]);

        var logger = new CapturingLogger<ApplyNetworkConfigModule>();
        var module = new ApplyNetworkConfigModule(logger);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            BuildContext(os, network),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).EnableDhcpAsync(6, Arg.Any<CancellationToken>());
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("MAC"));
    }
}
