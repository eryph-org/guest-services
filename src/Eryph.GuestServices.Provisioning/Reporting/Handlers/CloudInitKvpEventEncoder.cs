using System.Globalization;
using System.Text;
using System.Text.Json;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Stages;

namespace Eryph.GuestServices.Provisioning.Reporting.Handlers;

/// <summary>
/// One normalized cloud-init reporting event: a <c>start</c> or <c>finish</c>
/// with an optional result. <see cref="Description"/> becomes the cloud-init
/// <c>msg</c> field. <see cref="Duration"/> (seconds) is the cloud-init
/// <c>duration</c> field on finish events — paired from the matching start by
/// the handler, so it is null on start events and when no start was seen.
/// </summary>
internal readonly record struct CloudInitEvent(
    string Name,
    string Type,
    string? Result,
    string Description,
    DateTimeOffset Timestamp,
    double? Duration = null);

/// <summary>
/// Encodes guest-services <see cref="ReportingEvent"/>s into cloud-init's
/// Hyper-V KVP wire format, matching
/// <c>cloudinit/reporting/handlers.py:HyperVKvpReportingHandler</c>.
///
/// Key:   <c>CLOUD_INIT|&lt;incarnation&gt;|&lt;type&gt;|&lt;name&gt;|&lt;vm_id&gt;|&lt;uuid&gt;</c>
/// Value: compact JSON <c>{"name","type","ts","result","msg"}</c>
/// </summary>
internal static class CloudInitKvpEventEncoder
{
    public const string KeyPrefix = "CLOUD_INIT";
    public const string StartType = "start";
    public const string FinishType = "finish";
    public const string ResultSuccess = "SUCCESS";
    public const string ResultFail = "FAIL";

    // cloud-init's HyperVKvpReportingHandler size constants. A value over the
    // (smaller, self-imposed) Azure limit is broken down across `…|<index>`
    // subkeys; see _break_down in cloudinit/reporting/handlers.py.
    private const int ChunkValueSize = 1024;     // HV_KVP_AZURE_MAX_VALUE_SIZE
    private const string MsgKey = "msg";
    private const string DescIndexKey = "msg_i"; // cloud-init DESC_IDX_KEY

    public static string MapStageName(Stage stage) => stage switch
    {
        Stage.Local => "init-local",
        Stage.Network => "init-network",
        Stage.Config => "modules-config",
        Stage.Final => "modules-final",
        _ => stage.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Maps a guest-services event to its cloud-init representation, or null when
    /// the event has no cloud-init analogue (the lifecycle bookends, reboot,
    /// ssh-host-keys and progress). <paramref name="currentStage"/>
    /// is the cloud-init name of the running stage, used to scope module child
    /// events as <c>&lt;stage&gt;/&lt;module&gt;</c>.
    /// </summary>
    public static CloudInitEvent? Map(ReportingEvent reportingEvent, string? currentStage) =>
        reportingEvent switch
        {
            ReportingEvent.StageStarted s =>
                new CloudInitEvent(MapStageName(s.Stage), StartType, null, "", s.Timestamp),
            ReportingEvent.StageFinished s =>
                new CloudInitEvent(MapStageName(s.Stage), FinishType, ResultSuccess, "", s.Timestamp),
            ReportingEvent.ModuleStarted m =>
                new CloudInitEvent(ChildName(currentStage, m.ModuleName), StartType, null, "", m.Timestamp),
            ReportingEvent.ModuleFinished m =>
                new CloudInitEvent(ChildName(currentStage, m.ModuleName), FinishType, ResultSuccess, m.Outcome, m.Timestamp),
            ReportingEvent.ModuleFailed m =>
                new CloudInitEvent(ChildName(currentStage, m.ModuleName), FinishType, ResultFail, m.Reason, m.Timestamp),
            // On failure StageFinished never fires, so emit the stage-level FAIL
            // here (falling back to init-local when we failed before any stage).
            ReportingEvent.ProvisioningFailed f =>
                new CloudInitEvent(currentStage ?? "init-local", FinishType, ResultFail, f.Reason, f.Timestamp),
            _ => null,
        };

    /// <summary>
    /// Encodes one cloud-init event into one or more KVP entries. A value that
    /// fits the per-value limit is a single entry under the base key; an
    /// oversized one is broken down across <c>&lt;base&gt;|&lt;index&gt;</c>
    /// subkeys, each carrying a <c>msg_i</c> index and a slice of the
    /// description — matching cloud-init's <c>_break_down</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Encode(
        CloudInitEvent cloudInitEvent, int incarnation, string vmId, Guid eventId)
    {
        var baseKey = string.Join(
            '|',
            KeyPrefix,
            incarnation.ToString(CultureInfo.InvariantCulture),
            cloudInitEvent.Type,
            cloudInitEvent.Name,
            vmId,
            eventId.ToString("D"));

        var whole = Serialize(cloudInitEvent, cloudInitEvent.Description ?? "", descIndex: null);

        // cloud-init compares len(value) (chars) against the Azure limit; our
        // JSON is ASCII (the encoder escapes non-ASCII), so chars == bytes.
        if (whole.Length <= ChunkValueSize)
            return [new KeyValuePair<string, string>(baseKey, whole)];

        return BreakDown(baseKey, cloudInitEvent);
    }

