using AwesomeAssertions;
using Eryph.GuestServices.Service.Services;

namespace Eryph.GuestServices.Service.Tests;

public sealed class CloudInitStatusReaderTests
{
    // A trimmed-but-real shape of `cloud-init status --format json` on a failed
    // run: top-level `errors` + `recoverable_errors`, as assembled by
    // cloudinit/cmd/status.py (errors is a string list; recoverable_errors maps
    // a level to a list of messages).
    private const string FailedStatusJson = """
        {
          "boot_status_code": "enabled-by-generator",
          "datasource": "nocloud",
          "status": "error",
          "extended_status": "error - done",
          "errors": [
            "Runparts: 1 failures (001-bootstrap.sh) in 3 attempted commands"
          ],
          "recoverable_errors": {
            "WARNING": ["Deprecated cloud-config key 'foo'"],
            "ERROR": ["something recoverable"]
          },
          "last_update": "Sat, 25 Oct 2026 10:00:00 +0000"
        }
        """;

    [Fact]
    public void ParseStatus_reads_status_errors_and_recoverable_errors()
    {
        var probe = CloudInitStatusReader.ParseStatus(FailedStatusJson);

        probe.Installed.Should().BeTrue();
        probe.Status.Should().Be("error");
        probe.Errors.Should().ContainSingle()
            .Which.Should().Contain("001-bootstrap.sh");
        probe.RecoverableErrors.Should().BeEquivalentTo(
            "WARNING: Deprecated cloud-config key 'foo'",
            "ERROR: something recoverable");
    }

    [Fact]
    public void ParseStatus_tolerates_a_clean_run_with_no_error_fields()
    {
        var probe = CloudInitStatusReader.ParseStatus("""{"status":"done"}""");

        probe.Status.Should().Be("done");
        probe.Errors.Should().BeEmpty();
        probe.RecoverableErrors.Should().BeEmpty();
    }
}
