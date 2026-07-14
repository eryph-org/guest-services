using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows.Win32;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// <see cref="NetUserHelpers.DescribeUserError"/> turns the raw NERR_* / Win32
/// status codes NetUserAdd and NetUserSetInfo return into a human-readable
/// reason. It is what makes a failed user/password provisioning report readable
/// (e.g. "the password does not meet the system's length, complexity, or history
/// policy") instead of "NetUserSetInfo(1003) failed ... with code 2245". The
/// mapping is pure (no Win32 calls for the known codes), so it is asserted
/// directly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DescribeUserErrorTests
{
    [Fact]
    public void PasswordTooShort_2245_describes_the_policy_rejection()
    {
        // 2245 is the umbrella code for ANY length/complexity/history rejection.
        var text = NetUserHelpers.DescribeUserError(NetApi32.NERR_PasswordTooShort);
        text.Should().Contain("password");
        text.Should().Contain("complexity");
        text.Should().NotContain("NetUserSetInfo");
        text.Should().NotContain("2245");
    }

    [Theory]
    [InlineData(NetApi32.NERR_PasswordHistConflict, "history")]
    [InlineData(NetApi32.NERR_PasswordTooRecent, "age")]
    [InlineData(NetApi32.NERR_BadUsername, "user name")]
    [InlineData(NetApi32.NERR_UserExists, "already exists")]
    [InlineData(NetApi32.NERR_UserNotFound, "does not exist")]
    [InlineData(5, "access is denied")]
    public void Known_codes_map_to_readable_phrases(int code, string fragment)
    {
        NetUserHelpers.DescribeUserError(code).Should().Contain(fragment);
    }

    [Fact]
    public void Unknown_code_falls_back_to_a_non_empty_system_message()
    {
        // An unmapped code must still yield something readable (the OS-localized
        // text), never an empty string.
        NetUserHelpers.DescribeUserError(0x1234).Should().NotBeNullOrWhiteSpace();
    }
}
