namespace Eryph.GuestServices.HvDataExchange.Host;

public interface IHostDataExchange
{
    /// <summary>
    /// The data from the pool Virtual Machine\Guest.
    /// </summary>
    /// <param name="vmId"></param>
    /// <returns></returns>
    public IReadOnlyDictionary<string, string> GetGuestData(Guid vmId);
}
