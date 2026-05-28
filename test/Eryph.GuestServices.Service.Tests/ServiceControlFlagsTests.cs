using AwesomeAssertions;
using Eryph.GuestServices.Core;

namespace Eryph.GuestServices.Service.Tests;

public class ServiceControlFlagsTests
{
    // The pure value->bool interpretation is opt-out: only an explicit 0 /
    // false turns a capability off. Everything else (missing value, unknown
    // shape) is ON. Tested without touching the real registry or filesystem.

    [Fact]
    public void InterpretFlag_NullValue_IsOn()
    {
        PlatformServiceControlFlags.InterpretFlag(null).Should().BeTrue();
    }

    // ---- Windows REG_DWORD shape (int) ----

    [Fact]
    public void InterpretFlag_DwordZero_IsOff()
    {
        PlatformServiceControlFlags.InterpretFlag(0).Should().BeFalse();
    }

    [Fact]
    public void InterpretFlag_DwordOne_IsOn()
    {
        PlatformServiceControlFlags.InterpretFlag(1).Should().BeTrue();
    }

    [Fact]
    public void InterpretFlag_NonZeroDword_IsOn()
    {
        PlatformServiceControlFlags.InterpretFlag(42).Should().BeTrue();
    }

    // ---- Linux config-file shape (string) ----

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("  0  ")]      // whitespace around the value
    [InlineData("  false ")]
    public void InterpretFlag_StringFalsyValue_IsOff(string value)
    {
        PlatformServiceControlFlags.InterpretFlag(value).Should().BeFalse();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("42")]
    [InlineData("yes")]        // not parseable as bool/int -> default ON
    [InlineData("")]            // empty string -> default ON
    [InlineData("garbage")]
    public void InterpretFlag_StringTruthyOrUnknown_IsOn(string value)
    {
        PlatformServiceControlFlags.InterpretFlag(value).Should().BeTrue();
    }

    [Fact]
    public void InterpretFlag_UnknownKind_IsOn()
    {
        // A REG_SZ on Windows is not the opt-out shape we honor; tolerate by
        // defaulting to ON. (REG_SZ is still a string after MZ; this guard is
        // really about non-string/non-int CIM/registry types.)
        PlatformServiceControlFlags.InterpretFlag(new object()).Should().BeTrue();
    }

    // ---- Linux config-file line parser ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# comment")]
    [InlineData("  # indented comment")]
    [InlineData("no-equals-sign")]
    [InlineData("=value-without-key")]
    public void ParseConfigLine_NotAnEntry_ReturnsNull(string line)
    {
        PlatformServiceControlFlags.ParseConfigLine(line).Should().BeNull();
    }

    [Theory]
    [InlineData("KvpAuthEnabled=0", "KvpAuthEnabled", "0")]
    [InlineData("  KvpAuthEnabled  =  0  ", "KvpAuthEnabled", "0")]
    [InlineData("RemoteAccessEnabled=false", "RemoteAccessEnabled", "false")]
    [InlineData("ProvisioningEnabled=1", "ProvisioningEnabled", "1")]
    [InlineData("Key=value=with=equals", "Key", "value=with=equals")]
    public void ParseConfigLine_WellFormed_ReturnsKeyValue(string line, string expectedKey, string expectedValue)
    {
        var entry = PlatformServiceControlFlags.ParseConfigLine(line);
        entry.Should().NotBeNull();
        entry!.Value.key.Should().Be(expectedKey);
        entry.Value.value.Should().Be(expectedValue);
    }

    // ---- Platform dispatch (live) ----

    [Fact]
    public void Default_AllCapabilities_AreOn()
    {
        // No flags set on the machine (CI Linux has no /etc/opt/eryph file,
        // CI Windows has no HKLM keys): every capability defaults to ON.
        // Fail-open by design — also verifies the platform dispatcher
        // doesn't throw when the config source is absent.
        var flags = new PlatformServiceControlFlags();

        flags.IsProvisioningEnabled().Should().BeTrue();
        flags.IsRemoteAccessEnabled().Should().BeTrue();
        flags.IsKvpAuthEnabled().Should().BeTrue();
    }
}
