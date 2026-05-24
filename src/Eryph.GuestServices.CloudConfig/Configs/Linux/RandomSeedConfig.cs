namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_seed_random</c> — seed the kernel RNG from cloud-config.
/// Linux-only; on Windows the OS handles entropy via boot-time providers.
/// </summary>
[CloudInitRecord]
public sealed record RandomSeedConfig
{
    /// <summary>Path of the device the seed is written to (default <c>/dev/urandom</c>).</summary>
    public string? File { get; init; }

    /// <summary>Seed payload (raw or encoded per <see cref="Encoding"/>).</summary>
    public string? Data { get; init; }

    /// <summary>Encoding of <see cref="Data"/> — <c>raw</c>, <c>base64</c>, <c>b64</c>, or <c>gzip</c>.</summary>
    public string? Encoding { get; init; }

    /// <summary>Command to run after seeding (e.g. <c>pollinate</c>).</summary>
    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>When true, cloud-init aborts if <see cref="Command"/> fails.</summary>
    public bool? CommandRequired { get; init; }
}
