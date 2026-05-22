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
}
