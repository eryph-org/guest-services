using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.UserData;

// Lightweight test double that mirrors UserDataResolutionContext but without
// any pipeline dependency. Tests can plug in a custom nestedDispatcher to
// observe recursion or to forward through a real chain.
internal sealed class TestResolutionContext : IUserDataResolutionContext
{
    private readonly Func<UserDataPart, IUserDataResolutionContext, CancellationToken, Task>? _nested;
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);

    public TestResolutionContext(
        Func<UserDataPart, IUserDataResolutionContext, CancellationToken, Task>? nestedDispatcher = null)
    {
        _nested = nestedDispatcher;
    }

    public CloudConfigModel CloudConfig { get; private set; } = new();

    public List<ScriptPayload> Scripts { get; } = [];

    public List<BoothookPayload> Boothooks { get; } = [];

    public List<UserDataPart> NestedParts { get; } = [];

    public void MergeCloudConfig(CloudConfigModel fragment) =>
        MergeCloudConfig(fragment, CloudInitMergeOptions.CloudInitDefault);

    public void MergeCloudConfig(CloudConfigModel fragment, CloudInitMergeOptions options)
    {
        CloudConfig = CloudConfigMerge.Merge(CloudConfig, fragment, options);
    }

    public void AddScript(ScriptPayload script) => Scripts.Add(script);

    public void AddBoothook(BoothookPayload boothook) => Boothooks.Add(boothook);

    public Task ProcessNestedAsync(UserDataPart nested, CancellationToken cancellationToken)
    {
        NestedParts.Add(nested);
        return _nested?.Invoke(nested, this, cancellationToken) ?? Task.CompletedTask;
    }

    public bool TryMarkVisited(string url) => _visited.Add(url);
}

internal static class CloudConfigMergeProxy
{
    // CloudConfigMerge.Merge is public on the model assembly now (Phase 1
    // moved the partial there for the source-gen split). This wrapper stays
    // so test sites can keep the call site flat regardless of namespace.
    public static CloudConfigModel Merge(CloudConfigModel left, CloudConfigModel right) =>
        CloudConfigMerge.Merge(left, right);
}