    // Mirror of cloudinit/reporting/handlers.py:_break_down. The description is
    // JSON-escaped once, then sliced across subkeys `<base>|<i>`; each entry is
    // a full event JSON carrying `msg_i:<i>` and its description slice. The
    // room for each slice is the Azure limit minus the rest of the JSON minus a
    // small fudge (cloud-init's `- 8`).
    private static IReadOnlyList<KeyValuePair<string, string>> BreakDown(
        string baseKey, CloudInitEvent cloudInitEvent)
    {
        // json.dumps(description)[1:-1] — the escaped body without the quotes.
        var serializedDescription = JsonSerializer.Serialize(cloudInitEvent.Description ?? "");
        var escaped = serializedDescription[1..^1];

        var entries = new List<KeyValuePair<string, string>>();
        var index = 0;
        while (true)
        {
            var withoutDescription = Serialize(cloudInitEvent, "", descIndex: index);
            var room = ChunkValueSize - withoutDescription.Length - 8;
            if (room < 1)
                room = 1;

            var take = Math.Min(room, escaped.Length);
            var slice = escaped[..take];
            var value = withoutDescription.Replace(
                $"\"{MsgKey}\":\"\"", $"\"{MsgKey}\":\"{slice}\"", StringComparison.Ordinal);

            entries.Add(new KeyValuePair<string, string>($"{baseKey}|{index}", value));

            index++;
            escaped = escaped[take..];
            if (escaped.Length == 0)
                break;
        }

        return entries;
    }

    private static string ChildName(string? stage, string module) =>
        string.IsNullOrEmpty(stage) ? module : $"{stage}/{module}";

    // Hand-written with Utf8JsonWriter (compact, trim-safe) rather than the
    // reflection-based serializer. Field order mirrors cloud-init's value JSON:
    // name, type, ts, [result], [duration], [msg_i], msg.
    private static string Serialize(CloudInitEvent cloudInitEvent, string msg, int? descIndex)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", cloudInitEvent.Name);
            writer.WriteString("type", cloudInitEvent.Type);
            // Match cloud-init's value JSON: datetime.fromtimestamp(ts, utc)
            // .isoformat() — a `+00:00` offset and 6-digit microseconds (not the
            // `Z` + 7-digit form .NET's round-trip "O" produces).
            writer.WriteString(
                "ts",
                cloudInitEvent.Timestamp.ToUniversalTime().ToString(
                    "yyyy-MM-ddTHH:mm:ss.ffffffK", CultureInfo.InvariantCulture));
            if (cloudInitEvent.Result is not null)
                writer.WriteString("result", cloudInitEvent.Result);
            if (cloudInitEvent.Duration is { } duration)
                writer.WriteNumber("duration", duration);
            if (descIndex is { } idx)
                writer.WriteNumber(DescIndexKey, idx);
            writer.WriteString(MsgKey, msg);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
