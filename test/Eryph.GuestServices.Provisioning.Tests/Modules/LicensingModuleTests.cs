using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class LicensingModuleTests
{
    private static TestModuleContext NewContext(
        IWindowsOs os,
        string sourceName = "test",
        string? cloudName = null) =>
        new(os, new DataSourceResult
        {
            SourceName = sourceName,
            InstanceId = "id",
            PlatformMetadata = cloudName is null
                ? null
                : new PlatformMetadata { CloudName = cloudName },
        });

    private static IWindowsOs NewOs(
        string? resolvedAvmaKey = null,
        string? resolvedKmsKey = null,
        bool isEvaluation = false,
        bool rebootAfterRearm = true)
    {
        var os = Substitute.For<IWindowsOs>();
        os.ResolveVolumeActivationKeyAsync(VolumeActivationKeyType.Avma, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(resolvedAvmaKey));
        os.ResolveVolumeActivationKeyAsync(VolumeActivationKeyType.Kms, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(resolvedKmsKey));
        os.IsEvaluationLicenseAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(isEvaluation));
        os.RearmLicenseAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RearmResult { RebootRequired = rebootAfterRearm }));
        return os;
    }

    [Fact]
    public async Task Default_run_on_non_server_attempts_AVMA_then_silently_no_ops()
    {
        // No license block. Module is always-on by default — it asks the OS
        // for an AVMA key. A null result on (e.g.) a client SKU must NOT
        // produce ModuleOutcome.Failed; the defaulted intent is best-effort.
        var os = NewOs(resolvedAvmaKey: null, isEvaluation: false);
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).ResolveVolumeActivationKeyAsync(
            VolumeActivationKeyType.Avma, Arg.Any<CancellationToken>());
        await os.DidNotReceive().ApplyLicenseAsync(
            Arg.Any<LicenseSpec>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().RearmLicenseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Default_run_installs_resolved_AVMA_key_when_available()
    {
        // Server SKU with an AVMA key in the table — defaulted set_avma
        // should pick it up and install it without any operator declaration.
        var os = NewOs(resolvedAvmaKey: "RESOLVED-AVMA-KEY", isEvaluation: false);
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).ApplyLicenseAsync(
            Arg.Is<LicenseSpec>(s => s.ProductKey == "RESOLVED-AVMA-KEY"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Default_run_rearms_when_OS_reports_evaluation()
    {
        // Rearm is on-by-default; eval check gates it so non-eval guests
        // don't waste a rearm slot. Eval guest must trigger rearm.
        var os = NewOs(isEvaluation: true, rebootAfterRearm: true);
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os),
            CancellationToken.None);

        var reboot = result.Should().BeOfType<ModuleOutcome.RebootRequested>().Subject;
        reboot.Reason.Should().ContainEquivalentOf("rearm");
        await os.Received(1).RearmLicenseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Default_run_does_not_rearm_when_not_evaluation()
    {
        // Eval gate must hold — running rearm on a non-eval product would
        // burn a rearm slot Microsoft documents as limited (5 by default).
        var os = NewOs(isEvaluation: false);
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os),
            CancellationToken.None);

        await os.DidNotReceive().RearmLicenseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rearm_false_overrides_the_default()
    {
        // Operator can opt out of the rearm default explicitly.
        var os = NewOs(isEvaluation: true);
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                License = new LicenseConfig { Rearm = false },
            }),
            NewContext(os),
            CancellationToken.None);

        await os.DidNotReceive().RearmLicenseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_avma_false_overrides_the_default()
    {
        // Operator opts out of the AVMA default — module must not query
        // the AVMA table at all in that case.
        var os = NewOs(resolvedAvmaKey: "WOULD-HAVE-BEEN-PICKED");
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                License = new LicenseConfig { SetAvma = false },
            }),
            NewContext(os),
            CancellationToken.None);

        await os.DidNotReceive().ResolveVolumeActivationKeyAsync(
            VolumeActivationKeyType.Avma, Arg.Any<CancellationToken>());
        await os.DidNotReceive().ApplyLicenseAsync(
            Arg.Any<LicenseSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Explicit_product_key_wins_over_AVMA_default()
    {
        // Explicit beats auto-detect — even though set_avma is defaulted true.
        var os = NewOs(resolvedAvmaKey: "AVMA-WOULD-HAVE-WON");
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);
        var config = new CloudConfigModel
        {
            License = new LicenseConfig { ProductKey = "EXPLICIT-KEY-AAAAA-BBBBB-CCCCC" },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            NewContext(os),
            CancellationToken.None);

        await os.Received(1).ApplyLicenseAsync(
            Arg.Is<LicenseSpec>(s => s.ProductKey == "EXPLICIT-KEY-AAAAA-BBBBB-CCCCC"),
            Arg.Any<CancellationToken>());
        // With an explicit key in hand the auto-resolver must not be invoked.
        await os.DidNotReceive().ResolveVolumeActivationKeyAsync(
            Arg.Any<VolumeActivationKeyType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Explicit_set_avma_true_with_no_key_in_table_fails_loudly()
    {
        // When the operator EXPLICITLY opts in to AVMA and the table has no
        // key for their edition (Server 2025 corner cases, exotic SKUs),
        // we surface a Failed outcome so the operator gets feedback.
        var os = NewOs(resolvedAvmaKey: null);
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                License = new LicenseConfig { SetAvma = true },
            }),
            NewContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Failed>()
            .Which.Reason.Should().ContainEquivalentOf("AVMA");
    }

    [Fact]
    public async Task Default_set_avma_with_no_key_does_NOT_fail()
    {
        // Distinguishes from the test above: defaulted (null in config) AVMA
        // returning null must be silent — most guests have no AVMA key in
        // their LicenseFamily and we don't want to spam Failed outcomes.
        var os = NewOs(resolvedAvmaKey: null);
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
    }

    [Fact]
    public async Task Set_kms_explicit_with_no_host_resolves_key_and_clears_kms_host()
    {
        // set_kms: true with no kms_host means "install the generic KMS
        // client key AND let DNS SRV discovery find a KMS host".
        var os = NewOs(resolvedKmsKey: "RESOLVED-KMS-KEY");
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                License = new LicenseConfig { SetKms = true, SetAvma = false },
            }),
            NewContext(os),
            CancellationToken.None);

        await os.Received(1).ApplyLicenseAsync(
            Arg.Is<LicenseSpec>(s =>
                s.ProductKey == "RESOLVED-KMS-KEY"
                && s.KmsHost == null
                && s.ClearKmsHost),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AVMA_takes_precedence_over_KMS_when_both_resolve()
    {
        // The cbi flow prefers AVMA when available; we mirror that ordering.
        // Only the AVMA key should be installed, not the KMS one.
        var os = NewOs(resolvedAvmaKey: "PICKED-AVMA", resolvedKmsKey: "WOULD-HAVE-PICKED-KMS");
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                License = new LicenseConfig { SetAvma = true, SetKms = true },
            }),
            NewContext(os),
            CancellationToken.None);

        await os.Received(1).ApplyLicenseAsync(
            Arg.Is<LicenseSpec>(s => s.ProductKey == "PICKED-AVMA"),
            Arg.Any<CancellationToken>());
        // KMS resolver MUST NOT have been consulted once AVMA succeeded.
        await os.DidNotReceive().ResolveVolumeActivationKeyAsync(
            VolumeActivationKeyType.Kms, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_datasource_skips_activation_but_still_rearms_evaluations()
    {
        // The Azure skip is the activation-path skip (slmgr /ipk, /skms,
        // /ato). Rearm has nothing to do with activation — it extends an
        // evaluation grace period that Azure does NOT manage. So rearm
        // must still happen on Azure when the guest is an evaluation.
        var os = NewOs(resolvedAvmaKey: "WOULD-HAVE-PICKED", isEvaluation: true);
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os, cloudName: "azure"),
            CancellationToken.None);

        // Activation path skipped.
        await os.DidNotReceive().ApplyLicenseAsync(
            Arg.Any<LicenseSpec>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().ResolveVolumeActivationKeyAsync(
            Arg.Any<VolumeActivationKeyType>(), Arg.Any<CancellationToken>());

        // Rearm path RAN.
        await os.Received(1).RearmLicenseAsync(Arg.Any<CancellationToken>());
        result.Should().BeOfType<ModuleOutcome.RebootRequested>();
    }

    [Fact]
    public async Task Force_true_overrides_the_Azure_activation_skip()
    {
        // Hybrid scenario: operator wants a corporate KMS host on an Azure
        // VM (e.g. licensing audit, regulatory) and uses force to bypass
        // the platform skip.
        var os = NewOs();
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);
        var config = new CloudConfigModel
        {
            License = new LicenseConfig
            {
                KmsHost = "internal-kms.corp.example.com:1688",
                Force = true,
                SetAvma = false,  // we only want kms_host applied
            },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            NewContext(os, cloudName: "azure"),
            CancellationToken.None);

        await os.Received(1).ApplyLicenseAsync(
            Arg.Is<LicenseSpec>(s => s.KmsHost == "internal-kms.corp.example.com:1688"),
            Arg.Any<CancellationToken>());
    }

    // Regression: the Azure detection MUST gate on PlatformMetadata.CloudName,
    // not on SourceName. SourceName is diagnostic; CloudName is the contract.
    // This guards against a regression to string-coupling: if a future
    // refactor renames the source ("AzureCustomData", "AzureIMDS") the
    // licensing module must still detect Azure via the structured metadata.
    [Fact]
    public async Task Azure_detection_uses_PlatformMetadata_CloudName_not_SourceName()
    {
        // SourceName says "Azure" but the structured metadata declares no
        // cloud — module must NOT skip activation.
        var os = NewOs(resolvedAvmaKey: "PICK-ME");
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os, sourceName: "Azure"),
            CancellationToken.None);

        // Activation ran because cloud-name is not "azure".
        await os.Received(1).ApplyLicenseAsync(
            Arg.Is<LicenseSpec>(s => s.ProductKey == "PICK-ME"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_detection_triggers_on_CloudName_even_with_unrelated_SourceName()
    {
        // SourceName is something else but cloud_name == "azure" — module
        // must take the Azure path.
        var os = NewOs(resolvedAvmaKey: "WOULD-HAVE-PICKED");
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os, sourceName: "SomeOther", cloudName: "azure"),
            CancellationToken.None);

        // Activation path skipped under Azure.
        await os.DidNotReceive().ApplyLicenseAsync(
            Arg.Any<LicenseSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_failure_does_not_run_rearm()
    {
        // If slmgr /ipk fails we must not attempt /rearm — rearm against a
        // broken-licensing state would produce confusing errors AND burn an
        // evaluation rearm slot (Microsoft documents a hard limit).
        var os = NewOs(isEvaluation: true);
        os.ApplyLicenseAsync(Arg.Any<LicenseSpec>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("slmgr 0xC004F050"));
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                License = new LicenseConfig { ProductKey = "BAD" },
            }),
            NewContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().RearmLicenseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Eval_probe_failure_does_not_fail_the_module()
    {
        // IsEvaluationLicenseAsync hitting a CIM exception should NOT fail
        // the entire licensing module — rearm is best-effort by design.
        var os = NewOs();
        os.IsEvaluationLicenseAsync(Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("WMI down"));
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().RearmLicenseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rearm_no_reboot_returns_Completed()
    {
        // Future-proof: if the OS ever reports rearm not requiring reboot,
        // we should return Completed (not RebootRequested).
        var os = NewOs(isEvaluation: true, rebootAfterRearm: false);
        var module = new LicensingModule(NullLogger<LicensingModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            NewContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).RearmLicenseAsync(Arg.Any<CancellationToken>());
    }
}
