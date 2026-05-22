using Eryph.GuestServices.Provisioning.Modules;

namespace Eryph.GuestServices.Provisioning.Hosting;

/// <summary>
/// Explicit compile-time list of every <see cref="IModule"/> implementation
/// in this assembly. Trim-safe because every entry is a rooted
/// <c>typeof(T)</c> reference — no reflection over the assembly is needed.
/// </summary>
internal static class ModuleRegistry
{
    public static IReadOnlyList<Type> ModuleTypes { get; } =
    [
        typeof(ApplyNetworkConfigModule),
        typeof(RuncmdModule),
        typeof(ScriptsUserModule),
        typeof(SetHostnameModule),
        typeof(SetPasswordsModule),
        typeof(SshAuthorizedKeysModule),
        typeof(UsersGroupsModule),
        typeof(WriteFilesModule),
    ];
}
