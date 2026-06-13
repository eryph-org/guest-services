using AwesomeAssertions;
using Eryph.GuestServices.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Tests.Logging;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public FileLoggerProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "egs-filelog-" + Guid.NewGuid().ToString("N"));
        _logPath = Path.Combine(_dir, "logs", "agent.log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Constructor_creates_the_log_directory_eagerly()
    {
        // The directory must exist even before the first write so a guest that
        // never logged anything still hands collect-logs a logs\ dir.
        using var provider = new FileLoggerProvider(_logPath);

        Directory.Exists(Path.GetDirectoryName(_logPath)).Should().BeTrue();
    }

    [Fact]
    public void Log_writes_a_formatted_line_with_level_category_and_message()
    {
        using var provider = new FileLoggerProvider(_logPath);
        var logger = provider.CreateLogger("Eryph.Test.Category");

        logger.LogInformation("hello world");

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("[INF]");
        content.Should().Contain("Eryph.Test.Category");
        content.Should().Contain("hello world");
        content.Should().EndWith(Environment.NewLine);
    }

    [Fact]
    public void Log_appends_the_exception_after_the_message()
    {
        using var provider = new FileLoggerProvider(_logPath);
        var logger = provider.CreateLogger("Cat");

        logger.LogError(new InvalidOperationException("boom"), "it failed");

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("[ERR]");
        content.Should().Contain("it failed");
        content.Should().Contain("InvalidOperationException");
        content.Should().Contain("boom");
    }

    [Fact]
    public void Log_skips_entries_with_empty_message_and_no_exception()
    {
        using var provider = new FileLoggerProvider(_logPath);
        var logger = provider.CreateLogger("Cat");

        logger.Log(LogLevel.Information, default, string.Empty, null, (s, _) => s);

        // Nothing meaningful to record — the file should not even be created.
        File.Exists(_logPath).Should().BeFalse();
    }

    [Fact]
    public void Log_rolls_over_to_a_backup_when_the_size_cap_is_exceeded()
    {
        // Tiny cap so a couple of lines trips the roll.
        using var provider = new FileLoggerProvider(_logPath, maxBytes: 64);
        var logger = provider.CreateLogger("Cat");

        // First write creates agent.log; the file now exceeds 64 bytes.
        logger.LogInformation(new string('a', 100));
        // Second write sees the over-cap file, rolls it to .1, starts fresh.
        logger.LogInformation("after-roll");

        var backup = _logPath + ".1";
        File.Exists(backup).Should().BeTrue();
        File.ReadAllText(backup).Should().Contain(new string('a', 100));

        var current = File.ReadAllText(_logPath);
        current.Should().Contain("after-roll");
        current.Should().NotContain(new string('a', 100));
    }

    [Fact]
    public void Append_does_not_throw_after_dispose()
    {
        var provider = new FileLoggerProvider(_logPath);
        var logger = provider.CreateLogger("Cat");
        provider.Dispose();

        // Best-effort: logging after disposal is a no-op, never an exception.
        var act = () => logger.LogInformation("late");
        act.Should().NotThrow();
        File.Exists(_logPath).Should().BeFalse();
    }
}
