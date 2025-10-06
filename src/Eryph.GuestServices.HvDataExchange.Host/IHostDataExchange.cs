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
    /// Gets the intrinsic guest data of the virtual machine with the
    /// given <paramref name="vmId"/>.
    /// </summary>
    /// <remarks>
    /// This data is stored in <c>Virtual Machine\Auto</c> pool
    /// of the Hyper-V data exchange. The data is automatically
    /// populated by the driver or kernel module which is responsible
    /// for the Hyper-V integration in the guest.
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> GetIntrinsicGuestDataAsync(Guid vmId);

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
    /// Gets the host-only data of the virtual machine with the
    /// given <paramref name="vmId"/>.
    /// </summary>
    /// <remarks>
    /// This data is in the configuration of the Hyper-V VM but is not
    /// pushed to the guest.
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> GetHostOnlyDataAsync(Guid vmId);

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

    /// <summary>
    /// Sets the host-only data of the virtual machine with the
    /// given <paramref name="vmId"/>. When the value is <see langword="null"/>,
    /// the key is removed from the host-only data.
    /// </summary>
    /// <remarks>
    /// This data is stored in the configuration of the Hyper-V VM but is not
    /// pushed to the guest.
    /// </remarks>
    Task SetHostOnlyValuesAsync(Guid vmId, IReadOnlyDictionary<string, string?> values);
}
