using System.Reflection;

namespace Eryph.GuestServices.Provisioning.Update;

/// <summary>Supplies the running agent's version, for the self-update compare.</summary>
public interface IAgentVersionProvider
{
    /// <summary>
    /// The running version as a SemVer core+prerelease string (build metadata
    /// stripped), e.g. <c>"0.4.0"</c> or <c>"0.4.0-preview.5"</c>.
    /// </summary>
    string GetCurrentVersion();
}

/// <summary>
/// Reads the entry assembly's <see cref="AssemblyInformationalVersionAttribute"/>
/// (egs-service.exe) — the same value <c>egs-service version</c> prints — and
/// normalises it to the index's version-key form.
/// </summary>
public sealed class AgentVersionProvider : IAgentVersionProvider
{
    public string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
            return UpdateTargetResolver.StripBuildMetadata(info);

        // Fall back to the assembly version (e.g. 0.4.0.0 -> 0.4.0) when no
        // informational version was baked in.
        var v = assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
