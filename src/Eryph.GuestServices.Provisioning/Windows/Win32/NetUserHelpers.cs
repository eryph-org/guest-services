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

    public static void SetPasswordExpired(string userName, bool expired)
    {
        var info = new NetApi32.USER_INFO_1017 { usri1017_password_expired = expired ? 1u : 0u };
        var status = NetApi32.NetUserSetInfo(null, userName, NetApi32.USER_INFO_LEVEL_1017, ref info, out var parmErr);
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetUserSetInfo(1017) failed for '{userName}' with code {status}, param {parmErr}.");
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
        var info = new NetApi32.USER_INFO_1052 { usri1052_comment = comment };
        var status = NetApi32.NetUserSetInfo(null, userName, NetApi32.USER_INFO_LEVEL_1052, ref info, out var parmErr);
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetUserSetInfo(1052) failed for '{userName}' with code {status}, param {parmErr}.");
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
        if (status == NetApi32.NERR_UserInGroup)
            return; // already a member; idempotent.
        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetLocalGroupAddMembers failed adding '{userName}' to '{groupName}' with code {status}.");
    }

    public static IReadOnlyList<string> GetGroupMemberNames(string groupName)
    {
        var status = NetApi32.NetLocalGroupGetMembers(
            null, groupName, 3, out var buffer, -1, out var entriesRead, out _, IntPtr.Zero);

        if (status != NetApi32.NERR_Success)
            throw new Win32Exception(status,
                $"NetLocalGroupGetMembers failed for '{groupName}' with code {status}.");

        try
        {
            var members = new List<string>(entriesRead);
            var entrySize = Marshal.SizeOf<NetApi32.LOCALGROUP_MEMBERS_INFO_3>();
            for (var i = 0; i < entriesRead; i++)
            {
                var entryPtr = IntPtr.Add(buffer, i * entrySize);
                var entry = Marshal.PtrToStructure<NetApi32.LOCALGROUP_MEMBERS_INFO_3>(entryPtr);
                if (!string.IsNullOrEmpty(entry.lgrmi3_domainandname))
                    members.Add(entry.lgrmi3_domainandname);
            }

            return members;
        }
        finally
        {
            NetApi32.NetApiBufferFree(buffer);
        }
    }
}
