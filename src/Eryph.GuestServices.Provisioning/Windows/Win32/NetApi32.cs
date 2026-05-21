using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Eryph.GuestServices.Provisioning.Windows.Win32;

/// <summary>
/// P/Invoke surface for <c>netapi32.dll</c>. Only the entries we actually
/// call live here. All structures are marshalled as Unicode and use
/// <c>LPWSTR</c> pointers for string members, matching the documented
/// signatures of the underlying Net* APIs.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NetApi32
{
    public const int NERR_Success = 0;
    public const int NERR_UserExists = 2224;
    public const int NERR_UserNotFound = 2221;
    public const int NERR_GroupExists = 2223;
    public const int NERR_GroupNotFound = 2220;
    public const int NERR_UserInGroup = 2236;

    // USER_INFO_1.usri1_priv values
    public const uint USER_PRIV_USER = 1;

    // USER_INFO_1.usri1_flags
    public const uint UF_SCRIPT = 0x0001;
    public const uint UF_ACCOUNTDISABLE = 0x0002;
    public const uint UF_NORMAL_ACCOUNT = 0x0200;
    public const uint UF_DONT_EXPIRE_PASSWD = 0x10000;
    public const uint UF_PASSWORD_EXPIRED = 0x800000;

    public const int USER_INFO_LEVEL_1 = 1;
    public const int USER_INFO_LEVEL_4 = 4;
    public const int USER_INFO_LEVEL_1003 = 1003; // password
    public const int USER_INFO_LEVEL_1008 = 1008; // flags
    public const int USER_INFO_LEVEL_1011 = 1011; // full name
    public const int USER_INFO_LEVEL_1052 = 1052; // comment
    public const int USER_INFO_LEVEL_1006 = 1006; // home dir
    public const int USER_INFO_LEVEL_1017 = 1017; // password expired

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_comment;
        public uint usri1_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1003
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1003_password;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct USER_INFO_1008
    {
        public uint usri1008_flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1011
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1011_full_name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1052
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1052_comment;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1006
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1006_home_dir;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct USER_INFO_1017
    {
        public uint usri1017_password_expired;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOCALGROUP_INFO_0
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lgrpi0_name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LOCALGROUP_MEMBERS_INFO_0
    {
        public IntPtr lgrmi0_sid; // PSID
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOCALGROUP_MEMBERS_INFO_3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lgrmi3_domainandname;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetUserAdd(
        string? servername,
        int level,
        ref USER_INFO_1 buf,
        out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetUserGetInfo(
        string? servername,
        string username,
        int level,
        out IntPtr bufptr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetUserSetInfo(
        string? servername,
        string username,
        int level,
        ref USER_INFO_1003 buf,
        out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetUserSetInfo(
        string? servername,
        string username,
        int level,
        ref USER_INFO_1008 buf,
        out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetUserSetInfo(
        string? servername,
        string username,
        int level,
        ref USER_INFO_1011 buf,
        out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetUserSetInfo(
        string? servername,
        string username,
        int level,
        ref USER_INFO_1052 buf,
        out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetUserSetInfo(
        string? servername,
        string username,
        int level,
        ref USER_INFO_1006 buf,
        out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetUserSetInfo(
        string? servername,
        string username,
        int level,
        ref USER_INFO_1017 buf,
        out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetLocalGroupAdd(
        string? servername,
        int level,
        ref LOCALGROUP_INFO_0 buf,
        out uint parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetLocalGroupGetInfo(
        string? servername,
        string groupname,
        int level,
        out IntPtr bufptr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetLocalGroupAddMembers(
        string? servername,
        string groupname,
        int level,
        [In] LOCALGROUP_MEMBERS_INFO_3[] buf,
        int totalentries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetLocalGroupGetMembers(
        string? servername,
        string localgroupname,
        int level,
        out IntPtr bufptr,
        int prefmaxlen,
        out int entriesread,
        out int totalentries,
        IntPtr resumehandle);

    [DllImport("netapi32.dll")]
    public static extern int NetApiBufferFree(IntPtr buffer);
}
