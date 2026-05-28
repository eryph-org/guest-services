using AwesomeAssertions;
using Eryph.GuestServices.Core;

namespace Eryph.GuestServices.Service.Tests;

public class ServiceControlFlagsTests
{
    // Each backend has its own value→bool interpreter (kept separate so a
    // mistyped REG_SZ on Windows cannot accidentally exercise the Linux
    // string-parsing path). Both are opt-out: a capability is ON unless an
    // explicit OFF value is present.

    // ---- Windows registry shape (int / REG_DWORD only) ----

    [Fact]
    public void InterpretWindowsRegistryValue_NullValue_IsOn()
    {
        PlatformServiceControlFlags.InterpretWindowsRegistryValue(null).Should().BeTrue();
    }

    [Fact]
    public void InterpretWindowsRegistryValue_DwordZero_IsOff()
    {
        PlatformServiceControlFlags.InterpretWindowsRegistryValue(0).Should().BeFalse();
    }

    [Fact]
    public void InterpretWindowsRegistryValue_DwordOne_IsOn()
    {
        PlatformServiceControlFlags.InterpretWindowsRegistryValue(1).Should().BeTrue();
    }

    [Fact]
    public void InterpretWindowsRegistryValue_NonZeroDword_IsOn()
    {
        PlatformServiceControlFlags.InterpretWindowsRegistryValue(42).Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    public void InterpretWindowsRegistryValue_RegSzFalsy_IsStillOn(string regSzValue)
    {
        // Regression guard. The documented Windows control surface is
        // REG_DWORD only. A REG_SZ shouldn't silently disable a flag — a
        // user typing the wrong type means "I tried to set it but
        // mistyped", not "disable this capability".
        PlatformServiceControlFlags.InterpretWindowsRegistryValue(regSzValue).Should().BeTrue();
    }

    [Fact]
    public void InterpretWindowsRegistryValue_UnknownKind_IsOn()
    {
        PlatformServiceControlFlags.InterpretWindowsRegistryValue(new object()).Should().BeTrue();
    }

    // ---- Linux config-file shape (string) ----

    [Fact]
    public void InterpretLinuxConfigValue_NullValue_IsOn()
    {
        PlatformServiceControlFlags.InterpretLinuxConfigValue(null).Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("  0  ")]      // whitespace around the value
    [InlineData("  false ")]
    public void InterpretLinuxConfigValue_FalsyValue_IsOff(string value)
    {
        PlatformServiceControlFlags.InterpretLinuxConfigValue(value).Should().BeFalse();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("42")]
    [InlineData("yes")]        // not parseable as bool/int -> default ON
    [InlineData("")]            // empty string -> default ON
    [InlineData("garbage")]
    public void InterpretLinuxConfigValue_TruthyOrUnknown_IsOn(string value)
    {
        PlatformServiceControlFlags.InterpretLinuxConfigValue(value).Should().BeTrue();
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
