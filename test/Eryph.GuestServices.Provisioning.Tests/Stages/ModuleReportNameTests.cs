using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.Stages;

namespace Eryph.GuestServices.Provisioning.Tests.Stages;

/// <summary>
/// The provisioning report (KVP status / cloud-init event stream) must carry a
/// human-readable module name, not the C# type name. This pins the report name
/// resolved for every registered module (attribute override else derived) so a
/// rename or a new module can't silently regress the operator-facing report.
/// </summary>
public sealed class ModuleReportNameTests
{
    private static string ReportName(Type moduleType)
    {
        var attr = (StageAttribute?)Attribute.GetCustomAttribute(moduleType, typeof(StageAttribute));
        return attr?.Name ?? StageRunner.DeriveReportName(moduleType);
    }

    [Theory]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.UsersGroupsModule), "users-groups")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.SetPasswordsModule), "set-passwords")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.SetHostnameModule), "set-hostname")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.ApplyNetworkConfigModule), "apply-network-config")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.SshModule), "ssh")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.WriteFilesModule), "write-files")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.WriteFilesDeferredModule), "write-files-deferred")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.RuncmdModule), "runcmd")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.GrowpartModule), "growpart")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.TimezoneModule), "timezone")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.ScriptsUserModule), "scripts-user")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.EgsModule), "egs")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.LicensingModule), "licensing")]
    // Explicit overrides — canonical cloud-init names that differ from the derived form.
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.NtpClientModule), "ntp")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.SetLocaleModule), "locale")]
    [InlineData(typeof(Eryph.GuestServices.Provisioning.Modules.PowerStateModule), "power-state-change")]
    public void Module_report_name_is_the_expected_readable_name(Type moduleType, string expected)
    {
        ReportName(moduleType).Should().Be(expected);
    }

    [Fact]
    public void Every_registered_module_has_a_readable_unique_non_type_name()
    {
        var names = ModuleRegistry.ModuleTypes.ToDictionary(t => t, ReportName);

        foreach (var (type, name) in names)
        {
            name.Should().NotBeNullOrWhiteSpace();
            // Readable = lowercase kebab-case, and never the C# type name.
            name.Should().MatchRegex("^[a-z0-9]+(-[a-z0-9]+)*$");
            name.Should().NotContain("Module");
            name.Should().NotBe(type.Name);
        }

        names.Values.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("UsersGroupsModule", "users-groups")]
    [InlineData("SetHostnameModule", "set-hostname")]
    [InlineData("ApplyNetworkConfigModule", "apply-network-config")]
    [InlineData("SshModule", "ssh")]
    public void DeriveReportName_strips_suffix_and_kebab_cases(string typeName, string expected)
    {
        var type = ModuleRegistry.ModuleTypes.Single(t => t.Name == typeName);
        StageRunner.DeriveReportName(type).Should().Be(expected);
    }
}
