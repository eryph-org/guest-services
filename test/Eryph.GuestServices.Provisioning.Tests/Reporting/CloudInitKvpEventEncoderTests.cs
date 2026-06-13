using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Reporting.Handlers;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Reporting;

public sealed class CloudInitKvpEventEncoderTests
{
    [Theory]
    [InlineData(Stage.Local, "init-local")]
    [InlineData(Stage.Network, "init-network")]
    [InlineData(Stage.Config, "modules-config")]
    [InlineData(Stage.Final, "modules-final")]
    public void MapStageName_matches_cloud_init_stage_names(Stage stage, string expected)
    {
        CloudInitKvpEventEncoder.MapStageName(stage).Should().Be(expected);
    }

    [Fact]
    public void Map_StageStarted_is_a_start_event_named_for_the_stage()
    {
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.StageStarted(Stage.Network) { Origin = "stage:Network" }, null);

        mapped.Should().NotBeNull();
        mapped!.Value.Name.Should().Be("init-network");
        mapped.Value.Type.Should().Be("start");
        mapped.Value.Result.Should().BeNull();
    }

    [Fact]
    public void Map_StageFinished_is_a_finish_success_event()
    {
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.StageFinished(Stage.Config) { Origin = "stage:Config" }, "modules-config");

        mapped!.Value.Name.Should().Be("modules-config");
        mapped.Value.Type.Should().Be("finish");
        mapped.Value.Result.Should().Be("SUCCESS");
    }

    [Fact]
    public void Map_ModuleStarted_nests_under_the_current_stage()
    {
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.ModuleStarted("SetHostname") { Origin = "module:SetHostname" }, "init-network");

        mapped!.Value.Name.Should().Be("init-network/SetHostname");
        mapped.Value.Type.Should().Be("start");
    }

    [Fact]
    public void Map_ModuleFinished_is_finish_success()
    {
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.ModuleFinished("SetHostname", "Completed") { Origin = "module:SetHostname" },
            "init-network");

        mapped!.Value.Name.Should().Be("init-network/SetHostname");
        mapped.Value.Type.Should().Be("finish");
        mapped.Value.Result.Should().Be("SUCCESS");
    }

    [Fact]
    public void Map_ModuleFailed_is_finish_fail_with_reason()
    {
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.ModuleFailed("SetHostname", "boom", null) { Origin = "module:SetHostname" },
            "init-network");

        mapped!.Value.Type.Should().Be("finish");
        mapped.Value.Result.Should().Be("FAIL");
        mapped.Value.Description.Should().Be("boom");
    }

    [Fact]
    public void Map_ProvisioningFailed_is_a_stage_level_fail()
    {
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.ProvisioningFailed("kaput", null) { Origin = "stage-runner" }, "modules-config");

        mapped!.Value.Name.Should().Be("modules-config");
        mapped.Value.Result.Should().Be("FAIL");
    }

    [Fact]
    public void Map_ProvisioningFailed_falls_back_to_init_local_before_any_stage()
    {
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.ProvisioningFailed("early", null) { Origin = "stage-runner" }, null);

        mapped!.Value.Name.Should().Be("init-local");
        mapped.Value.Result.Should().Be("FAIL");
    }

    [Theory]
    [MemberData(nameof(SkippedEvents))]
    public void Map_returns_null_for_events_without_a_cloud_init_analogue(ReportingEvent reportingEvent)
    {
        CloudInitKvpEventEncoder.Map(reportingEvent, "modules-final").Should().BeNull();
    }

    public static IEnumerable<object[]> SkippedEvents() =>
    [
        [new ReportingEvent.ProvisioningStarted("i-1") { Origin = "stage-runner" }],
        [new ReportingEvent.ProvisioningCompleted { Origin = "stage-runner" }],
        [new ReportingEvent.RebootRequested("needs-restart") { Origin = "module:Foo" }],
        [new ReportingEvent.Progress("hello") { Origin = "module:Foo" }],
        [new ReportingEvent.SshHostKeysReported(
            [new SshHostKeyFingerprint("ed25519", "SHA256:aaa", "ssh-ed25519 AAA")])
            { Origin = "module:SshModule" }],
    ];

    [Fact]
    public void Encode_builds_the_cloud_init_key_layout()
    {
        var ts = new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero);
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var entry = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent("init-network/SetHostname", "finish", "SUCCESS", "ok", ts),
            incarnation: 2,
            vmId: "ABCD",
            eventId: id);

        entry.Key.Should().Be(
            "CLOUD_INIT|2|finish|init-network/SetHostname|ABCD|11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public void Encode_value_is_compact_json_with_cloud_init_fields()
    {
        var ts = new DateTimeOffset(2026, 6, 3, 12, 0, 0, 500, TimeSpan.Zero);

        var entry = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent("modules-final", "finish", "SUCCESS", "done", ts),
            incarnation: 0, vmId: "vm", eventId: Guid.NewGuid());

        entry.Value.Should().NotContain(" ");
        using var doc = JsonDocument.Parse(entry.Value);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("modules-final");
        root.GetProperty("type").GetString().Should().Be("finish");
        root.GetProperty("result").GetString().Should().Be("SUCCESS");
        root.GetProperty("msg").GetString().Should().Be("done");
        // cloud-init isoformat shape: +00:00 offset, 6-digit microseconds.
        root.GetProperty("ts").GetString().Should().Be("2026-06-03T12:00:00.500000+00:00");
    }

    [Fact]
    public void Encode_value_omits_result_for_start_events()
    {
        var entry = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent("init-local", "start", null, "", DateTimeOffset.UnixEpoch),
            incarnation: 0, vmId: "vm", eventId: Guid.NewGuid());

        using var doc = JsonDocument.Parse(entry.Value);
        doc.RootElement.TryGetProperty("result", out _).Should().BeFalse();
    }

    [Fact]
    public void Encode_trims_an_oversized_message_to_fit_the_pool_limit()
    {
        var huge = new string('x', 10_000);

        var entry = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent("modules-final", "finish", "FAIL", huge, DateTimeOffset.UnixEpoch),
            incarnation: 0, vmId: "vm", eventId: Guid.NewGuid());

        // HV_KVP_EXCHANGE_MAX_VALUE_SIZE, excluding the null terminator.
        Encoding.UTF8.GetByteCount(entry.Value).Should().BeLessThanOrEqualTo(2047);
        // Still valid JSON after trimming.
        var act = () => JsonDocument.Parse(entry.Value);
        act.Should().NotThrow();
    }
}
