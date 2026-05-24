namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Cloud-init compatible <c>ntp</c> directive. Mirrors the cross-platform
/// subset of cloud-init's <c>cc_ntp</c> schema — Windows-irrelevant fields
/// (<c>config</c>, <c>ntp_client</c>, <c>peers</c>, <c>allow</c>) are
/// intentionally absent; only <c>enabled</c>, <c>servers</c> and
/// <c>pools</c> have meaningful Windows analogues.
/// </summary>
public sealed record NtpConfig
{
    /// <summary>
    /// When false, the Windows Time service (<c>w32time</c>) is stopped and
    /// set to <c>Disabled</c>. Default is true.
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// Explicit NTP server hostnames or IPs. Combined with <see cref="Pools"/>
    /// into a single <c>w32tm /config /manualpeerlist:</c> argument.
    /// </summary>
    public IReadOnlyList<string>? Servers { get; init; }

    /// <summary>
    /// NTP pool entries (e.g. <c>pool.ntp.org</c>). On Windows there is no
    /// distinction between pools and servers — both end up in the
    /// <c>w32tm</c> manual peer list.
    /// </summary>
    public IReadOnlyList<string>? Pools { get; init; }

    /// <summary>
    /// When set, writes
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation\RealTimeIsUniversal</c>
    /// to instruct Windows whether to treat the RTC as UTC. Mirrors
    /// cloudbase-init's <c>real_time_clock_utc</c> option. Defaults to null
    /// — the registry is left alone, so existing Windows defaults apply.
    /// </summary>
    public bool? RealTimeClockUtc { get; init; }
}
