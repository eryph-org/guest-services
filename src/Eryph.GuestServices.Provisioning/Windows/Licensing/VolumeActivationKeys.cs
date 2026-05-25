namespace Eryph.GuestServices.Provisioning.Windows.Licensing;

/// <summary>
/// Volume-activation product key tables. Verified against Microsoft Learn:
/// <list type="bullet">
///   <item>AVMA — <c>learn.microsoft.com/en-us/windows-server/get-started/automatic-vm-activation</c></item>
///   <item>KMS — <c>learn.microsoft.com/en-us/windows-server/get-started/kms-client-activation-keys</c></item>
/// </list>
/// </summary>
/// <remarks>
/// These keys are <b>public</b>. Microsoft publishes them for the explicit
/// purpose of letting volume-license customers activate Windows guests.
/// They are not secrets and including them here is the same model
/// cloudbase-init has shipped since 2017.
/// </remarks>
internal static class VolumeActivationKeys
{
    public static string? Lookup(
        OsVersionFamily osFamily,
        string licenseFamily,
        VolumeActivationType type)
    {
        return type switch
        {
            VolumeActivationType.Avma => LookupAvma(osFamily, licenseFamily),
            VolumeActivationType.Kms => LookupKms(osFamily, licenseFamily),
            _ => null,
        };
    }

