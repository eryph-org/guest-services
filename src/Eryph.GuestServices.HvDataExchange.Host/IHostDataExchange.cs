namespace Eryph.GuestServices.HvDataExchange.Host;

public interface IHostDataExchange
{
    /// <summary>
    /// Gets the guest data of the virtual machine with the
    /// given <paramref name="vmId"/>.
    /// </summary>
    /// <remarks>
    /// This data is stored in <c>Virtual Machine\Guest</c> pool
    /// of the Hyper-V data exchange.
    /// </remarks>
    public IReadOnlyDictionary<string, string> GetGuestData(Guid vmId);
}
