using System.Globalization;
using System.Text;
using System.Text.Json;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Stages;

namespace Eryph.GuestServices.Provisioning.Reporting.Handlers;

/// <summary>
/// One normalized cloud-init reporting event: a <c>start</c> or <c>finish</c>
/// with an optional result. <see cref="Description"/> becomes the cloud-init
/// <c>msg</c> field.
/// </summary>
internal readonly record struct CloudInitEvent(
    string Name,
    string Type,
    string? Result,
    string Description,
    DateTimeOffset Timestamp);

/// <summary>
/// Encodes guest-services <see cref="ReportingEvent"/>s into cloud-init's
/// Hyper-V KVP wire format, matching
/// <c>cloudinit/reporting/handlers.py:HyperVKvpReportingHandler._event_key</c>.
///
/// Key:   <c>CLOUD_INIT|&lt;incarnation&gt;|&lt;type&gt;|&lt;name&gt;|&lt;uuid&gt;</c>
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
        CloudInitEvent cloudInitEvent, long incarnation, Guid eventId)
    {
        // cloud-init _event_key: CLOUD_INIT|<incarnation>|<type>|<name>|<uuid>.
        var baseKey = string.Join(
            '|',
            KeyPrefix,
            incarnation.ToString(CultureInfo.InvariantCulture),
            cloudInitEvent.Type,
            cloudInitEvent.Name,
            eventId.ToString("D"));

        var whole = Serialize(cloudInitEvent, cloudInitEvent.Description ?? "", descIndex: null);
        if (Encoding.UTF8.GetByteCount(whole) <= ChunkValueSize)
            return [new KeyValuePair<string, string>(baseKey, whole)];

        return BreakDown(baseKey, cloudInitEvent);
    }

    // Same shape as cloudinit/reporting/handlers.py:_break_down — a value over
    // the limit is split across `<base>|<index>` subkeys, each a full event JSON
    // with `msg_i:<index>` and a slice of the description; a reader concatenates
    // the `msg` slices ordered by `msg_i`. Unlike cloud-init, which slices the
    // ALREADY-escaped string and can emit invalid JSON when a cut lands inside a
    // `\` or `\uXXXX` escape, we slice the RAW description and let the JSON
    // writer escape each slice — so every chunk is valid JSON and the slices
    // still reassemble to the full description.
    private static IReadOnlyList<KeyValuePair<string, string>> BreakDown(
        string baseKey, CloudInitEvent cloudInitEvent)
    {
        var description = cloudInitEvent.Description ?? "";
        var entries = new List<KeyValuePair<string, string>>();
        var position = 0;
        var index = 0;

        while (position < description.Length)
        {
            var take = LargestChunkThatFits(cloudInitEvent, description, position, index);
            var chunk = description.Substring(position, take);
            entries.Add(new KeyValuePair<string, string>(
                $"{baseKey}|{index}", Serialize(cloudInitEvent, chunk, index)));
            position += take;
            index++;
        }

        // An oversized envelope with no description still needs one indexed entry.
        if (entries.Count == 0)
            entries.Add(new KeyValuePair<string, string>(
                $"{baseKey}|0", Serialize(cloudInitEvent, "", 0)));

        return entries;
    }

    // Largest run of raw chars from description[position..] whose serialized
    // value stays within the chunk byte limit (at least one char, never ending
    // inside a UTF-16 surrogate pair). Binary search over the serialized length.
    private static int LargestChunkThatFits(
        CloudInitEvent cloudInitEvent, string description, int position, int index)
    {
        var remaining = description.Length - position;
        int low = 1, high = remaining, best = 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var bytes = Encoding.UTF8.GetByteCount(
                Serialize(cloudInitEvent, description.Substring(position, mid), index));
            if (bytes <= ChunkValueSize)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        // Don't end a chunk on a high surrogate — that would split a code point.
        if (best > 1 && best < remaining && char.IsHighSurrogate(description[position + best - 1]))
            best--;

        return best;
    }

    private static string ChildName(string? stage, string module) =>
        string.IsNullOrEmpty(stage) ? module : $"{stage}/{module}";

    // Hand-written with Utf8JsonWriter (compact, trim-safe) rather than the
    // reflection-based serializer. Field order mirrors cloud-init's value JSON:
    // name, type, ts, [result], [msg_i], msg.
    private static string Serialize(CloudInitEvent cloudInitEvent, string msg, int? descIndex)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", cloudInitEvent.Name);
            writer.WriteString("type", cloudInitEvent.Type);
            writer.WriteString("ts", FormatTimestamp(cloudInitEvent.Timestamp));
            if (cloudInitEvent.Result is not null)
                writer.WriteString("result", cloudInitEvent.Result);
            if (descIndex is { } idx)
                writer.WriteNumber(DescIndexKey, idx);
            writer.WriteString(MsgKey, msg);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Match cloud-init's value JSON: datetime.fromtimestamp(ts, utc).isoformat().
    // Python's isoformat() uses a `+00:00` offset and emits 6 microsecond digits
    // when non-zero, but omits the fractional part entirely when microseconds
    // are 0 (it does NOT trim trailing zeros within the 6 digits). The round-trip
    // "O" specifier would instead give a `Z` suffix with 7 fixed digits.
    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return utc.ToString("ffffff", CultureInfo.InvariantCulture) == "000000"
            ? utc.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffK", CultureInfo.InvariantCulture);
    }
}
