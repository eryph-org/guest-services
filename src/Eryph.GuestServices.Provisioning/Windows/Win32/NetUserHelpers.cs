using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Eryph.GuestServices.Provisioning.Windows.Win32;

/// <summary>
/// Thin wrappers around the netapi32 user/group P/Invokes that surface
/// failures as exceptions and free buffers reliably. Keeps the orchestration
/// in <see cref="WindowsOs"/> readable.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NetUserHelpers
{
    public static bool UserExists(string userName)
    {
        var status = NetApi32.NetUserGetInfo(null, userName, NetApi32.USER_INFO_LEVEL_1, out var buffer);
        if (status == NetApi32.NERR_Success)
        {
            NetApi32.NetApiBufferFree(buffer);
            return true;
        }

        if (status == NetApi32.NERR_UserNotFound)
            return false;

        throw new Win32Exception(status, $"NetUserGetInfo failed for '{userName}' with code {status}.");
    }

    public static NetApi32.USER_INFO_1? TryGetUserInfo1(string userName)
    {
        var status = NetApi32.NetUserGetInfo(null, userName, NetApi32.USER_INFO_LEVEL_1, out var buffer);
        if (status == NetApi32.NERR_UserNotFound)
            return null;

        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status, $"NetUserGetInfo failed for '{userName}' with code {status}.");

        try
        {
            return Marshal.PtrToStructure<NetApi32.USER_INFO_1>(buffer);
        }
        finally
        {
            NetApi32.NetApiBufferFree(buffer);
        }
    }

    public static NetApi32.USER_INFO_2? TryGetUserInfo2(string userName)
    {
        var status = NetApi32.NetUserGetInfo(null, userName, NetApi32.USER_INFO_LEVEL_2, out var buffer);
        if (status == NetApi32.NERR_UserNotFound)
            return null;

        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status, $"NetUserGetInfo(2) failed for '{userName}' with code {status}.");

        try
        {
            return Marshal.PtrToStructure<NetApi32.USER_INFO_2>(buffer);
        }
        finally
        {
            NetApi32.NetApiBufferFree(buffer);
        }
    }

    public static void AddUser(LocalUserSpec spec, string? initialPassword)
    {
        var info = new NetApi32.USER_INFO_1
        {
            usri1_name = spec.Name,
            usri1_password = initialPassword,
            usri1_password_age = 0,
            usri1_priv = NetApi32.USER_PRIV_USER,
            usri1_home_dir = spec.HomeDir,
            usri1_comment = spec.Comment,
            usri1_flags = NetApi32.UF_NORMAL_ACCOUNT | NetApi32.UF_SCRIPT | NetApi32.UF_DONT_EXPIRE_PASSWD,
            usri1_script_path = null,
        };

        if (spec.Disabled == true)
            info.usri1_flags |= NetApi32.UF_ACCOUNTDISABLE;

        var status = NetApi32.NetUserAdd(null, NetApi32.USER_INFO_LEVEL_1, ref info, out var parmErr);
        if (status == NetApi32.NERR_UserExists)
            throw new InvalidOperationException($"User '{spec.Name}' already exists.");
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetUserAdd failed for '{spec.Name}' with code {status}, param {parmErr}.");

        if (!string.IsNullOrEmpty(spec.FullName))
            SetFullName(spec.Name, spec.FullName);
    }

    public static void SetPassword(string userName, string password)
    {
        var info = new NetApi32.USER_INFO_1003 { usri1003_password = password };
        var status = NetApi32.NetUserSetInfo(null, userName, NetApi32.USER_INFO_LEVEL_1003, ref info, out var parmErr);
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetUserSetInfo(1003) failed for '{userName}' with code {status}, param {parmErr}.");
    }

    /// <summary>
    /// Sets ("user must change password at next logon") via usri4_password_expired.
    /// There is no dedicated 10xx info level for this flag, so we read the whole
    /// level-4 record, flip the one field, and write it back. We deliberately do
    /// NOT touch usri4_acct_expires — the account expiry stays whatever NetUserAdd
    /// defaulted it to (TIMEQ_FOREVER / never). The previous implementation used
    /// level 1017 here, which is acct_expires, and expired the account at the epoch.
    /// </summary>
    public static void SetPasswordExpired(string userName, bool expired)
    {
        var status = NetApi32.NetUserGetInfo(null, userName, NetApi32.USER_INFO_LEVEL_4, out var buffer);
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status, $"NetUserGetInfo(4) failed for '{userName}' with code {status}.");

        NetApi32.USER_INFO_4 info;
        try
        {
            info = Marshal.PtrToStructure<NetApi32.USER_INFO_4>(buffer);
        }
        finally
        {
            NetApi32.NetApiBufferFree(buffer);
        }

        // The LPWSTR members were copied into managed strings by PtrToStructure and
        // will be re-marshalled into fresh native strings on the way back, so they
        // survive freeing the buffer. The two raw pointers did NOT — they point into
        // the freed buffer. NetUserSetInfo ignores the SID, and a NULL logon_hours
        // means "leave the logon schedule unchanged", so null both before writing.
        info.usri4_user_sid = IntPtr.Zero;
        info.usri4_logon_hours = IntPtr.Zero;
        info.usri4_password_expired = expired ? 1u : 0u;

        // "Must change at next logon" and "password never expires" are mutually
        // exclusive: with UF_DONT_EXPIRE_PASSWD set, Windows silently ignores a
        // usri4_password_expired = 1 write (NetUserSetInfo still returns success).
        // AddUser sets UF_DONT_EXPIRE_PASSWD on every account, so the flag must be
        // cleared in this same write for the must-change request to take effect.
        // When clearing the flag (expired == false) we leave it as-is.
        if (expired)
            info.usri4_flags &= ~NetApi32.UF_DONT_EXPIRE_PASSWD;

        var setStatus = NetApi32.NetUserSetInfo(null, userName, NetApi32.USER_INFO_LEVEL_4, ref info, out var parmErr);
        if (setStatus != NetApi32.NERR_Success)
            throw new Win32Exception(setStatus,
                $"NetUserSetInfo(4) failed for '{userName}' with code {setStatus}, param {parmErr}.");
    }

    /// <summary>
    /// Reads back (usri4_password_expired, usri4_acct_expires) for diagnostics.
    /// password_expired is normalised to 0/1 by NetUserGetInfo.
    /// </summary>
    public static (uint passwordExpired, uint acctExpires) GetPasswordState(string userName)
    {
        var status = NetApi32.NetUserGetInfo(null, userName, NetApi32.USER_INFO_LEVEL_4, out var buffer);
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status, $"NetUserGetInfo(4) failed for '{userName}' with code {status}.");

        try
        {
            var info = Marshal.PtrToStructure<NetApi32.USER_INFO_4>(buffer);
            return (info.usri4_password_expired, info.usri4_acct_expires);
        }
        finally
        {
            NetApi32.NetApiBufferFree(buffer);
        }
    }

    public static void SetFlags(string userName, uint flags)
    {
        var info = new NetApi32.USER_INFO_1008 { usri1008_flags = flags };
        var status = NetApi32.NetUserSetInfo(null, userName, NetApi32.USER_INFO_LEVEL_1008, ref info, out var parmErr);
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetUserSetInfo(1008) failed for '{userName}' with code {status}, param {parmErr}.");
    }

    public static void SetFullName(string userName, string? fullName)
    {
        var info = new NetApi32.USER_INFO_1011 { usri1011_full_name = fullName };
        var status = NetApi32.NetUserSetInfo(null, userName, NetApi32.USER_INFO_LEVEL_1011, ref info, out var parmErr);
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetUserSetInfo(1011) failed for '{userName}' with code {status}, param {parmErr}.");
    }

    public static void SetComment(string userName, string? comment)
    {
        // Level 1007 is the comment. Level 1052 (used previously) is the user's
        // PROFILE PATH — setting the comment through it silently rewrote the
        // profile path instead.
        var info = new NetApi32.USER_INFO_1007 { usri1007_comment = comment };
        var status = NetApi32.NetUserSetInfo(null, userName, NetApi32.USER_INFO_LEVEL_1007, ref info, out var parmErr);
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetUserSetInfo(1007) failed for '{userName}' with code {status}, param {parmErr}.");
    }

    public static void SetHomeDir(string userName, string? homeDir)
    {
        var info = new NetApi32.USER_INFO_1006 { usri1006_home_dir = homeDir };
        var status = NetApi32.NetUserSetInfo(null, userName, NetApi32.USER_INFO_LEVEL_1006, ref info, out var parmErr);
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetUserSetInfo(1006) failed for '{userName}' with code {status}, param {parmErr}.");
    }

    public static bool LocalGroupExists(string groupName)
    {
        var status = NetApi32.NetLocalGroupGetInfo(null, groupName, 0, out var buffer);
        if (status == NetApi32.NERR_Success)
        {
            NetApi32.NetApiBufferFree(buffer);
            return true;
        }

        if (status == NetApi32.NERR_GroupNotFound)
            return false;

        throw new Win32Exception(status, $"NetLocalGroupGetInfo failed for '{groupName}' with code {status}.");
    }

    public static void CreateLocalGroup(string groupName)
    {
        var info = new NetApi32.LOCALGROUP_INFO_0 { lgrpi0_name = groupName };
        var status = NetApi32.NetLocalGroupAdd(null, 0, ref info, out var parmErr);
        if (status == NetApi32.NERR_GroupExists)
            return;
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetLocalGroupAdd failed for '{groupName}' with code {status}, param {parmErr}.");
    }

    public static void AddMemberByName(string groupName, string userName)
    {
        var entries = new[]
        {
            new NetApi32.LOCALGROUP_MEMBERS_INFO_3 { lgrmi3_domainandname = userName },
        };

        var status = NetApi32.NetLocalGroupAddMembers(null, groupName, 3, entries, entries.Length);
        // NetLocalGroupAddMembers reports the "already a member" outcome via two
        // different codes depending on Windows version: NERR_UserInGroup (2236)
        // on some, ERROR_MEMBER_IN_ALIAS (1378) on most. Treat both as success.
        if (status is NetApi32.NERR_UserInGroup or NetApi32.ERROR_MEMBER_IN_ALIAS)
            return;
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetLocalGroupAddMembers failed adding '{userName}' to '{groupName}' with code {status}.");
    }

    public static IReadOnlyList<string> GetGroupMemberNames(string groupName)
    {
        const int ERROR_MORE_DATA = 234;

        var members = new List<string>();
        var entrySize = Marshal.SizeOf<NetApi32.LOCALGROUP_MEMBERS_INFO_3>();
        IntPtr resumeHandle = IntPtr.Zero;

        while (true)
        {
            var status = NetApi32.NetLocalGroupGetMembers(
                null, groupName, 3, out var buffer, -1, out var entriesRead, out _, ref resumeHandle);

            if (status != NetApi32.NERR_Success && status != ERROR_MORE_DATA)
                throw new Win32Exception(status,
                    $"NetLocalGroupGetMembers failed for '{groupName}' with code {status}.");

            try
            {
                for (var i = 0; i < entriesRead; i++)
                {
                    var entryPtr = IntPtr.Add(buffer, i * entrySize);
                    var entry = Marshal.PtrToStructure<NetApi32.LOCALGROUP_MEMBERS_INFO_3>(entryPtr);
                    if (!string.IsNullOrEmpty(entry.lgrmi3_domainandname))
                        members.Add(entry.lgrmi3_domainandname);
                }
            }
            finally
            {
                NetApi32.NetApiBufferFree(buffer);
            }

            if (status != ERROR_MORE_DATA || resumeHandle == IntPtr.Zero)
                break;
        }

        return members;
    }
}
