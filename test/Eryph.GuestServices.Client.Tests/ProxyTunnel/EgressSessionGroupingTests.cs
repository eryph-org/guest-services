using System.Net;
using AwesomeAssertions;
using Eryph.GuestServices.Client.ProxyTunnel;

namespace Eryph.GuestServices.Client.Tests.ProxyTunnel;

// DevTunnels keys reverse-forwards by port, so egress rules sharing a port must run
// in separate SSH sessions. These pin the partitioning the engine uses to place them.
public class EgressSessionGroupingTests
{
    private static EgressRule Rule(string name, string address, int port) =>
        new(name, IPAddress.Parse(address), port, "host", 1);

    [Fact]
    public void Distinct_ports_share_one_session()
    {
        var groups = ProxyTunnelEngine.GroupEgressByDistinctPorts(
        [
            Rule("a", "127.0.0.1", 3300),
            Rule("b", "127.0.0.1", 3301),
            Rule("c", "127.0.0.1", 3302),
        ]);

        groups.Should().ContainSingle();
        groups[0].Should().HaveCount(3);
    }

    [Fact]
    public void Same_port_rules_split_across_sessions()
    {
        var groups = ProxyTunnelEngine.GroupEgressByDistinctPorts(
        [
            Rule("a", "127.0.0.2", 3300),
            Rule("b", "127.0.0.3", 3300),
            Rule("c", "127.0.0.4", 3300),
        ]);

        // Three rules on the same port => three sessions, one rule each.
        groups.Should().HaveCount(3);
        groups.Should().AllSatisfy(g => g.Should().ContainSingle());
    }

    [Fact]
    public void Session_count_matches_the_highest_port_multiplicity()
    {
        // Port 3300 appears twice, 3301 once => 2 sessions; no session repeats a port.
        var groups = ProxyTunnelEngine.GroupEgressByDistinctPorts(
        [
            Rule("a", "127.0.0.2", 3300),
            Rule("b", "127.0.0.3", 3300),
            Rule("c", "127.0.0.1", 3301),
        ]);

        groups.Should().HaveCount(2);
        foreach (var group in groups)
            group.Select(r => r.ListenPort).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void No_egress_yields_no_groups()
    {
        ProxyTunnelEngine.GroupEgressByDistinctPorts([]).Should().BeEmpty();
    }
}
