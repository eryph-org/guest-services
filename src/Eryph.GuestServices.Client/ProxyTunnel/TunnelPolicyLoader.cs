using System.Net;
using System.Text.Json;

namespace Eryph.GuestServices.Client.ProxyTunnel;

// Thrown when a policy file cannot be read, parsed, or validated. The message is
// user-facing and, for validation failures, lists every problem found so the
// operator fixes the file in one pass rather than one error at a time.
public sealed class TunnelPolicyException(string message) : Exception(message);

// Loads and validates a proxied-tunnel policy file. Validation is default-deny and
// exhaustive: a malformed file, an unknown key, a wildcard/range/list/multi-target
// endpoint, a duplicate rule name, or a missing required section each produces an
// error, and the policy is rejected unless every rule resolves to exactly one
// fixed target with positive keepalive values.
public static class TunnelPolicyLoader
{
    public static TunnelPolicy LoadFromFile(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new TunnelPolicyException($"The policy file '{path}' could not be read: {ex.Message}");
        }

        if (!TryParse(json, out var policy, out var errors))
            throw new TunnelPolicyException(
                $"The policy file '{path}' is not valid:{Environment.NewLine}"
                + string.Join(Environment.NewLine, errors.Select(e => $"  - {e}")));

        return policy!;
    }

    public static bool TryParse(string json, out TunnelPolicy? policy, out IReadOnlyList<string> errors)
    {
        policy = null;

        TunnelPolicyDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, TunnelPolicyJsonContext.Default.TunnelPolicyDto);
        }
        catch (JsonException ex)
        {
            // Covers both syntax errors and (via UnmappedMemberHandling.Disallow)
            // unknown keys, which STJ reports as a JsonException naming the member.
            errors = [$"the file is not valid JSON: {ex.Message}"];
            return false;
        }

        if (dto is null)
        {
            errors = ["the file is empty"];
            return false;
        }

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(dto.Tunnel))
            problems.Add("'tunnel' is required (a logical name for the tunnel)");
        else if (dto.Tunnel.Any(char.IsControl))
            problems.Add("'tunnel' must not contain control characters");

        if (string.IsNullOrWhiteSpace(dto.Catlet))
            problems.Add("'catlet' is required (the catlet id or name to bind to)");
        else if (!IsSafeCatletToken(dto.Catlet.Trim()))
            problems.Add("'catlet' may only contain letters, digits, '.', '-' and '_' and needs at least one letter or digit");

        var keepalive = ValidateKeepalive(dto.Keepalive, problems);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var egress = ValidateEgress(dto.Egress, names, problems);
        var ingress = ValidateIngress(dto.Ingress, names, problems);

        // Key this off what the file *declared*, not what survived validation: a
        // policy whose only rule has a bad target already reports that target error,
        // and adding "no rules" on top would just be misleading noise.
        var declaredAnyRule = dto.Egress is { Count: > 0 } || dto.Ingress is { Count: > 0 };
        if (!declaredAnyRule)
            problems.Add("at least one 'egress' or 'ingress' rule is required");

        if (problems.Count > 0)
        {
            errors = problems;
            return false;
        }

        policy = new TunnelPolicy(dto.Tunnel!.Trim(), dto.Catlet!.Trim(), egress, ingress, keepalive!);
        errors = [];
        return true;
    }

    private static KeepaliveSettings? ValidateKeepalive(KeepaliveDto? dto, List<string> problems)
    {
        if (dto is null)
        {
            problems.Add("'keepalive' is required (idleSeconds, intervalSeconds, retries)");
            return null;
        }

        var ok = true;
        ok &= RequirePositive(dto.IdleSeconds, "keepalive.idleSeconds", problems);
        ok &= RequirePositive(dto.IntervalSeconds, "keepalive.intervalSeconds", problems);
        ok &= RequirePositive(dto.Retries, "keepalive.retries", problems);

        return ok ? new KeepaliveSettings(dto.IdleSeconds!.Value, dto.IntervalSeconds!.Value, dto.Retries!.Value) : null;
    }

    private static List<EgressRule> ValidateEgress(
        List<EgressRuleDto?>? rules, HashSet<string> names, List<string> problems)
    {
        var result = new List<EgressRule>();
        if (rules is null)
            return result;

        var binds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rules.Count; i++)
        {
            var dto = rules[i];
            if (dto is null)
            {
                problems.Add($"egress[{i}] is null");
                continue;
            }

            var label = RuleLabel("egress", i, dto.Name);
            var name = ValidateName(dto.Name, label, names, problems);

            // listenAddress is optional and defaults to 127.0.0.1; when present it
            // must be a loopback literal.
            var listenAddress = IPAddress.Loopback;
            var addressOk = true;
            if (dto.ListenAddress is not null)
            {
                addressOk = TunnelAddress.TryParseLoopbackAddress(dto.ListenAddress, out listenAddress, out var addrError);
                if (!addressOk)
                    problems.Add($"{label}: listenAddress {addrError}");
            }

            var portOk = false;
            if (dto.Listen is null)
                problems.Add($"{label}: 'listen' is required (the guest-side loopback port)");
            else if (!TunnelAddress.TryValidatePort(dto.Listen.Value, out var portError))
                problems.Add($"{label}: listen {portError}");
            else
                portOk = true;

            // A rule is uniquely identified on the guest by bind address + port.
            if (addressOk && portOk && !binds.Add($"{listenAddress}:{dto.Listen!.Value}"))
            {
                problems.Add($"{label}: egress bind {listenAddress}:{dto.Listen.Value} is used by more than one rule");
                portOk = false;
            }

            var targetOk = TunnelAddress.TryParseTarget(dto.To, out var toHost, out var toPort, out var toError);
            if (!targetOk)
                problems.Add($"{label}: to {toError}");

            if (name is not null && addressOk && portOk && targetOk)
                result.Add(new EgressRule(name, listenAddress, dto.Listen!.Value, toHost, toPort));
        }

        return result;
    }

    private static List<IngressRule> ValidateIngress(
        List<IngressRuleDto?>? rules, HashSet<string> names, List<string> problems)
    {
        var result = new List<IngressRule>();
        if (rules is null)
            return result;

        var binds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rules.Count; i++)
        {
            var dto = rules[i];
            if (dto is null)
            {
                problems.Add($"ingress[{i}] is null");
                continue;
            }

            var label = RuleLabel("ingress", i, dto.Name);
            var name = ValidateName(dto.Name, label, names, problems);

            var listenOk = TunnelAddress.TryParseListen(dto.Listen, out var listen, out var listenError);
            if (!listenOk)
                problems.Add($"{label}: listen {listenError}");
            else if (!binds.Add(listen.ToString()))
            {
                problems.Add($"{label}: ingress listen bind {listen} is used by more than one rule");
                listenOk = false;
            }

            var targetOk = TunnelAddress.TryParseTarget(dto.To, out var toHost, out var toPort, out var toError);
            if (!targetOk)
                problems.Add($"{label}: to {toError}");

            if (name is not null && listenOk && targetOk)
                result.Add(new IngressRule(name, listen, toHost, toPort));
        }

        return result;
    }

    private static string? ValidateName(
        string? name, string label, HashSet<string> names, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add($"{label}: 'name' is required");
            return null;
        }

        var trimmed = name.Trim();
        if (!names.Add(trimmed))
        {
            problems.Add($"{label}: the rule name '{trimmed}' is used more than once");
            return null;
        }

        return trimmed;
    }

    private static bool RequirePositive(int? value, string field, List<string> problems)
    {
        if (value is null)
        {
            problems.Add($"'{field}' is required");
            return false;
        }

        if (value.Value <= 0)
        {
            problems.Add($"'{field}' must be greater than zero");
            return false;
        }

        return true;
    }

    private static string RuleLabel(string section, int index, string? name) =>
        string.IsNullOrWhiteSpace(name) ? $"{section}[{index}]" : $"{section} '{name.Trim()}'";

    private static bool IsSafeCatletToken(string value) =>
        value.Any(char.IsAsciiLetterOrDigit)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
