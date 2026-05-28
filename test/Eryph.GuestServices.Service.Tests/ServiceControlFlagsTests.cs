using AwesomeAssertions;
using Eryph.GuestServices.Core;

namespace Eryph.GuestServices.Service.Tests;

public class ServiceControlFlagsTests
{
    // The pure value->bool interpretation is opt-out: only an explicit
    // REG_DWORD 0 turns a capability off. Everything else (missing value or any
    // non-zero) is ON. Tested without touching the real registry.

    [Fact]
    public void InterpretFlag_MissingValue_IsOn()
    {
        RegistryServiceControlFlags.InterpretFlag(null).Should().BeTrue();
    }

    [Fact]
    public void InterpretFlag_DwordZero_IsOff()
    {
        RegistryServiceControlFlags.InterpretFlag(0).Should().BeFalse();
    }

    [Fact]
    public void InterpretFlag_DwordOne_IsOn()
    {
        RegistryServiceControlFlags.InterpretFlag(1).Should().BeTrue();
    }

    [Fact]
    public void InterpretFlag_NonZeroDword_IsOn()
    {
        RegistryServiceControlFlags.InterpretFlag(42).Should().BeTrue();
    }

    [Fact]
    public void InterpretFlag_NonIntValueKind_IsOn()
    {
        // A REG_SZ (or any non-DWORD) value is not the opt-out shape we honor,
        // so it must not disable the capability.
        RegistryServiceControlFlags.InterpretFlag("0").Should().BeTrue();
    }

    [Fact]
    public void NonWindowsOrDefault_AllCapabilities_AreOn()
    {
        // No flags set on the machine (and the Linux CI leg has no registry at
        // all): every capability defaults to ON. Fail-open by design.
        var flags = new RegistryServiceControlFlags();

        flags.IsProvisioningEnabled().Should().BeTrue();
        flags.IsRemoteAccessEnabled().Should().BeTrue();
        flags.IsKvpAuthEnabled().Should().BeTrue();
    }
}