    private static string? LookupAvma(OsVersionFamily osFamily, string licenseFamily)
    {
        return osFamily switch
        {
            OsVersionFamily.WindowsServer2012R2 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "Y4TGP-NPTV9-HTC2H-7MGQ3-DV4TW",
                "ServerStandard" or "ServerStandardCore" => "DBGBW-NPF86-BJVTX-K3WKJ-MTB6V",
                "ServerSolution" or "ServerSolutionCore" => "K2XGM-NMBT3-2R6Q8-WF2FK-P36R2",
                _ => null,
            },
            OsVersionFamily.WindowsServer2016 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "TMJ3Y-NTRTM-FJYXT-T22BY-CWG3J",
                "ServerStandard" or "ServerStandardCore" => "C3RCX-M6NRP-6CXC9-TW2F2-4RHYD",
                "ServerSolution" or "ServerSolutionCore" => "B4YNW-62DX9-W8V6M-82649-MHBKQ",
                _ => null,
            },
            OsVersionFamily.WindowsServer2019 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "H3RNG-8C32Q-Q8FRX-6TDXV-WMBMW",
                "ServerStandard" or "ServerStandardCore" => "TNK62-RXVTB-4P47B-2D623-4GF74",
                "ServerSolution" or "ServerSolutionCore" => "2CTP7-NHT64-BP62M-FV6GG-HFV28",
                _ => null,
            },
            OsVersionFamily.WindowsServer2022 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "W3GNR-8DDXR-2TFRP-H8P33-DV9BG",
                "ServerStandard" or "ServerStandardCore" => "YDFWN-MJ9JR-3DYRK-FXXRW-78VHK",
                // Datacenter: Azure Edition (Server 2022). LicenseFamily as
                // reported by SoftwareLicensingProduct on those guests is
                // included under common observed names — operators on a SKU
                // that doesn't match can still use an explicit product_key.
                "ServerAzureEditionDatacenter" or "ServerDatacenterAzureEdition"
                    or "ServerAzureCor" or "ServerAzureCorCore" => "F7TB6-YKN8Y-FCC6R-KQ484-VMK3J",
                _ => null,
            },
            OsVersionFamily.WindowsServer2025 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "YQB4H-NKHHJ-Q6K4R-4VMY6-VCH67",
                "ServerStandard" or "ServerStandardCore" => "WWVGQ-PNHV9-B89P4-8GGM9-9HPQ4",
                "ServerAzureEditionDatacenter" or "ServerDatacenterAzureEdition"
                    or "ServerAzureCor" or "ServerAzureCorCore" => "6NMQ9-T38WF-6MFGM-QYGYM-88J4F",
                _ => null,
            },
            _ => null,
        };
    }

    private static string? LookupKms(OsVersionFamily osFamily, string licenseFamily)
    {
        return osFamily switch
        {
            OsVersionFamily.WindowsServer2012 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "48HP8-DN98B-MYWDG-T2DCC-8W83P",
                "ServerStandard" or "ServerStandardCore" => "XC9B7-NBPP2-83J2H-RHMBY-92BT4",
                _ => null,
            },
            OsVersionFamily.WindowsServer2012R2 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "W3GGN-FT8W3-Y4M27-J84CP-Q3VJ9",
                "ServerStandard" or "ServerStandardCore" => "D2N9P-3P6X9-2R39C-7RTCD-MDVJX",
                "ServerSolution" or "ServerSolutionCore" => "KNC87-3J2TX-XB4WP-VCPJV-M4FWM",
                _ => null,
            },
            OsVersionFamily.WindowsServer2016 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "CB7KF-BWN84-R7R2Y-793K2-8XDDG",
                "ServerStandard" or "ServerStandardCore" => "WC2BQ-8NRM3-FDDYY-2BFGV-KHKQY",
                "ServerSolution" or "ServerSolutionCore" => "JCKRF-N37P4-C2D82-9YXRT-4M63B",
                // Windows Server, version 1709 / 1803 ("Azure Core") shipped
                // with the same KMS key per cbi's productkeys.py.
                "ServerAzureCor" or "ServerAzureCorCore" => "VP34G-4NPPG-79JTQ-864T4-R3MQX",
                _ => null,
            },
            OsVersionFamily.WindowsServer2019 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "WMDGN-G9PQG-XVVXX-R3X43-63DFG",
                "ServerStandard" or "ServerStandardCore" => "N69G4-B89J2-4G8F4-WWYCC-J464C",
                "ServerSolution" or "ServerSolutionCore" => "WVDHN-86M7X-466P6-VHXV7-YY726",
                _ => null,
            },
            OsVersionFamily.WindowsServer2022 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "WX4NM-KYWYW-QJJR4-XV3QB-6VM33",
                "ServerStandard" or "ServerStandardCore" => "VDYBN-27WPP-V4HQT-9VMD4-VMK7H",
                "ServerAzureEditionDatacenter" or "ServerDatacenterAzureEdition"
                    or "ServerAzureCor" or "ServerAzureCorCore" => "NTBV8-9K7Q8-V27C6-M2BTV-KHMXV",
                _ => null,
            },
            OsVersionFamily.WindowsServer2025 => licenseFamily switch
            {
                "ServerDatacenter" or "ServerDatacenterCore" => "D764K-2NDRG-47T6Q-P8T8W-YP6DF",
                "ServerStandard" or "ServerStandardCore" => "TVRH6-WHNXV-R9WG3-9XRFY-MY832",
                "ServerAzureEditionDatacenter" or "ServerDatacenterAzureEdition"
                    or "ServerAzureCor" or "ServerAzureCorCore" => "XGN3F-F394H-FD2MY-PP6FD-8MCRC",
                _ => null,
            },
            _ => null,
        };
    }
}

public enum VolumeActivationType
{
    Kms = 0,
    Avma = 1,
}

/// <summary>
/// Coarse OS-version bucket used to pick a volume-activation key table.
/// .NET 5+ <c>Environment.OSVersion.Version</c> returns the real version
/// (no longer manifest-clamped), so build-number discrimination is reliable.
/// </summary>
public enum OsVersionFamily
{
    Unknown = 0,
    WindowsServer2012 = 6_02,
    WindowsServer2012R2 = 6_03,
    WindowsServer2016 = 10_00_14393,
    WindowsServer2019 = 10_00_17763,
    WindowsServer2022 = 10_00_20348,
    WindowsServer2025 = 10_00_26100,
}
