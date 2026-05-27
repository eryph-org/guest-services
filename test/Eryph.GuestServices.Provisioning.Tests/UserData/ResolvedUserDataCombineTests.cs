using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.UserData;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.UserData;

/// <summary>
/// Vendor-data is applied as a lower-priority user-data source (cloud-init
/// semantics). <see cref="ResolvedUserData.Combine"/> is the merge the
/// StageRunner uses to fold resolved vendor-data under resolved user-data.
/// </summary>
public sealed class ResolvedUserDataCombineTests
{
    private static ResolvedUserData Make(CloudConfigModel cfg, params string[] scriptNames) => new()
    {
        CloudConfig = cfg,
        Scripts = scriptNames.Select(n => new ScriptPayload(ScriptKind.PowerShell, [], n)).ToArray(),
        Boothooks = [],
    };

    [Fact]
    public void User_data_wins_over_vendor_data_on_conflict()
    {
        var vendor = Make(new CloudConfigModel { Hostname = "vendor-host" }, "vendor.ps1");
        var user = Make(new CloudConfigModel { Hostname = "user-host" }, "user.ps1");

        var combined = ResolvedUserData.Combine(lower: vendor, higher: user);

        combined.CloudConfig.Hostname.Should().Be("user-host");
        // Scripts run vendor-first, then user.
        combined.Scripts.Select(s => s.Filename).Should().Equal("vendor.ps1", "user.ps1");
    }

    [Fact]
    public void Vendor_data_fills_fields_user_data_omits()
    {
        var vendor = Make(new CloudConfigModel { Hostname = "vendor-host", Fqdn = "vendor.fqdn" });
        var user = Make(new CloudConfigModel { Hostname = "user-host" });

        var combined = ResolvedUserData.Combine(vendor, user);

        combined.CloudConfig.Hostname.Should().Be("user-host"); // user wins
        combined.CloudConfig.Fqdn.Should().Be("vendor.fqdn");   // vendor fills the gap
    }
}
