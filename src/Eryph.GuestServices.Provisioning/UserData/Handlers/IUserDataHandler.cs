using Eryph.GuestServices.CloudConfig;

namespace Eryph.GuestServices.Provisioning.UserData.Handlers;

// One handler per cloud-init content-type. The pipeline walks parts
// (root part + everything yielded by multipart/include) and dispatches
// each part to the first handler whose CanHandle returns true.
public interface IUserDataHandler
{
    bool CanHandle(UserDataPart part);

    Task ProcessAsync(UserDataPart part, IUserDataResolutionContext ctx, CancellationToken cancellationToken);
}

public sealed record UserDataPart(
    string ContentType,
    byte[] Body,
    string? Filename,
    IReadOnlyDictionary<string, string>? Headers = null);

// Mutable accumulator surfaced to handlers. Handlers merge cloud-config
// fragments and append script/boothook payloads; multipart and include
// handlers re-enter the pipeline via ProcessNestedAsync.
public interface IUserDataResolutionContext
{
    void MergeCloudConfig(CloudConfig.CloudConfig fragment);

    // Merge a fragment using its cloud-init merge_how / merge_type directive
    // (RFC 0032). The parameterless overload uses the cloud-init default.
    void MergeCloudConfig(CloudConfig.CloudConfig fragment, CloudInitMergeOptions options);

    void AddScript(ScriptPayload script);

    void AddBoothook(BoothookPayload boothook);

    Task ProcessNestedAsync(UserDataPart nested, CancellationToken cancellationToken);

    // Returns true if the URL had not been visited yet; cycles in #include
    // chains are detected by the pipeline using this hook.
    bool TryMarkVisited(string url);
}
