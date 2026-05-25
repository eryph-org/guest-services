using System.Xml.Linq;

namespace Eryph.GuestServices.Provisioning.DataSources.Azure;

/// <summary>
/// Parses Azure ConfigDrive <c>ovf-env.xml</c>. Public XML namespace is
/// <c>http://schemas.microsoft.com/windowsazure</c>. We extract only the fields
/// v1 actually consumes (HostName and CustomData); other elements are noted in
/// RFC 0014 for later phases. Robust to either provisioning-configuration-set
/// variant — Linux and Windows ship the same wrapping element shape.
/// </summary>
internal static class AzureOvfEnvParser
{
    internal static readonly XNamespace Wa = "http://schemas.microsoft.com/windowsazure";

    public static AzureOvfEnv Parse(string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(xml);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("ovf-env.xml is not well-formed XML.", ex);
        }

        // The root is <Environment xmlns="...">; ProvisioningSection sits one
        // level down. We search by local name + the Azure namespace to be
        // resilient to varying prefix declarations.
        var provisioning = doc
            .Descendants(Wa + "ProvisioningSection")
            .FirstOrDefault();

        if (provisioning is null)
            return new AzureOvfEnv();

        // Try Linux first then Windows. Both elements are siblings inside
        // ProvisioningSection in real-world Azure environments.
        var configSet =
            provisioning.Element(Wa + "LinuxProvisioningConfigurationSet")
            ?? provisioning.Element(Wa + "WindowsProvisioningConfigurationSet");

        if (configSet is null)
            return new AzureOvfEnv();

        // Linux uses <HostName>, Windows uses <ComputerName>. Try both.
        var hostname =
            (string?)configSet.Element(Wa + "HostName")
            ?? (string?)configSet.Element(Wa + "ComputerName");

        var customData = (string?)configSet.Element(Wa + "CustomData");

        return new AzureOvfEnv
        {
            Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname.Trim(),
            CustomDataBase64 = string.IsNullOrWhiteSpace(customData) ? null : customData.Trim(),
        };
    }
}

internal sealed record AzureOvfEnv
{
    public string? Hostname { get; init; }

    /// <summary>
    /// Raw base64 CustomData string as embedded in the XML. PA base64-decodes
    /// it once before writing <c>C:\AzureData\CustomData.bin</c>, so the
    /// canonical post-PA source is the file, not this property. The XML form
    /// is kept for the rare case where the ConfigDrive is still mounted when
    /// our agent reads it. CustomData is NOT encrypted at any layer — the
    /// <c>&lt;CertificateThumbprint&gt;</c> element under <c>&lt;UserPassword&gt;</c>
    /// in the same configset wraps AdminPassword, not CustomData (verified
    /// against cloud-init / WALinuxAgent / cloudbase-init in
    /// docs/research/azure-customdata-encryption.md).
    /// </summary>
    public string? CustomDataBase64 { get; init; }
}
