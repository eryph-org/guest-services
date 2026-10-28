using System.Globalization;
using System.Net;
using System.Net.Sockets;

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

        if (HasWhitespaceOrControl(value))
        {
            error = $"'{value}' contains whitespace or control characters";
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
        // not one endpoint. Reject it (including the IPv4-mapped form ::ffff:0.0.0.0)
        // so a target is unambiguously a single host.
        if (IPAddress.TryParse(rawHost, out var ip))
        {
            var normalized = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
            if (normalized.Equals(IPAddress.Any) || normalized.Equals(IPAddress.IPv6Any))
            {
                error = $"'{value}' is not a single target: {rawHost} means any address";
                return false;
            }
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

        if (HasWhitespaceOrControl(value))
        {
            error = $"'{value}' contains whitespace or control characters";
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

        var firstColon = value.IndexOf(':');
        var lastColon = value.LastIndexOf(':');
        if (lastColon < 0)
        {
            error = $"'{value}' is missing a port; use host:port";
            return false;
        }

        // More than one colon and no brackets is an ambiguous IPv6 literal
        // (e.g. "fe80::1" would silently parse as host "fe80:" port "1"). Require
        // the bracket form so an IPv6 address can never be misread as host:port.
        if (firstColon != lastColon)
        {
            error = $"'{value}' is ambiguous; write an IPv6 address as [address]:port";
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

    // A guest-side egress bind address: an IPv4 loopback literal (127.0.0.0/8).
    // Keeping it loopback means the reverse-forward listener is never exposed on the
    // guest's network. IPv4-only on purpose: ::1 is the single IPv6 loopback and the
    // library dual-binds it with 127.0.0.1 on the same port, which would collide
    // across the per-port sessions; 127.0.0.0/8 already offers plenty of distinct
    // addresses for same-port targets. This also rejects IPv4-mapped IPv6
    // (::ffff:127.0.0.1), which is AddressFamily InterNetworkV6.
    public static bool TryParseLoopbackAddress(string? value, out IPAddress address, out string? error)
    {
        address = IPAddress.Loopback;

        if (string.IsNullOrWhiteSpace(value) || HasWhitespaceOrControl(value))
        {
            error = $"'{value}' is not a valid address";
            return false;
        }

        if (!IPAddress.TryParse(value, out var parsed))
        {
            error = $"'{value}' is not an IP address";
            return false;
        }

        if (parsed.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(parsed))
        {
            error = $"'{value}' is not an IPv4 loopback address (egress binds must be in 127.0.0.0/8)";
            return false;
        }

        address = parsed;
        error = null;
        return true;
    }

    private static bool HasWhitespaceOrControl(string value) =>
        value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

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
