using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.UserData;

// Per-resolution mutable state. A fresh instance is created by the
// pipeline for every ResolveAsync call and torn down when the call
// completes. Re-entrant ProcessNestedAsync is dispatched back through
// the pipeline via the nestedDispatcher callback so the multipart/include
// handlers don't need a reference to the pipeline itself.
internal sealed class UserDataResolutionContext(
    Func<UserDataPart, IUserDataResolutionContext, CancellationToken, Task> nestedDispatcher)
    : IUserDataResolutionContext
{
    private readonly List<ScriptPayload> _scripts = [];
    private readonly List<BoothookPayload> _boothooks = [];
    private readonly HashSet<string> _visitedUrls = new(StringComparer.OrdinalIgnoreCase);

    // Bumped by the pipeline before dispatching a part and decremented when
    // the dispatch returns. Stored on the context so the lambda passed to
    // nestedDispatcher does not need to close over a ref local.
    public int CurrentDepth { get; set; }

    public CloudConfigModel CloudConfig { get; private set; } = new();

    public IReadOnlyList<ScriptPayload> Scripts => _scripts;

    public IReadOnlyList<BoothookPayload> Boothooks => _boothooks;

    public void MergeCloudConfig(CloudConfigModel fragment) =>
        MergeCloudConfig(fragment, CloudInitMergeOptions.CloudInitDefault);

    public void MergeCloudConfig(CloudConfigModel fragment, CloudInitMergeOptions options)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(options);
        CloudConfig = CloudConfigMerge.Merge(CloudConfig, fragment, options);
    }

    public void AddScript(ScriptPayload script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _scripts.Add(script);
    }

    public void AddBoothook(BoothookPayload boothook)
    {
        ArgumentNullException.ThrowIfNull(boothook);
        _boothooks.Add(boothook);
    }

    public Task ProcessNestedAsync(UserDataPart nested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nested);
        return nestedDispatcher(nested, this, cancellationToken);
    }

    public bool TryMarkVisited(string url) => _visitedUrls.Add(url);

    public ResolvedUserData ToResolvedUserData() => new()
    {
        CloudConfig = CloudConfig,
        Scripts = _scripts.ToArray(),
        Boothooks = _boothooks.ToArray(),
    };
}
