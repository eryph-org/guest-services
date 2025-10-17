namespace Eryph.GuestServices.Core;

public static class Constants
{
    /// <summary>
    /// The Hyper-V integration service ID for the eryph guest services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This ID must be registered on the VM host before the eryph guest services can be used.
    /// </para>
    /// <para>
    /// This ID corresponds to the port 5002 for Linux VSock sockets.
    /// </para>
    /// </remarks>
    public static readonly Guid ServiceId = Guid.Parse("0000138a-facb-11e6-bd58-64006a7986d3");

    public static readonly string ServiceName = "Eryph Guest Services";

    public static readonly string OperatingSystemKey = "eryph:guest-services:operating-system";

    public static readonly string StatusKey = "eryph:guest-services:status";

    public static readonly string VersionKey = "eryph:guest-services:version";

    public static readonly string ClientAuthKey = "eryph:guest-services:client-public-key";
}
