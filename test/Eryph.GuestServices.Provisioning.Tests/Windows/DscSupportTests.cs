using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Unit tests for the pure, host-OS-independent DSC helpers. These run on any
/// host (no CA1416 surface) and pin the reboot-signal scan, the GitHub release
/// asset selection, and the checksum verification.
/// </summary>
public sealed class DscSupportTests
{
    [Fact]
    public void RequiresReboot_false_for_empty_or_non_json()
    {
        DscSupport.RequiresReboot(null).Should().BeFalse();
        DscSupport.RequiresReboot("").Should().BeFalse();
        DscSupport.RequiresReboot("not json at all").Should().BeFalse();
    }

    [Fact]
    public void RequiresReboot_true_for_nested_reboot_required_flag()
    {
        const string json = """
            { "results": [ { "name": "x", "result": { "rebootRequired": true } } ] }
            """;

        DscSupport.RequiresReboot(json).Should().BeTrue();
    }

    [Fact]
    public void RequiresReboot_accepts_snake_case_and_string_true()
    {
        DscSupport.RequiresReboot("""{ "reboot_required": "true" }""").Should().BeTrue();
    }

    [Fact]
    public void RequiresReboot_false_when_flag_is_falsey()
    {
        DscSupport.RequiresReboot("""{ "results": [ { "rebootRequired": false } ] }""").Should().BeFalse();
    }

    [Fact]
    public void RequiresReboot_scans_json_lines()
    {
        const string jsonLines =
            "{ \"name\": \"a\", \"rebootRequired\": false }\n"
            + "{ \"name\": \"b\", \"rebootRequired\": true }\n";

        DscSupport.RequiresReboot(jsonLines).Should().BeTrue();
    }

    [Fact]
    public void RequiresReboot_true_for_restartRequired_in_execution_information()
    {
        // DSC v3.2+ collects per-resource restart requests into the config
        // result's execution information under 'restartRequired' (an array of
        // {system|service|process} objects), emitted only when a restart is due.
        const string json = """
            {
              "metadata": {
                "Microsoft.DSC": {
                  "version": "3.2.2",
                  "securityContext": "elevated",
                  "restartRequired": [
                    { "service": "Spooler" },
                    { "system": "A pending feature install needs a restart." }
                  ]
                }
              },
              "results": []
            }
            """;

        DscSupport.RequiresReboot(json).Should().BeTrue();
    }

    [Fact]
    public void RequiresReboot_true_for_resource_restartRequired_array()
    {
        // The canonical per-resource property '_restartRequired'.
        const string json = """
            {
              "results": [
                {
                  "type": "Microsoft.Windows/WindowsFeature",
                  "name": "dns",
                  "result": { "afterState": { "_restartRequired": [ { "process": { "name": "w3wp", "id": 1234 } } ] } }
                }
              ]
            }
            """;

        DscSupport.RequiresReboot(json).Should().BeTrue();
    }

    [Fact]
    public void RequiresReboot_false_for_empty_restartRequired_array()
    {
        // Present but empty must NOT trigger a reboot (DSC omits it entirely when
        // none is required, but be robust to an empty array too).
        DscSupport.RequiresReboot("""{ "metadata": { "Microsoft.DSC": { "restartRequired": [] } } }""")
            .Should().BeFalse();
    }

    [Fact]
    public void RequiresReboot_true_for_legacy_rebootRequested_boolean()
    {
        // '_restartRequired' replaced the legacy boolean '_rebootRequested'; honour both.
        DscSupport.RequiresReboot("""{ "results": [ { "result": { "_rebootRequested": true } } ] }""")
            .Should().BeTrue();
    }

    [Fact]
    public void RequiresReboot_false_for_legacy_rebootRequested_false()
    {
        DscSupport.RequiresReboot("""{ "_rebootRequested": false }""").Should().BeFalse();
    }

    [Fact]
    public void SelectWindowsReleaseAsset_picks_matching_arch_zip()
    {
        const string json = """
            {
              "assets": [
                { "name": "DSC-3.1.0-x86_64-pc-windows-msvc.zip", "browser_download_url": "https://x/win-x64.zip" },
                { "name": "DSC-3.1.0-aarch64-pc-windows-msvc.zip", "browser_download_url": "https://x/win-arm64.zip" },
                { "name": "DSC-3.1.0-x86_64-unknown-linux-gnu.tar.gz", "browser_download_url": "https://x/linux.tar.gz" }
              ]
            }
            """;

        DscSupport.SelectWindowsReleaseAsset(json, "x86_64").Should().Be("https://x/win-x64.zip");
        DscSupport.SelectWindowsReleaseAsset(json, "aarch64").Should().Be("https://x/win-arm64.zip");
    }

    [Fact]
    public void SelectWindowsReleaseAsset_throws_when_no_matching_asset()
    {
        const string json = """
            { "assets": [ { "name": "DSC-3.1.0-x86_64-unknown-linux-gnu.tar.gz", "browser_download_url": "https://x/l.tgz" } ] }
            """;

        var act = () => DscSupport.SelectWindowsReleaseAsset(json, "x86_64");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Sha256Mismatch_null_when_content_matches()
    {
        var content = Encoding.UTF8.GetBytes("$schema: x\nresources: []\n");
        var hex = Convert.ToHexString(SHA256.HashData(content));

        DscSupport.Sha256Mismatch(content, "sha256:" + hex).Should().BeNull();
        // The algorithm prefix is optional and the hex is case-insensitive.
        DscSupport.Sha256Mismatch(content, hex.ToLowerInvariant()).Should().BeNull();
    }

    [Fact]
    public void Sha256Mismatch_reports_a_wrong_checksum()
    {
        var content = Encoding.UTF8.GetBytes("payload");

        DscSupport.Sha256Mismatch(content, "sha256:" + new string('0', 64))
            .Should().NotBeNull();
    }

    [Fact]
    public void Sha256Mismatch_rejects_an_unsupported_algorithm()
    {
        var content = Encoding.UTF8.GetBytes("payload");

        DscSupport.Sha256Mismatch(content, "md5:abcdef")
            .Should().NotBeNull().And.Contain("unsupported");
    }
}
