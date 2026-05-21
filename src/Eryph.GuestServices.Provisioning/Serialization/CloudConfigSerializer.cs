using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.Provisioning.Serialization;

public sealed class CloudConfigSerializer : ICloudConfigSerializer
{
    public global::Eryph.GuestServices.CloudConfig.CloudConfig Deserialize(string yaml) =>
        CloudConfigYamlSerializer.Deserialize(yaml);
}
