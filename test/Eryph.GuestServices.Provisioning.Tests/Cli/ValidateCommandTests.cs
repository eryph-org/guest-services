using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Cli;

namespace Eryph.GuestServices.Provisioning.Tests.Cli;

[SupportedOSPlatform("windows")]
[RequiresUnreferencedCode("Tests ValidateCommand which uses ProvisioningContainerBuilder.")]
public sealed class ValidateCommandTests : IDisposable
{
    private readonly string _tempDir;

    public ValidateCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "egs-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_missing_user_data_flag_returns_two()
    {
        var sut = new ValidateCommand();

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("validate"),
            new ValidateCommand.Settings());

        exit.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_nonexistent_file_returns_two()
    {
        var sut = new ValidateCommand();
        var missing = Path.Combine(_tempDir, "does-not-exist.yaml");

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("validate"),
            new ValidateCommand.Settings { UserDataPath = missing });

        exit.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_valid_user_data_returns_zero()
    {
        var path = Path.Combine(_tempDir, "valid.yaml");
        await File.WriteAllTextAsync(path,
            """
            #cloud-config
            hostname: myhost
            users:
              - name: alice
            """);

        var sut = new ValidateCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("validate"),
            new ValidateCommand.Settings { UserDataPath = path });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_invalid_user_data_returns_one()
    {
        // Hostname must be a single DNS label — "host.local" fails the
        // hostname validator (use fqdn for FQDNs).
        var path = Path.Combine(_tempDir, "invalid.yaml");
        await File.WriteAllTextAsync(path,
            """
            #cloud-config
            hostname: invalid.with.dots
            """);

        var sut = new ValidateCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("validate"),
            new ValidateCommand.Settings { UserDataPath = path });

        exit.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_unknown_target_returns_two()
    {
        var path = Path.Combine(_tempDir, "any.yaml");
        await File.WriteAllTextAsync(path, "#cloud-config\nhostname: myhost\n");

        var sut = new ValidateCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("validate"),
            new ValidateCommand.Settings { UserDataPath = path, Target = "mac" });

        exit.Should().Be(2);
    }

    // --target windows: Linux-only keys must surface as Warnings. Hostname is
    // cross-platform and must not warn. Exit code stays 0 — validation
    // passed; the warning is portability advice, not a validation failure.
    [Fact]
    public async Task ExecuteAsync_target_windows_warns_about_linux_only_keys()
    {
        var path = Path.Combine(_tempDir, "linux-keys.yaml");
        await File.WriteAllTextAsync(path,
            """
            #cloud-config
            hostname: myhost
            apt:
              preserve_sources_list: true
            chef:
              install_type: omnibus
            """);

        var (exit, output) = await RunCaptureAsync(new ValidateCommand.Settings
        {
            UserDataPath = path,
            Target = "windows",
        });

        exit.Should().Be(0);
        output.Should().Contain("apt");
        output.Should().Contain("chef");
        output.Should().Contain("not supported on windows");
        // Cross-platform key must not be flagged as unsupported.
        output.Should().NotContain("'hostname' is not supported on windows");
    }

    // --target linux: Windows-only keys (license:) must surface as Warnings.
    [Fact]
    public async Task ExecuteAsync_target_linux_warns_about_windows_only_keys()
    {
        var path = Path.Combine(_tempDir, "windows-keys.yaml");
        await File.WriteAllTextAsync(path,
            """
            #cloud-config
            hostname: myhost
            license:
              set_avma: true
            """);

        var (exit, output) = await RunCaptureAsync(new ValidateCommand.Settings
        {
            UserDataPath = path,
            Target = "linux",
        });

        exit.Should().Be(0);
        output.Should().Contain("license");
        output.Should().Contain("not supported on linux");
    }

    // --target all is the lenient default. Identical to no --target — no
    // per-field portability warnings.
    [Fact]
    public async Task ExecuteAsync_target_all_emits_no_portability_warnings()
    {
        var path = Path.Combine(_tempDir, "linux-keys.yaml");
        await File.WriteAllTextAsync(path,
            """
            #cloud-config
            hostname: myhost
            apt:
              preserve_sources_list: true
            """);

        var (exit, output) = await RunCaptureAsync(new ValidateCommand.Settings
        {
            UserDataPath = path,
            Target = "all",
        });

        exit.Should().Be(0);
        output.Should().NotContain("not supported on");
    }

    private async Task<(int Exit, string Output)> RunCaptureAsync(ValidateCommand.Settings settings)
    {
        var originalOut = Console.Out;
        using var captured = new StringWriter();
        try
        {
            // Spectre.Console reaches into Console at static-init time;
            // rewriting Console.Out here is the simplest way to capture
            // its output without rebuilding the AnsiConsole singleton.
            Console.SetOut(captured);
            Spectre.Console.AnsiConsole.Console = Spectre.Console.AnsiConsole.Create(
                new Spectre.Console.AnsiConsoleSettings
                {
                    Ansi = Spectre.Console.AnsiSupport.No,
                    ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
                    Out = new Spectre.Console.AnsiConsoleOutput(captured),
                });

            var sut = new ValidateCommand();
            var exit = await sut.ExecuteAsync(
                TestCommandContext.Create("validate"),
                settings);
            return (exit, captured.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Spectre.Console.AnsiConsole.Console = Spectre.Console.AnsiConsole.Create(
                new Spectre.Console.AnsiConsoleSettings());
        }
    }
}
