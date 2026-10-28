using System.Net;
using AwesomeAssertions;
using Eryph.GuestServices.Client.ProxyTunnel;

namespace Eryph.GuestServices.Client.Tests.ProxyTunnel;

public class TunnelPolicyLoaderTests
{
    private const string ValidPolicy = """
        {
          "tunnel": "sap-prod",
          "catlet": "my-catlet",
          "egress": [
            { "name": "sap-rfc", "listen": 3300, "to": "backend.example.com:3300" }
          ],
          "ingress": [
            { "name": "saphttp-callback", "listen": "127.0.0.1:8080", "to": "127.0.0.1:8080" }
          ],
          "keepalive": { "idleSeconds": 15, "intervalSeconds": 15, "retries": 5 }
        }
        """;

    [Fact]
    public void Parses_a_valid_policy()
    {
        TunnelPolicyLoader.TryParse(ValidPolicy, out var policy, out var errors)
            .Should().BeTrue(string.Join("; ", errors));

        policy!.Tunnel.Should().Be("sap-prod");
        policy.Catlet.Should().Be("my-catlet");

        policy.Egress.Should().ContainSingle();
        var egress = policy.Egress[0];
        egress.Name.Should().Be("sap-rfc");
        egress.ListenPort.Should().Be(3300);
        egress.ToHost.Should().Be("backend.example.com");
        egress.ToPort.Should().Be(3300);

        policy.Ingress.Should().ContainSingle();
        var ingress = policy.Ingress[0];
        ingress.Name.Should().Be("saphttp-callback");
        ingress.Listen.Should().Be(new IPEndPoint(IPAddress.Loopback, 8080));
        ingress.ToHost.Should().Be("127.0.0.1");
        ingress.ToPort.Should().Be(8080);

        policy.Keepalive.Should().Be(new KeepaliveSettings(15, 15, 5));
    }

    [Fact]
    public void Egress_listen_address_defaults_to_loopback()
    {
        TunnelPolicyLoader.TryParse(ValidPolicy, out var policy, out _).Should().BeTrue();
        policy!.Egress[0].ListenAddress.Should().Be(IPAddress.Loopback);
    }

    [Fact]
    public void Egress_honors_an_explicit_loopback_listen_address()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ { "name": "e", "listen": 3300, "listenAddress": "127.0.0.2", "to": "h:1" } ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;
        TunnelPolicyLoader.TryParse(json, out var policy, out var errors).Should().BeTrue(string.Join("; ", errors));
        policy!.Egress[0].ListenAddress.Should().Be(IPAddress.Parse("127.0.0.2"));
    }

    [Fact]
    public void Egress_rejects_a_non_loopback_listen_address()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ { "name": "e", "listen": 3300, "listenAddress": "10.0.0.5", "to": "h:1" } ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;
        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("loopback"));
    }

    [Fact]
    public void Egress_allows_same_port_on_distinct_loopback_addresses()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [
                { "name": "a", "listen": 3300, "listenAddress": "127.0.0.1", "to": "h1:1" },
                { "name": "b", "listen": 3300, "listenAddress": "127.0.0.2", "to": "h2:1" }
              ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;
        TunnelPolicyLoader.TryParse(json, out var policy, out var errors).Should().BeTrue(string.Join("; ", errors));
        policy!.Egress.Should().HaveCount(2);
    }

    [Fact]
    public void Accepts_a_policy_with_only_egress()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ { "name": "e", "listen": 3300, "to": "host:3300" } ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out var policy, out var errors)
            .Should().BeTrue(string.Join("; ", errors));
        policy!.Ingress.Should().BeEmpty();
        policy.Egress.Should().ContainSingle();
    }

    [Fact]
    public void Rejects_unknown_top_level_key()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c", "bogus": true,
              "egress": [ { "name": "e", "listen": 3300, "to": "host:3300" } ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().ContainSingle().Which.Should().Contain("JSON");
    }

    [Fact]
    public void Rejects_unknown_rule_key()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ { "name": "e", "listen": 3300, "to": "host:3300", "extra": 1 } ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("*.corp:3300", "wildcard")]
    [InlineData("host:3300,host2:3300", "list")]
    [InlineData("host:3300-3400", "range")]
    public void Rejects_non_single_target_egress(string to, string scenario)
    {
        _ = scenario;
        var json = $$"""
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ { "name": "e", "listen": 3300, "to": "{{to}}" } ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().ContainSingle();
    }

    [Fact]
    public void Rejects_missing_keepalive()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ { "name": "e", "listen": 3300, "to": "host:3300" } ]
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("keepalive"));
    }

    [Fact]
    public void Rejects_non_positive_keepalive()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ { "name": "e", "listen": 3300, "to": "host:3300" } ],
              "keepalive": { "idleSeconds": 0, "intervalSeconds": -1, "retries": 5 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("idleSeconds"))
            .And.Contain(e => e.Contains("intervalSeconds"));
    }

    [Fact]
    public void Rejects_duplicate_rule_names_across_sections()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ { "name": "dup", "listen": 3300, "to": "host:3300" } ],
              "ingress": [ { "name": "dup", "listen": "8080", "to": "127.0.0.1:80" } ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("dup") && e.Contains("more than once"));
    }

    [Fact]
    public void Rejects_empty_policy_with_no_rules()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("at least one"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    public void Rejects_malformed_json(string json)
    {
        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Rejects_null_rule_element()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ null ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("null"));
    }

    [Fact]
    public void Rejects_duplicate_egress_listen_port()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [
                { "name": "a", "listen": 3300, "to": "h1:1" },
                { "name": "b", "listen": 3300, "to": "h2:2" }
              ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("more than one rule"));
    }

    [Fact]
    public void Rejects_duplicate_ingress_bind()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "ingress": [
                { "name": "a", "listen": "127.0.0.1:8080", "to": "127.0.0.1:1" },
                { "name": "b", "listen": "8080", "to": "127.0.0.1:2" }
              ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("more than one rule"));
    }

    [Fact]
    public void Rejects_unbracketed_ipv6_target()
    {
        const string json = """
            {
              "tunnel": "t", "catlet": "c",
              "egress": [ { "name": "e", "listen": 3300, "to": "fe80::1:3300" } ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("ambiguous") || e.Contains("IPv6"));
    }

    [Theory]
    [InlineData("../evil")]  // path separator
    [InlineData(".")]        // no alphanumeric
    [InlineData("..")]       // no alphanumeric
    public void Rejects_unsafe_catlet_token(string catlet)
    {
        var json = $$"""
            {
              "tunnel": "t", "catlet": "{{catlet}}",
              "egress": [ { "name": "e", "listen": 3300, "to": "host:3300" } ],
              "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 }
            }
            """;

        TunnelPolicyLoader.TryParse(json, out _, out var errors).Should().BeFalse();
        errors.Should().Contain(e => e.Contains("catlet"));
    }
}
