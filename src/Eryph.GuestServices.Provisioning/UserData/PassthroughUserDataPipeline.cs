using Eryph.GuestServices.Provisioning.Serialization;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.UserData;

// Default impl. Agent Z replaces with full recursive pipeline (multipart MIME,
// include URL, jinja2 deferred, etc.). This passthrough parses the raw bytes
// as a single #cloud-config YAML document and returns it with empty Scripts.
internal sealed class PassthroughUserDataPipeline(ICloudConfigSerializer serializer) : IUserDataPipeline
{
    public Task<ResolvedUserData> ResolveAsync(byte[]? rawUserData, CancellationToken cancellationToken)
    {
        if (rawUserData is null || rawUserData.Length == 0)
            return Task.FromResult(ResolvedUserData.Empty(new CloudConfigModel()));

        var yaml = System.Text.Encoding.UTF8.GetString(rawUserData);
        var cloud = serializer.Deserialize(yaml);
        return Task.FromResult(ResolvedUserData.Empty(cloud));
    }
}
