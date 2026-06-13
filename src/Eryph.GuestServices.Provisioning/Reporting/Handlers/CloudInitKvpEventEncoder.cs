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

    // HV_KVP_EXCHANGE_MAX_VALUE_SIZE, excluding the null terminator. The pool
    // (and DataValidator) reject larger values; we trim the msg to fit rather
    // than lose the whole event.
    private const int MaxValueSize = 2047;

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

    public static KeyValuePair<string, string> Encode(
        CloudInitEvent cloudInitEvent, int incarnation, string vmId, Guid eventId)
    {
        var key = string.Join(
            '|',
            KeyPrefix,
            incarnation.ToString(CultureInfo.InvariantCulture),
            cloudInitEvent.Type,
            cloudInitEvent.Name,
            vmId,
            eventId.ToString("D"));

        return new KeyValuePair<string, string>(key, BuildValue(cloudInitEvent));
    }

    private static string ChildName(string? stage, string module) =>
        string.IsNullOrEmpty(stage) ? module : $"{stage}/{module}";

    private static string BuildValue(CloudInitEvent cloudInitEvent)
    {
        var msg = cloudInitEvent.Description ?? "";
        var json = Serialize(cloudInitEvent, msg);

        // Trim the msg until the serialized value fits the pool limit. The base
        // fields are tiny, so this only ever bites a pathologically long error.
        while (Encoding.UTF8.GetByteCount(json) > MaxValueSize && msg.Length > 0)
        {
            msg = msg[..(msg.Length / 2)];
            json = Serialize(cloudInitEvent, msg + "…");
        }

        return json;
    }

    // Hand-written with Utf8JsonWriter (compact, trim-safe) rather than the
    // reflection-based serializer. Field names mirror cloud-init's value JSON.
    private static string Serialize(CloudInitEvent cloudInitEvent, string msg)
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
            writer.WriteString("msg", msg);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
