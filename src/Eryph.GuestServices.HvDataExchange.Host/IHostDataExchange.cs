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
    Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync(Guid vmId);

    /// <summary>
    /// Gets the external data of the virtual machine with the
    /// given <paramref name="vmId"/>.
    /// </summary>
    /// <remarks>
    /// This data is stored in <c>Virtual Machine\External</c> pool
    /// of the Hyper-V data exchange.
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync(Guid vmId);

    /// <summary>
    /// Sets the external data of the virtual machine with the
    /// given <paramref name="vmId"/>. When the value is <see langword="null"/>,
    /// the key is removed from the guest data.
    /// </summary>
    /// <remarks>
    /// This data is stored in <c>Virtual Machine\External</c> pool
    /// of the Hyper-V data exchange.
    /// </remarks>
    Task SetExternalValuesAsync(Guid vmId, IReadOnlyDictionary<string, string?> values);
}
