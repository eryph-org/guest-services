namespace Eryph.GuestServices.HvDataExchange.Guest;

public interface IGuestDataExchange
{
    /// <summary>
    /// Gets the guest data of this virtual machine.
    /// </summary>
    /// <remarks>
    /// This data is stored in <c>Virtual Machine\Guest</c> pool
    /// of the Hyper-V data exchange.
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> GetGuestData();

    /// <summary>
    /// Set the given <paramref name="values"/> in the guest data
    /// of this virtual machine. When the value is <see langword="null"/>,
    /// the key is removed from the guest data.
    /// </summary>
    /// <remarks>
    /// This data is stored in <c>Virtual Machine\Guest</c> pool
    /// of the Hyper-V data exchange.
    /// </remarks>
    Task SetGuestValues(IReadOnlyDictionary<string, string?> values);
}
