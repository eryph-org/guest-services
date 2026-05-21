using Eryph.GuestServices.Provisioning.UserData;

namespace Eryph.GuestServices.Provisioning.Modules;

public interface IModule
{
    Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken);
}
