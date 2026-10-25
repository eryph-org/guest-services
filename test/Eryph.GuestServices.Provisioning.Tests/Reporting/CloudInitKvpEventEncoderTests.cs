using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Eryph.GuestServices.HvDataExchange.Common;
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
    public void Map_UserCodeFailed_is_a_finish_fail_nested_under_module_and_item()
    {
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.UserCodeFailed("runcmd", "#2", 7, "boom on stderr")
            {
                Origin = "module:RuncmdModule",
            },
            "modules-config");

        mapped!.Value.Name.Should().Be("modules-config/runcmd/#2");
        mapped.Value.Type.Should().Be("finish");
        mapped.Value.Result.Should().Be("FAIL");
        mapped.Value.Description.Should().Be("exit 7: boom on stderr");
    }

    [Fact]
    public void Map_UserCodeFailed_without_output_is_just_the_exit_code()
    {
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.UserCodeFailed("scripts-user", "003-x.ps1", 1, "")
            {
                Origin = "module:ScriptsUserModule",
            },
            "modules-final");

        mapped!.Value.Description.Should().Be("exit 1");
    }

    [Fact]
    public void Encode_breaks_down_a_large_user_code_tail_across_subkeys()
    {
        // A multi-KB stderr tail must survive transport: the encoder splits it
        // into …|<index> subkeys that the host decoder reassembles.
        var tail = new string('x', 4000);
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.UserCodeFailed("runcmd", "#1", 2, tail) { Origin = "module:RuncmdModule" },
            "modules-config");

        var entries = CloudInitKvpEventEncoder.Encode(mapped!.Value, incarnation: 123, Guid.NewGuid());

        entries.Count.Should().BeGreaterThan(1);
        entries.Should().OnlyContain(e => e.Key.Contains("|"));
        var reassembled = string.Concat(entries
            .Select(e => JsonDocument.Parse(e.Value).RootElement)
            .OrderBy(r => r.GetProperty("msg_i").GetInt32())
            .Select(r => r.GetProperty("msg").GetString()));
        reassembled.Should().Contain(tail);
    }

    [Fact]
    public void Encode_output_always_passes_the_KVP_DataValidator()
    {
        // The host rejects (and silently drops) any key > 255 chars or value
        // > 1023 chars. A large stderr tail AND a pathologically long user script
        // filename must still produce only valid entries.
        var tail = new string('x', 5000);
        var longItem = new string('f', 200) + ".ps1";
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.UserCodeFailed("scripts-user", longItem, 9, tail)
            {
                Origin = "module:ScriptsUserModule",
            },
            "modules-final");

        var entries = CloudInitKvpEventEncoder.Encode(mapped!.Value, incarnation: 1730000000, Guid.NewGuid());

        entries.Should().NotBeEmpty();
        foreach (var entry in entries)
        {
            DataValidator.IsKeyValid(entry.Key, out var keyError).Should().BeTrue(keyError);
            DataValidator.IsValueValid(entry.Value, out var valueError).Should().BeTrue(valueError);
        }
    }

    [Fact]
    public void Map_UserCodeFailed_truncates_a_long_item_name()
    {
        var longItem = new string('f', 300);
        var mapped = CloudInitKvpEventEncoder.Map(
            new ReportingEvent.UserCodeFailed("scripts-user", longItem, 1, "x")
            {
                Origin = "module:ScriptsUserModule",
            },
            "modules-final");

        // modules-final/scripts-user/<=80 chars → well under the 255-char key limit.
        mapped!.Value.Name.Length.Should().BeLessThan(120);
        mapped.Value.Name.Should().EndWith("…");
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
            eventId: id).Single();

        // cloud-init _event_key: CLOUD_INIT|<incarnation>|<type>|<name>|<uuid>.
        entry.Key.Should().Be(
            "CLOUD_INIT|2|finish|init-network/SetHostname|11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public void Encode_value_is_compact_json_with_cloud_init_fields()
    {
        var ts = new DateTimeOffset(2026, 6, 3, 12, 0, 0, 500, TimeSpan.Zero);

        var entry = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent("modules-final", "finish", "SUCCESS", "done", ts),
            incarnation: 0, eventId: Guid.NewGuid()).Single();

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
    public void Encode_ts_omits_the_fractional_part_for_whole_seconds()
    {
        // Python datetime.isoformat() drops the fractional seconds entirely when
        // microseconds are 0 — it does not emit `.000000`.
        var entry = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent(
                "init-local", "start", null, "",
                new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero)),
            incarnation: 0, eventId: Guid.NewGuid()).Single();

        using var doc = JsonDocument.Parse(entry.Value);
        doc.RootElement.GetProperty("ts").GetString().Should().Be("2026-06-03T12:00:00+00:00");
    }

    [Fact]
    public void Encode_value_omits_result_for_start_events()
    {
        var entry = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent("init-local", "start", null, "", DateTimeOffset.UnixEpoch),
            incarnation: 0, eventId: Guid.NewGuid()).Single();

        using var doc = JsonDocument.Parse(entry.Value);
        doc.RootElement.TryGetProperty("result", out _).Should().BeFalse();
    }

    [Fact]
    public void Encode_breaks_an_oversized_message_into_indexed_subkeys()
    {
        // cloud-init _break_down: a value over the Azure limit (1024) is split
        // across <base>|<index> subkeys, each a full event JSON carrying msg_i
        // and a slice of the description.
        var huge = new string('x', 10_000);

        var entries = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent("modules-final", "finish", "FAIL", huge, DateTimeOffset.UnixEpoch),
            incarnation: 0, eventId: Guid.NewGuid());

        entries.Count.Should().BeGreaterThan(1);

        var reassembled = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].Key.Should().EndWith($"|{i}");
            Encoding.UTF8.GetByteCount(entries[i].Value).Should().BeLessThanOrEqualTo(1024);
            using var doc = JsonDocument.Parse(entries[i].Value);
            doc.RootElement.GetProperty("msg_i").GetInt32().Should().Be(i);
            reassembled.Append(doc.RootElement.GetProperty("msg").GetString());
        }

        // Concatenating the slices reconstructs the full description.
        reassembled.ToString().Should().Be(huge);
    }

    [Fact]
    public void Encode_break_down_stays_valid_json_for_escape_heavy_descriptions()
    {
        // Regression: slicing the *escaped* string could cut a `\` or `\uXXXX`
        // escape mid-sequence and emit unparseable JSON. We slice the raw text
        // and escape per chunk, so every chunk must parse — and reassemble.
        // The unit mixes backslashes, quotes, tab, newline and non-ASCII.
        var unit = "C:\\path\\to\\x\twith \"quotes\"\nand ünïcödé éè中 ";
        var builder = new StringBuilder();
        while (builder.Length < 4000)
            builder.Append(unit);
        var description = builder.ToString();

        var entries = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent("modules-final", "finish", "FAIL", description, DateTimeOffset.UnixEpoch),
            incarnation: 0, eventId: Guid.NewGuid());

        entries.Count.Should().BeGreaterThan(1);

        var reassembled = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            // Each chunk parses as valid JSON (the regression guard) and fits.
            using var doc = JsonDocument.Parse(entries[i].Value);
            Encoding.UTF8.GetByteCount(entries[i].Value).Should().BeLessThanOrEqualTo(1024);
            doc.RootElement.GetProperty("msg_i").GetInt32().Should().Be(i);
            reassembled.Append(doc.RootElement.GetProperty("msg").GetString());
        }

        reassembled.ToString().Should().Be(description);
    }

    [Fact]
    public void Encode_break_down_never_splits_a_surrogate_pair()
    {
        // Each emoji is a supplementary-plane code point — a UTF-16 surrogate
        // pair (2 chars, 4 UTF-8 bytes). A chunk boundary that fell between the
        // high and low surrogate would emit U+FFFD and corrupt the reassembly.
        // The chunker must never cut inside a pair; verify across many chunks
        // and at varied offsets by mixing in single-unit ASCII runs.
        var builder = new StringBuilder();
        var n = 0;
        while (builder.Length < 4000)
            builder.Append("😀😁🚀").Append(new string('a', n++ % 7));
        var description = builder.ToString();

        var entries = CloudInitKvpEventEncoder.Encode(
            new CloudInitEvent("modules-final", "finish", "FAIL", description, DateTimeOffset.UnixEpoch),
            incarnation: 0, eventId: Guid.NewGuid());

        entries.Count.Should().BeGreaterThan(1);

        var reassembled = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            using var doc = JsonDocument.Parse(entries[i].Value);
            Encoding.UTF8.GetByteCount(entries[i].Value).Should().BeLessThanOrEqualTo(1024);
            var msg = doc.RootElement.GetProperty("msg").GetString()!;
            // No chunk may carry a lone (unpaired) surrogate.
            msg.Should().NotContain("�");
            for (var j = 0; j < msg.Length; j++)
                if (char.IsHighSurrogate(msg[j]))
                    (j + 1 < msg.Length && char.IsLowSurrogate(msg[j + 1])).Should().BeTrue();
            reassembled.Append(msg);
        }

        reassembled.ToString().Should().Be(description);
    }
}
