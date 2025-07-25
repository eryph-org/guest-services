﻿using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Eryph.GuestServices.Pty.Windows;

public sealed partial class WindowsPty : IPty
{
    private int _disposed;

    private SafePipeHandle? _ptyReadPipe;
    private SafePipeHandle? _ptyWritePipe;
    private SafePipeHandle? _readPipe;
    private SafePipeHandle? _writePipe;
    private PseudoConsoleSafeHandle? _pseudoConsoleHandle;
    private ProcThreadAttributeListSafeHandle? _attributeListHandle;

    private SafeProcessHandle? _processHandle;
    private SafeWaitHandle? _threadHandle;

    private Process? _process;

    public Stream? Input { get; private set; }

    public Stream? Output { get; private set; }

    [MemberNotNull(nameof(_process), nameof(Input), nameof(Output))]
    public async Task StartAsync(uint width, uint height, string command, string arguments)
    {
        await Task.Yield();

        if (!CreatePipe(out _ptyReadPipe, out _writePipe, 0, 0))
        {
            var error = Marshal.GetHRForLastWin32Error();
            throw new PtyException($"Could not create pseudo console input pipe: 0x{error:x8}.", error);
        }

        if (!CreatePipe(out _readPipe, out _ptyWritePipe, 0, 0))
        {
            var error = Marshal.GetHRForLastWin32Error();
            throw new PtyException($"Could not create pseudo console output pipe: 0x{error:x8}.", error);
        }

        var hResult = CreatePseudoConsole(
            new Coordinates
            {
                X = (short)Math.Min(width, short.MaxValue),
                Y = (short)Math.Min(height, short.MaxValue),
            },
            _ptyReadPipe,
            _ptyWritePipe,
            0,
            out _pseudoConsoleHandle);
        if (hResult != 0)
            throw new PtyException($"Could not create pseudo console: 0x{hResult:x8}.", hResult);
        
        _attributeListHandle = ProcThreadAttributeListSafeHandle.Allocate(1);
        var success = UpdateProcThreadAttribute(
            lpAttributeList: _attributeListHandle,
            dwFlags: 0,
            attribute: ProcThreadAttributePseudoConsole,
            lpValue: _pseudoConsoleHandle,
            cbSize: nint.Size,
            lpPreviousValue: 0,
            lpReturnSize: 0);

        if (!success)
        {
            var error = Marshal.GetHRForLastWin32Error();
            throw new PtyException($"Could not update ProcTheadAttributeList: 0x{error:x8}.", error);
        }
        
        var startupInfo = new StartupInfoEx();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        startupInfo.lpAttributeList = _attributeListHandle.DangerousGetHandle();

        var commandLine = string.IsNullOrEmpty(arguments) ? $"{command}" : $"{command} {arguments}";

        success = CreateProcess(
            lpApplicationName: null,
            // According to the documentation, CreateProcessW might actually
            // modify the provided string (the documentation states that the pointer
            // must be mutable). Not sure  if this is necessary, but we explicitly
            // pass a char array to make sure that the memory is writable.
            lpCommandLine: commandLine.ToCharArray(),
            lpProcessAttributes: 0,
            lpThreadAttributes: 0,
            bInheritHandles: false,
            dwCreationFlags: ExtendedStartupInfoPresent,
            lpEnvironment: 0,
            lpCurrentDirectory: null,
            lpStartupInfo: in startupInfo,
            lpProcessInformation: out var pInfo);

        if (!success)
        {
            var error = Marshal.GetHRForLastWin32Error();
            throw new PtyException($"Could not create pseudo console process 0x{error:x8}.", error); 
        }
        
        _processHandle = new SafeProcessHandle(pInfo.hProcess, true);
        _threadHandle = new SafeWaitHandle(pInfo.hThread, true);

        _process = Process.GetProcessById(pInfo.dwProcessId);

        Input = new AnonymousPipeClientStream(PipeDirection.Out, _writePipe);
        Output = new AnonymousPipeClientStream(PipeDirection.In, _readPipe);
    }

    public async Task ResizeAsync(uint width, uint height)
    {
        await Task.Yield();

        if (_pseudoConsoleHandle is null)
            return;

        var result = ResizePseudoConsole(
            _pseudoConsoleHandle,
            new Coordinates
            {
                X = (short)Math.Min(width, short.MaxValue),
                Y = (short)Math.Min(height, short.MaxValue)
            });

        if(result != 0)
            throw new PtyException($"Could not resize pseudo console: 0x{result:x8}.", result);

        return;
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        await _process!.WaitForExitAsync(cancellation);
        return _process.ExitCode;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        Input?.Dispose();
        Output?.Dispose();

        _readPipe?.Dispose();
        _writePipe?.Dispose();
        _ptyReadPipe?.Dispose();
        _ptyWritePipe?.Dispose();

        // We must dispose the pseudo console after its pipes. Otherwise,
        // a deadlock can occur in older Windows Versions.
        // See https://learn.microsoft.com/en-us/windows/console/closepseudoconsole.
        _pseudoConsoleHandle?.Dispose();

        _process?.Kill(true);
        _process?.Dispose();

        _processHandle?.Dispose();
        _threadHandle?.Dispose();
        _attributeListHandle?.Dispose();
    }

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nint ProcThreadAttributePseudoConsole = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coordinates
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        // These fields are actually strings (LPSTR or LPWSTR) but
        // these are not supported by LibraryImport. Our code does not
        // need them. Hence, we use nint to allocate the proper space
        // in the struct.
        public nint lpReserved;
        public nint lpDesktop; 
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;

        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        SafeHandle lpAttributeList,
        uint dwFlags,
        nint attribute,
        SafeHandle lpValue,
        nint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(
        string? lpApplicationName,
        char[] lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        in StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CreatePseudoConsole(
        Coordinates size,
        SafePipeHandle hInput,
        SafePipeHandle hOutput,
        uint dwFlags,
        out PseudoConsoleSafeHandle phPc);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int ResizePseudoConsole(
        PseudoConsoleSafeHandle hPc,
        Coordinates size);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(
        out SafePipeHandle hReadPipe,
        out SafePipeHandle hWritePipe,
        nint lpPipeAttributes,
        int nSize);
}
