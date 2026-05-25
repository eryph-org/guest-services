namespace Eryph.GuestServices.Provisioning.DataSources;

// Abstraction over DriveInfo so datasources can be unit-tested against temp directories.
public interface IVolumeProbe
{
    IEnumerable<MountedVolume> EnumerateVolumes();
}

public sealed record MountedVolume(string Label, string RootPath);
