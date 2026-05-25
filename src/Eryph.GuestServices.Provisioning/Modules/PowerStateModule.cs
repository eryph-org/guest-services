using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Validation;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Mirrors cloud-init's <c>cc_power_state_change</c>: at the END of
/// provisioning, optionally reboot / poweroff / hibernate after a
/// configured delay. Distinct from exit-1003 reboot-and-continue, which
/// is MID-stage; this module fires after every other Final module has
/// already done its work and recorded its semaphores.
/// </summary>
[Stage(Stage.Final, Order = int.MaxValue - 1, Frequency = ModuleFrequency.PerInstance)]
internal sealed class PowerStateModule(ILogger<PowerStateModule> logger) : IModule
{
    // Minimum buffer between scheduling the shutdown and actual power-down.
    // The StageRunner still needs to finish writing the per-instance
    // semaphore + emit the ProvisioningCompleted KVP after this module
    // returns; cutting it to /t 0 risks Windows tearing the process down
    // before either lands. Five seconds is short enough to not feel like
    // a wait, long enough to cover the StageRunner cleanup.
    private const int MinDelaySeconds = 5;

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var cfg = userData.CloudConfig.PowerState;
        if (cfg is null)
        {
            logger.LogDebug("No power_state directive; nothing to do.");
            return ModuleOutcome.Ok();
        }

        // Schema-level parsing lives in the model library so `egs-service
        // validate` catches malformed values BEFORE first boot. Re-running
        // it here is cheap and keeps the module honest if validation was
        // bypassed (older fodder, in-process tests).
        var parsedMode = PowerStateGrammar.ParseMode(cfg.Mode);
        if (parsedMode.IsFail)
            return ModuleOutcome.Fail(FormatErrors(parsedMode));
        var mode = parsedMode.SuccessToSeq().Head;

        if (mode == PowerStateGrammar.PowerStateMode.Halt)
        {
            // Cloud-init's `halt` doesn't translate cleanly — hibernate is
            // the closest Windows analogue. Surface that to operators
            // rather than surprising them with "halt = hibernate" silently.
            logger.LogWarning(
                "power_state: 'halt' has no clean Windows analogue; falling back to hibernate (shutdown.exe /h).");
        }

        var conditionResult = await EvaluateConditionAsync(cfg.Condition, context, cancellationToken).ConfigureAwait(false);
        if (!conditionResult)
        {
            logger.LogInformation("power_state: condition evaluated false; skipping power-state change.");
            return ModuleOutcome.Ok();
        }

        var parsedDelay = PowerStateGrammar.ParseDelay(cfg.Delay, DateTimeOffset.UtcNow);
        if (parsedDelay.IsFail)
            return ModuleOutcome.Fail(FormatErrors(parsedDelay));
        var delaySeconds = Math.Max(MinDelaySeconds, parsedDelay.SuccessToSeq().Head);

        var request = new PowerStateRequest
        {
            Action = MapAction(mode),
            DelaySeconds = delaySeconds,
            Message = cfg.Message,
        };

        try
        {
            await context.Os.RequestPowerStateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "power_state: shutdown.exe failed.");
            return ModuleOutcome.Fail($"power_state: {ex.Message}", ex);
        }

        // Return Ok — not RebootRequested — because the per-instance
        // semaphore must be written so the post-reboot run does NOT
        // re-enter the module. Cloud-init's intent: power_state is a
        // one-shot end-of-first-boot action. RebootRequested would loop.
        return ModuleOutcome.Ok();
    }

    private async Task<bool> EvaluateConditionAsync(
        BoolOrString condition,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        // The YAML layer routes plain (unquoted) YAML 1.1 bool tokens to
        // BoolOrString.FromBool and everything else (including quoted bool
        // tokens — operator quoted intentionally) to BoolOrString.FromString,
        // matching cloud-init's PyYAML-driven behaviour exactly. See
        // Yaml11ScalarResolver / BoolOrStringYamlConverter.
        if (condition.IsEmpty)
            return true;
        if (condition.IsBool)
            return condition.Bool!.Value;
        return await RunConditionCommandAsync(condition.String!, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RunConditionCommandAsync(
        string command,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
            return true;

        try
        {
            var result = await context.Os.RunShellCommandAsync(command, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "power_state: condition command threw; treating as 'skip'.");
            return false;
        }
    }

    private static PowerStateAction MapAction(PowerStateGrammar.PowerStateMode mode) => mode switch
    {
        PowerStateGrammar.PowerStateMode.Reboot => PowerStateAction.Reboot,
        PowerStateGrammar.PowerStateMode.Poweroff => PowerStateAction.Poweroff,
        PowerStateGrammar.PowerStateMode.Halt => PowerStateAction.Halt,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown PowerStateMode."),
    };

    private static string FormatErrors<T>(Validation<Error, T> validation) =>
        validation.FailToSeq()
            .Map(e => e.Message)
            .Aggregate(string.Empty, (acc, msg) =>
                string.IsNullOrEmpty(acc) ? msg : acc + " | " + msg);
}
