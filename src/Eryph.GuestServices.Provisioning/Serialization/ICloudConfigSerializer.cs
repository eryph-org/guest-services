namespace Eryph.GuestServices.Provisioning.Serialization;

public interface ICloudConfigSerializer
{
    global::Eryph.GuestServices.CloudConfig.CloudConfig Deserialize(string yaml);

    // The fragment's cloud-init merge_how / merge_type directive (RFC 0032),
    // or null when it carries none (caller uses the cloud-init default).
    global::Eryph.GuestServices.CloudConfig.CloudInitMergeOptions? ReadMergeOptions(string yaml);
}
