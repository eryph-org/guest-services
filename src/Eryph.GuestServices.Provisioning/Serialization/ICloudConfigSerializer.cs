namespace Eryph.GuestServices.Provisioning.Serialization;

public interface ICloudConfigSerializer
{
    global::Eryph.GuestServices.CloudConfig.CloudConfig Deserialize(string yaml);
}
