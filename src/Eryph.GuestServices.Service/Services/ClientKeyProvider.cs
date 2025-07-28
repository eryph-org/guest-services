using Eryph.GuestServices.Core;
using Eryph.GuestServices.HvDataExchange.Guest;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

public class ClientKeyProvider(
    IKeyStorage keyStorage,
    IGuestDataExchange dataExchange,
    ILogger<ClientKeyProvider> logger)
    : IClientKeyProvider
{
    public async Task<IKeyPair?> GetClientKey()
    {
        var clientKey = await keyStorage.GetClientKeyAsync();
        if (clientKey is not null)
            return clientKey;

        logger.LogInformation("Client key not found. Trying to pull the key from the Hyper-V data exchange.");
        
        var guestData = await dataExchange.GetExternalDataAsync();
        if (!guestData.TryGetValue(Constants.ClientAuthKey, out var clientKeyData))
            return null;

        if (string.IsNullOrEmpty(clientKeyData))
            return null;

        var keyPair = ParseKey(clientKeyData);
        if (keyPair is null)
            return null;

        logger.LogInformation("Pulled client key from the Hyper-V data exchange. Saving the key.");
        
        await keyStorage.SetClientKeyAsync(keyPair);

        return keyPair;
    }

    private IKeyPair? ParseKey(string keyData)
    {
        try
        {
            return KeyPair.ImportKey(keyData);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The provided key could not be parsed");
            return null;
        }
    }
}
