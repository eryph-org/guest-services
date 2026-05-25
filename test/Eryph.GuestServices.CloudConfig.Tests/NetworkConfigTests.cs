using AwesomeAssertions;

namespace Eryph.GuestServices.CloudConfig.Tests;

public class NetworkConfigTests
{
    [Fact]
    public void Record_with_all_fields_round_trips_via_with_expression()
    {
        var original = new NetworkConfig
        {
            Version = 2,
            Renderer = NetworkDhcpRenderer.Networkd,
            Ethernets = new Dictionary<string, NetworkEthernetConfig>
            {
                ["eth0"] = new()
                {
                    Dhcp4 = true,
                    Addresses = ["10.0.0.5/24"],
                    Gateway4 = "10.0.0.1",
                    Nameservers = new NetworkNameservers
                    {
                        Addresses = ["1.1.1.1"],
                        Search = ["example.com"],
                    },
                    Routes = [new NetworkRoute { To = "default", Via = "10.0.0.1", Metric = 100 }],
                    Mtu = 1500,
                    MacAddress = "00:11:22:33:44:55",
                },
            },
            Bonds = new Dictionary<string, NetworkBondConfig>
            {
                ["bond0"] = new()
                {
                    Interfaces = ["eth1", "eth2"],
                    Parameters = new Dictionary<string, string> { ["mode"] = "active-backup" },
                },
            },
            Bridges = new Dictionary<string, NetworkBridgeConfig>
            {
                ["br0"] = new() { Interfaces = ["eth3"] },
            },
            Vlans = new Dictionary<string, NetworkVlanConfig>
            {
                ["vlan100"] = new() { Link = "eth0", Id = 100 },
            },
        };

        var copy = original with { };

        copy.Should().Be(original);
        copy.Version.Should().Be(2);
        copy.Ethernets!["eth0"].Dhcp4.Should().BeTrue();
        copy.Bonds!["bond0"].Interfaces.Should().BeEquivalentTo(["eth1", "eth2"]);
        copy.Bridges!["br0"].Interfaces.Should().BeEquivalentTo(["eth3"]);
        copy.Vlans!["vlan100"].Id.Should().Be(100);
    }

    [Fact]
    public void Empty_network_config_has_no_collections()
    {
        var empty = new NetworkConfig();

        empty.Version.Should().Be(0);
        empty.Ethernets.Should().BeNull();
        empty.Bonds.Should().BeNull();
        empty.Bridges.Should().BeNull();
        empty.Vlans.Should().BeNull();
        empty.Renderer.Should().BeNull();
    }
}
