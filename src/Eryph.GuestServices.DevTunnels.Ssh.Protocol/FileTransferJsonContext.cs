using System.Text.Json.Serialization;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<RemoteFileInfo>))]
public partial class FileTransferJsonContext : JsonSerializerContext;
