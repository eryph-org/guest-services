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
        var certThumbprint = (string?)configSet.Element(Wa + "CustomDataCertificateThumbprint");

        return new AzureOvfEnv
        {
            Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname.Trim(),
            CustomDataBase64 = string.IsNullOrWhiteSpace(customData) ? null : customData.Trim(),
            CustomDataCertificateThumbprint =
                string.IsNullOrWhiteSpace(certThumbprint) ? null : certThumbprint.Trim(),
        };
    }
}

internal sealed record AzureOvfEnv
{
    public string? Hostname { get; init; }

    /// <summary>
    /// Raw base64 CustomData string as embedded in the XML. May be encrypted —
    /// see RFC 0015. v1 does NOT decode/decrypt this; the canonical
    /// post-PA source is <c>C:\AzureData\CustomData.bin</c> instead.
    /// </summary>
    public string? CustomDataBase64 { get; init; }

    /// <summary>
    /// When set, CustomData is encrypted with the matching certificate in
    /// <c>Cert:\LocalMachine\My</c>. Decryption is deferred to RFC 0015.
    /// </summary>
    public string? CustomDataCertificateThumbprint { get; init; }
}
