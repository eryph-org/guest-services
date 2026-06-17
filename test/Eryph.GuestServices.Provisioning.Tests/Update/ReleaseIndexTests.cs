using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Update;

namespace Eryph.GuestServices.Provisioning.Tests.Update;

public sealed class ReleaseIndexTests
{
    // Trimmed to the shape the real index.json uses (camelCase keys, per-file
    // os/arch/tags), so the parser is pinned against the actual producer.
    private const string Sample = """
        {
          "product": "eryph",
          "component": "guest-services",
          "latestVersion": "0.4.0",
          "latestStableVersion": "0.4.0",
          "versions": {
            "0.4.0": {
              "version": "0.4.0",
              "files": [
                {
                  "filename": "egs_0.4.0_windows_amd64.zip",
                  "url": "https://releases.dbosoft.eu/eryph/guest-services/0.4.0/egs_0.4.0_windows_amd64.zip",
                  "sha256Checksum": "deadbeef",
                  "os": "windows",
                  "arch": "amd64"
                },
                {
                  "filename": "eryph_guest-services_0.4.0.iso",
                  "url": "https://releases.dbosoft.eu/eryph/guest-services/0.4.0/eryph_guest-services_0.4.0.iso",
                  "sha256Checksum": "cafef00d",
                  "tags": ["iso"]
                }
              ]
            }
          }
        }
        """;

    [Fact]
    public void Parse_reads_versions_and_files()
    {
        var index = ReleaseIndex.Parse(Sample);

        index.LatestVersion.Should().Be("0.4.0");
        index.LatestStableVersion.Should().Be("0.4.0");
        index.Versions.Should().ContainKey("0.4.0");

        var win = UpdateTargetResolver.SelectWindowsPackage(index.Versions!["0.4.0"].Files!);
        win.Should().NotBeNull();
        win!.Sha256Checksum.Should().Be("deadbeef");
        win.Filename.Should().EndWith("windows_amd64.zip");
    }
}
