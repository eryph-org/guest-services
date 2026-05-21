namespace Eryph.GuestServices.Provisioning.UserData;

public interface IUserDataPipeline
{
    Task<ResolvedUserData> ResolveAsync(byte[]? rawUserData, CancellationToken cancellationToken);
}
