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
        typeof(EgsModule),
        typeof(GrowpartModule),
        typeof(LicensingModule),
        typeof(NtpClientModule),
        typeof(PowerStateModule),
        typeof(RuncmdModule),
        typeof(ScriptsUserModule),
        typeof(SetHostnameModule),
        typeof(SetLocaleModule),
        typeof(SetPasswordsModule),
        typeof(SshModule),
        typeof(TimezoneModule),
        typeof(UsersGroupsModule),
        typeof(WriteFilesModule),
        typeof(WriteFilesDeferredModule),
    ];
}
