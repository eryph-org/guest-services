namespace Eryph.GuestServices.CloudConfig;

[Flags]
public enum CloudInitPlatforms
{
    None = 0,
    Linux = 1 << 0,
    Windows = 1 << 1,
    All = Linux | Windows,
}
