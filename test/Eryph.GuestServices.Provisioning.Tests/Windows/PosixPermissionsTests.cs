using System.Runtime.Versioning;
using System.Security.AccessControl;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

[SupportedOSPlatform("windows")]
public sealed class PosixPermissionsTests
{
    [Theory]
    [InlineData("0644", 6, 4, 4)]
    [InlineData("644", 6, 4, 4)]
    [InlineData("0o755", 7, 5, 5)]
    [InlineData("0o644", 6, 4, 4)]
    [InlineData("0777", 7, 7, 7)]
    [InlineData("0000", 0, 0, 0)]
    [InlineData("1755", 7, 5, 5)]   // 4-digit form: leading sticky bit is ignored
    public void Parse_ValidOctal_ReturnsExpectedTriplets(string input, int owner, int group, int others)
    {
        var (o, g, t) = PosixPermissions.Parse(input);
        o.Should().Be(owner);
        g.Should().Be(group);
        t.Should().Be(others);
    }

    [Theory]
    [InlineData("0x644")]  // hex prefix is rejected (matches YAML converter)
    [InlineData("088")]    // 8 is not a valid octal digit
    [InlineData("129")]    // 9 is not a valid octal digit
    [InlineData("abc")]
    [InlineData("12")]     // too short
    [InlineData("12345")]  // too long
    [InlineData("")]
    public void Parse_Invalid_Throws(string input)
    {
        var act = () => PosixPermissions.Parse(input);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TripletToRights_Zero_ReturnsNoRights()
    {
        PosixPermissions.TripletToRights(0).Should().Be((FileSystemRights)0);
    }

    [Fact]
    public void TripletToRights_Read_IncludesReadRights()
    {
        var rights = PosixPermissions.TripletToRights(4);
        rights.HasFlag(FileSystemRights.Read).Should().BeTrue();
        rights.HasFlag(FileSystemRights.Write).Should().BeFalse();
        rights.HasFlag(FileSystemRights.ExecuteFile).Should().BeFalse();
    }

    [Fact]
    public void TripletToRights_Write_IncludesWriteRights()
    {
        var rights = PosixPermissions.TripletToRights(2);
        rights.HasFlag(FileSystemRights.Write).Should().BeTrue();
        rights.HasFlag(FileSystemRights.WriteData).Should().BeTrue();
        rights.HasFlag(FileSystemRights.AppendData).Should().BeTrue();
    }

    [Fact]
    public void TripletToRights_Execute_IncludesExecuteRights()
    {
        var rights = PosixPermissions.TripletToRights(1);
        rights.HasFlag(FileSystemRights.ExecuteFile).Should().BeTrue();
        rights.HasFlag(FileSystemRights.ReadAndExecute).Should().BeTrue();
    }

    [Fact]
    public void TripletToRights_ReadWriteExecute_IncludesAllThree()
    {
        var rights = PosixPermissions.TripletToRights(7);
        rights.HasFlag(FileSystemRights.Read).Should().BeTrue();
        rights.HasFlag(FileSystemRights.Write).Should().BeTrue();
        rights.HasFlag(FileSystemRights.ExecuteFile).Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(99)]
    public void TripletToRights_OutOfRange_Throws(int triplet)
    {
        var act = () => PosixPermissions.TripletToRights(triplet);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
