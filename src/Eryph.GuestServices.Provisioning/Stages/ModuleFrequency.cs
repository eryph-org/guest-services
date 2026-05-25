namespace Eryph.GuestServices.Provisioning.Stages;

/// <summary>
/// How often a module is allowed to run, mirroring cloud-init's three
/// frequencies. Stored as a semaphore on disk; <see cref="Semaphores.ISemaphoreStore"/>
/// gates execution before <c>IModule.ApplyAsync</c> is invoked.
/// </summary>
public enum ModuleFrequency
{
    /// <summary>
    /// Runs once per instance-id. The common case: configuration that survives
    /// reboots but should re-apply if the instance is redeployed (instance-id
    /// changes). Semaphore lives at
    /// <c>%ProgramData%\eryph\provisioning\instance\&lt;instance-id&gt;\sem\&lt;module&gt;.per-instance</c>.
    /// </summary>
    PerInstance = 0,

    /// <summary>
    /// Runs every boot. Semaphore lives at
    /// <c>%ProgramData%\eryph\provisioning\sem\&lt;module&gt;.per-boot</c>
    /// and is cleared at the start of every new boot session.
    /// </summary>
    PerBoot = 1,

    /// <summary>
    /// Runs exactly once on this filesystem, regardless of instance-id changes.
    /// Semaphore lives at
    /// <c>%ProgramData%\eryph\provisioning\sem\&lt;module&gt;.per-once</c>
    /// and is never cleared automatically. Use for one-shot setup that must
    /// survive redeploys (e.g. seeding a host-only secret).
    /// </summary>
    PerOnce = 2,
}
