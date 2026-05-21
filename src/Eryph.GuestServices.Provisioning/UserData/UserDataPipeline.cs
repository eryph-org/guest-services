using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Serialization;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.UserData;

// Recursive cloud-init user-data resolver. Sniffs the raw bytes, builds a
// root UserDataPart, and dispatches it through the handler chain. Handlers
// (multipart, include URL) recurse via IUserDataResolutionContext.ProcessNestedAsync.
// Once the recursion settles, the merged cloud-config, captured scripts, and
// captured boothooks are returned as a ResolvedUserData.
internal sealed class UserDataPipeline : IUserDataPipeline
{
    private readonly ILogger<UserDataPipeline> _logger;
    private readonly IReadOnlyList<IUserDataHandler> _handlers;
    private readonly int _maxRecursionDepth;

    public UserDataPipeline(
        IEnumerable<IUserDataHandler> handlers,
        ICloudConfigSerializer serializer,
        IUrlHelper urlHelper,
        ILogger<UserDataPipeline> logger,
        ProvisioningSettings? settings = null)
    {
        _logger = logger;
        _handlers = handlers.ToArray();
        Serializer = serializer;
        UrlHelper = urlHelper;
        _maxRecursionDepth = Math.Max(1, (settings ?? new ProvisioningSettings()).UserData.MaxRecursionDepth);
    }

    // The serializer + urlHelper dependencies are required by the task spec on the
    // pipeline's constructor signature even though the handlers consume them
    // directly via DI. Holding them as fields makes the dependency explicit and
    // gives downstream extensions a sanctioned hook.
    public ICloudConfigSerializer Serializer { get; }

    public IUrlHelper UrlHelper { get; }

    public async Task<ResolvedUserData> ResolveAsync(byte[]? rawUserData, CancellationToken cancellationToken)
    {
        if (rawUserData is null || rawUserData.Length == 0)
            return ResolvedUserData.Empty(new CloudConfigModel());

        // Gzipped userdata is a real-world thing (NoCloud sometimes ships
        // user-data.gz). Decompress upfront before sniffing.
        var bytes = UserDataContentTypeSniffer.DecompressIfGzipped(rawUserData);

        var contentType = UserDataContentTypeSniffer.Sniff(bytes);
        if (contentType == UserDataContentTypeSniffer.PlainText)
        {
            _logger.LogWarning(
                "Root user-data does not start with a recognised cloud-init marker; ignoring");
            return ResolvedUserData.Empty(new CloudConfigModel());
        }

        var context = new UserDataResolutionContext(DispatchAsync);
        var rootPart = new UserDataPart(contentType, bytes, Filename: null);

        await DispatchAsync(rootPart, context, cancellationToken).ConfigureAwait(false);

        return context.ToResolvedUserData();
    }

    private async Task DispatchAsync(
        UserDataPart part,
        IUserDataResolutionContext ctx,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var concrete = (UserDataResolutionContext)ctx;
        if (concrete.CurrentDepth >= _maxRecursionDepth)
        {
            _logger.LogWarning(
                "User-data recursion depth hit the limit of {Limit}; skipping nested part of type '{ContentType}'",
                _maxRecursionDepth,
                part.ContentType);
            return;
        }

        concrete.CurrentDepth++;
        try
        {
            var handler = _handlers.FirstOrDefault(h => h.CanHandle(part));
            if (handler is null)
            {
                _logger.LogWarning(
                    "No handler accepted user-data part of type '{ContentType}' (filename='{Filename}'); ignoring",
                    part.ContentType,
                    part.Filename ?? "<root>");
                return;
            }

            _logger.LogDebug(
                "Dispatching user-data part '{ContentType}' (depth={Depth}) to {Handler}",
                part.ContentType,
                concrete.CurrentDepth,
                handler.GetType().Name);

            await handler.ProcessAsync(part, ctx, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            concrete.CurrentDepth--;
        }
    }
}
