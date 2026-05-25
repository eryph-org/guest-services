namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class DriveInfoVolumeProbe : IVolumeProbe
{
    public IEnumerable<MountedVolume> EnumerateVolumes()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;

            string label;
            try
            {
                label = drive.VolumeLabel ?? string.Empty;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            yield return new MountedVolume(label, drive.RootDirectory.FullName);
        }
    }
}
