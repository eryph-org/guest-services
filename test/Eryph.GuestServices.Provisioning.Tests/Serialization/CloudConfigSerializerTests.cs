using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Serialization;

public sealed class CloudConfigSerializerTests
{
    // ---- Acknowledged-but-no-op keys (Info) ----

    [Fact]
    public void Apt_block_logs_at_Info_with_explanation()
    {
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            apt:
              sources:
                main: 'deb http://archive.ubuntu.com/ubuntu jammy main'
            """;

        var result = serializer.Deserialize(yaml);

        result.Apt.Should().NotBeNull();
        captured.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Information
            && r.Message.Contains("apt")
            && r.Message.Contains("not applied"));
    }

    [Fact]
    public void Multiple_linux_keys_each_log_one_Info_line()
    {
        // Cross-cloud catlets routinely carry apt + packages + snap together;
        // each acknowledged key surfaces its own log line so the operator
        // sees the full inventory of "we saw this, we did nothing".
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            apt:
              sources: stuff
            snap:
              commands: stuff
            packages:
              - git
              - vim
            """;

        serializer.Deserialize(yaml);

        var infos = captured.Records.Where(r => r.Level == LogLevel.Information).ToList();
        infos.Should().HaveCount(3);
        infos.Should().Contain(r => r.Message.Contains("apt"));
        infos.Should().Contain(r => r.Message.Contains("snap"));
        infos.Should().Contain(r => r.Message.Contains("packages"));
    }

    [Fact]
    public void No_acknowledged_keys_present_emits_no_Info_noise()
    {
        // Most Windows-only catlets carry no Linux keys. The Info path must
        // stay silent in that case — operators reading the log shouldn't see
        // dead "nothing to report" lines.
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            hostname: win-host
            """;

        serializer.Deserialize(yaml);

        captured.Records.Where(r => r.Level == LogLevel.Information).Should().BeEmpty();
    }

    [Fact]
    public void Bool_flag_explicitly_false_still_surfaces_at_Info()
    {
        // package_update: false is an explicit operator decision — "don't
        // update" — and we want them to know we didn't act on either branch.
        // Distinct from "key omitted entirely" which logs nothing.
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            package_update: false
            """;

        serializer.Deserialize(yaml);

        captured.Records.Should().Contain(r =>
            r.Level == LogLevel.Information && r.Message.Contains("package_update"));
    }

    // ---- Truly unknown keys (Warning) ----

    [Fact]
    public void Genuinely_unknown_key_logs_at_Warning()
    {
        // Typo or undocumented extension — schema does not list it, so the
        // YAML deserializer's onUnknownKey callback fires Warning.
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            hsotname: typo
            """;

        serializer.Deserialize(yaml);

        captured.Records.Should().Contain(r =>
            r.Level == LogLevel.Warning && r.Message.Contains("hsotname"));
    }

    [Fact]
    public void Implemented_module_keys_emit_no_Info_or_Warning_noise()
    {
        // hostname is a real implemented module; it should parse silently
        // (no Warning, no Info). The tiering is: implemented → silent;
        // acknowledged-no-op → Info; unknown → Warning. This pins tier 1.
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            hostname: real-host
            timezone: UTC
            ntp:
              enabled: true
            """;

        serializer.Deserialize(yaml);

        captured.Records.Should().BeEmpty();
    }

    // ---- Test helper: capturing logger ----

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, formatter(state, exception)));
        }
    }
}
