using System.Globalization;
using System.Net;

namespace Eryph.GuestServices.Client.ProxyTunnel;

// Parsing + validation for the two address shapes a policy uses:
//   * a target ("to"): exactly one fixed host:port egs-tool dials or relays to.
//   * a host-side listen bind: an IP:port, or a bare port meaning loopback.
//
// The single fixed target is the whole security control, so anything that could
// stand for more than one endpoint — a wildcard host, a port range, a comma list —
// is rejected here rather than silently widened.
internal static class TunnelAddress
{
    // The "to" of a rule: a single concrete host:port. The host may be a hostname
    // or an IP (resolved on whichever side dials it); it must be a single literal,
    // never a wildcard or a bind-any address.
    public static bool TryParseTarget(string? value, out string host, out int port, out string? error)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "a target endpoint is required, in the form host:port";
            return false;
        }

        if (value.Contains('*'))
        {
            error = $"'{value}' is not allowed: wildcards are forbidden, name a single host:port";
            return false;
        }

        if (value.Contains(','))
        {
            error = $"'{value}' is not allowed: lists are forbidden, name a single host:port";
            return false;
        }

        if (!TrySplitHostPort(value, out var rawHost, out var rawPort, out error))
            return false;

        if (rawHost.Length == 0)
        {
            error = $"'{value}' is missing a host; name a single host:port";
            return false;
        }

        // A bind-any address is not a dial target: it stands for "every interface",
        // not one endpoint. Reject it so a target is unambiguously a single host.
        if (IPAddress.TryParse(rawHost, out var ip)
            && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
        {
            error = $"'{value}' is not a single target: {rawHost} means any address";
            return false;
        }

        if (!TryParsePort(rawPort, out port, out error))
            return false;

        host = rawHost;
        return true;
    }

    // A host-side listen bind. A bare port ("8080") binds loopback; an explicit
    // "ip:port" honors that IP (a non-loopback bind is therefore opt-in: the
    // operator has to spell the interface out). A hostname is rejected — a bind
    // needs a literal address, not a name to resolve.
    public static bool TryParseListen(string? value, out IPEndPoint endPoint, out string? error)
    {
        endPoint = new IPEndPoint(IPAddress.Loopback, 0);

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "a listen endpoint is required (a port, or ip:port)";
            return false;
        }

        if (value.Contains('*') || value.Contains(','))
        {
            error = $"'{value}' is not allowed: a listen endpoint is a single port or ip:port";
            return false;
        }

        // Bare port => loopback bind.
        if (!value.Contains(':') && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            if (!TryParsePort(value, out var barePort, out error))
                return false;

            endPoint = new IPEndPoint(IPAddress.Loopback, barePort);
            return true;
        }

        if (!TrySplitHostPort(value, out var rawHost, out var rawPort, out error))
            return false;

        if (rawHost.Length == 0)
        {
            error = $"'{value}' is missing a bind address; use a port for loopback or ip:port";
            return false;
        }

        if (!IPAddress.TryParse(rawHost, out var ip))
        {
            error = $"'{rawHost}' is not an IP address; a listen bind needs a literal address, not a hostname";
            return false;
        }

        if (!TryParsePort(rawPort, out var port, out error))
            return false;

        endPoint = new IPEndPoint(ip, port);
        return true;
    }

    // Splits host:port, tolerating a bracketed IPv6 literal ("[::1]:8080").
    private static bool TrySplitHostPort(string value, out string host, out string port, out string? error)
    {
        host = string.Empty;
        port = string.Empty;
        error = null;

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
            {
                error = $"'{value}' has an unterminated IPv6 literal";
                return false;
            }

            host = value.Substring(1, close - 1);
            var rest = value[(close + 1)..];
            if (!rest.StartsWith(':'))
            {
                error = $"'{value}' is missing a port; use [ipv6]:port";
                return false;
            }

            port = rest[1..];
            return true;
        }

        var lastColon = value.LastIndexOf(':');
        if (lastColon < 0)
        {
            error = $"'{value}' is missing a port; use host:port";
            return false;
        }

        host = value[..lastColon];
        port = value[(lastColon + 1)..];
        return true;
    }

    private static bool TryParsePort(string value, out int port, out string? error)
    {
        port = 0;

        if (value.Contains('-'))
        {
            error = $"'{value}' is not allowed: port ranges are forbidden, name a single port";
            return false;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > 65535)
        {
            error = $"'{value}' is not a valid port (1-65535)";
            return false;
        }

        error = null;
        port = parsed;
        return true;
    }

    // Shared with the egress rule, whose listen is a JSON number rather than a
    // string: the same 1-65535 bound, reported the same way.
    public static bool TryValidatePort(int value, out string? error)
    {
        if (value is < 1 or > 65535)
        {
            error = $"{value} is not a valid port (1-65535)";
            return false;
        }

        error = null;
        return true;
    }
}
