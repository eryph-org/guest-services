namespace Eryph.GuestServices.Provisioning.Windows;

public enum SetComputerNameResult
{
    /// <summary>The current name already matches the requested name.</summary>
    AlreadySet,

    /// <summary>The name change was queued and will take effect after a reboot.</summary>
    SetWithRebootPending,
}
