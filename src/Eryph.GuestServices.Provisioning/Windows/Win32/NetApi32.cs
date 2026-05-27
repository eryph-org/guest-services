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
    // NetLocalGroupAddMembers returns this Win32 error (NOT a NERR_*) when the
    // principal is already a member of the local group. Treated as a success
    // for idempotency — re-running the agent against an already-provisioned
    // guest must not flip the run to Failed.
    public const int ERROR_MEMBER_IN_ALIAS = 1378;

    // USER_INFO_1.usri1_priv values
    public const uint USER_PRIV_USER = 1;

    // USER_INFO_1.usri1_flags
    public const uint UF_SCRIPT = 0x0001;
    public const uint UF_ACCOUNTDISABLE = 0x0002;
    public const uint UF_NORMAL_ACCOUNT = 0x0200;
    public const uint UF_DONT_EXPIRE_PASSWD = 0x10000;
    public const uint UF_PASSWORD_EXPIRED = 0x800000;

    // TIMEQ_FOREVER (-1 as a DWORD) in usri*_acct_expires means "never expires".
    public const uint TIMEQ_FOREVER = 0xFFFFFFFF;

    public const int USER_INFO_LEVEL_1 = 1;
    public const int USER_INFO_LEVEL_2 = 2;
    // Level 4 supersedes level 3 on Windows XP+. We use it for the read-
    // modify-write that flips usri4_password_expired ("user must change
    // password at next logon"); there is no dedicated 10xx level for that
    // field. NOTE: level 1017 is acct_expires (the ACCOUNT expiry date), NOT
    // a password flag — writing it to force a password change expires the
    // whole account at the epoch. That mistake is the reason this level exists.
    public const int USER_INFO_LEVEL_4 = 4;
    public const int USER_INFO_LEVEL_1003 = 1003; // password
    public const int USER_INFO_LEVEL_1008 = 1008; // flags
    public const int USER_INFO_LEVEL_1011 = 1011; // full name
    public const int USER_INFO_LEVEL_1007 = 1007; // comment
    public const int USER_INFO_LEVEL_1006 = 1006; // home dir

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

    // Read-only level used to fetch a few extra fields not present in USER_INFO_1
    // (notably usri2_full_name). We never write through this level — writes go via
    // the dedicated per-field setters (1011 for full name, etc.).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_2
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri2_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri2_password;
        public uint usri2_password_age;
        public uint usri2_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri2_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri2_comment;
        public uint usri2_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri2_script_path;
        public uint usri2_auth_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri2_full_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri2_usr_comment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri2_parms;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri2_workstations;
        public uint usri2_last_logon;
        public uint usri2_last_logoff;
        public uint usri2_acct_expires;
        public uint usri2_max_storage;
        public uint usri2_units_per_week;
        public IntPtr usri2_logon_hours;
        public uint usri2_bad_pw_count;
        public uint usri2_num_logons;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri2_logon_server;
        public uint usri2_country_code;
        public uint usri2_code_page;
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
    public struct USER_INFO_1007
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1007_comment;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1006
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1006_home_dir;
    }

    // Full level-4 record. We only ever change usri4_password_expired and feed
    // every other field straight back (read-modify-write); fields the docs say
    // NetUserSetInfo ignores (name, password, sid, auth_flags, statistics) are
    // still marshalled so the round-trip is faithful. usri4_logon_hours and
    // usri4_user_sid are raw pointers that belong to the NetUserGetInfo buffer —
    // they MUST be nulled before the buffer is freed and the struct written back.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_4
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_password;
        public uint usri4_password_age;
        public uint usri4_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_comment;
        public uint usri4_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_script_path;
        public uint usri4_auth_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_full_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_usr_comment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_parms;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_workstations;
        public uint usri4_last_logon;
        public uint usri4_last_logoff;
        public uint usri4_acct_expires;
        public uint usri4_max_storage;
        public uint usri4_units_per_week;
        public IntPtr usri4_logon_hours;
        public uint usri4_bad_pw_count;
        public uint usri4_num_logons;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_logon_server;
        public uint usri4_country_code;
        public uint usri4_code_page;
        public IntPtr usri4_user_sid;
        public uint usri4_primary_group_id;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_profile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri4_home_dir_drive;
        public uint usri4_password_expired;
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
        ref USER_INFO_1007 buf,
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
        ref USER_INFO_4 buf,
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

    // `resumehandle` is IN/OUT per the Win32 documentation. Passing it by ref is
    // necessary for multi-page enumeration; the callee writes the next cursor
    // into the same location when the buffer fills up (`ERROR_MORE_DATA`).
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern int NetLocalGroupGetMembers(
        string? servername,
        string localgroupname,
        int level,
        out IntPtr bufptr,
        int prefmaxlen,
        out int entriesread,
        out int totalentries,
        ref IntPtr resumehandle);

    [DllImport("netapi32.dll")]
    public static extern int NetApiBufferFree(IntPtr buffer);
}
