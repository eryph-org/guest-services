using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Pty.Windows;

internal partial class ProcThreadAttributeListSafeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    private bool _isInitialized;

    protected override bool ReleaseHandle()
    {
        if (_isInitialized)
            DeleteProcThreadAttributeList(handle);
        
        Marshal.FreeHGlobal(handle);
        return true;
    }

    /// <summary>
    /// Allocates a <c>ProcThreadAttributeList</c> and returns a <see cref="SafeHandle"/> for it.
    /// </summary>
    /// <remarks>
    /// This code uses the double call pattern described here:
    /// https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-initializeprocthreadattributelist
    /// </remarks>
    public static ProcThreadAttributeListSafeHandle Allocate(int size)
    {
        nint lpSize = 0;
        var success = InitializeProcThreadAttributeList(
            lpAttributeList: IntPtr.Zero,
            dwAttributeCount: size,
            dwFlags: 0,
            lpSize: ref lpSize
        );
        if (success || lpSize == IntPtr.Zero)
            throw new InvalidOperationException($"Could not calculate the number of bytes for the attribute list: 0x{Marshal.GetHRForLastWin32Error():x8}.");

        var safeHandle = new ProcThreadAttributeListSafeHandle();
        safeHandle.SetHandle(Marshal.AllocHGlobal(lpSize));

        try
        {
            success = InitializeProcThreadAttributeList(
                lpAttributeList: safeHandle.handle,
                dwAttributeCount: size,
                dwFlags: 0,
                lpSize: ref lpSize
            );
            
            if (!success)
                throw new InvalidOperationException($"Could not initialize ProcThreadAttributeList: 0x{Marshal.GetHRForLastWin32Error():x8}.");

            safeHandle._isInitialized = true;
            return safeHandle;
        }
        catch
        {
            safeHandle.Dispose();
            throw;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void DeleteProcThreadAttributeList(nint lpAttributeList);
}
