using Microsoft.VisualBasic;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Eryph.GuestServices.Pty.Windows;

public sealed class WindowsPty : IPty, IDisposable
{
    private SafePipeHandle? _ptyReadPipe;
    private SafePipeHandle? _ptyWritePipe;
    private SafePipeHandle? _readPipe;
    private SafePipeHandle? _writePipe;
    private PseudoConsoleSafeHandle? _pseudoConsoleHandle;
    private ProcThreadAttributeListSafeHandle? _attributeListHandle;

    private SafeWaitHandle? _processHandle;
    private SafeWaitHandle? _threadHandle;

    private Process? _process;

    private AnonymousPipeClientStream? _readStream;
    private AnonymousPipeClientStream? _writeStream;

    public Stream Input  => _writeStream!;

    public Stream Output => _readStream!;

    public Task StartAsync(uint width, uint height, string command)
    {
        NativeMethods.CreatePipe(out _ptyReadPipe, out _writePipe, IntPtr.Zero, 0);
        NativeMethods.CreatePipe(out _readPipe, out _ptyWritePipe, IntPtr.Zero, 0);

        NativeMethods.CreatePseudoConsole(
            new NativeMethods.COORD() { X = (short)width, Y = (short)height },
            _ptyReadPipe,
            _ptyWritePipe,
            0,
            out _pseudoConsoleHandle);

        _attributeListHandle = ProcThreadAttributeListSafeHandle.Allocate(1);

        var success = NativeMethods.UpdateProcThreadAttribute(
            lpAttributeList: _attributeListHandle,
            dwFlags: 0,
            attribute: (nint)NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            lpValue: _pseudoConsoleHandle,
            cbSize: IntPtr.Size,
            lpPreviousValue: IntPtr.Zero,
            lpReturnSize: IntPtr.Zero
        );
        if (!success)
        {
            throw new InvalidOperationException("Could not set pseudoconsole thread attribute. " + Marshal.GetLastWin32Error());
        }

        var startupInfo = new NativeMethods.STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();
        startupInfo.lpAttributeList = _attributeListHandle;

        int securityAttributeSize = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>();
        var pSec = new NativeMethods.SECURITY_ATTRIBUTES { nLength = securityAttributeSize };
        var tSec = new NativeMethods.SECURITY_ATTRIBUTES { nLength = securityAttributeSize };
        success = NativeMethods.CreateProcess(
            lpApplicationName: command,
            lpCommandLine: null,
            lpProcessAttributes: ref pSec,
            lpThreadAttributes: ref tSec,
            bInheritHandles: false,
            dwCreationFlags: NativeMethods.EXTENDED_STARTUPINFO_PRESENT,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: null,
            lpStartupInfo: ref startupInfo,
            lpProcessInformation: out NativeMethods.PROCESS_INFORMATION pInfo
        );
        if (!success)
        {
            throw new InvalidOperationException("Could not create process. " + Marshal.GetLastWin32Error());
        }

        _processHandle = new SafeWaitHandle(pInfo.hProcess, true);
        _threadHandle = new SafeWaitHandle(pInfo.hThread, true);

        _process = Process.GetProcessById(pInfo.dwProcessId);

        _readStream = new AnonymousPipeClientStream(PipeDirection.In, _readPipe);
        _writeStream = new AnonymousPipeClientStream(PipeDirection.Out, _writePipe);

        return Task.CompletedTask;
    }

    public Task ResizeAsync(uint width, uint height)
    {
        if (_pseudoConsoleHandle is null)
            return Task.CompletedTask;

        NativeMethods.ResizePseudoConsole(
            _pseudoConsoleHandle,
            new NativeMethods.COORD { X = (short)width, Y = (short)height });
        
        return Task.CompletedTask;
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        await _process!.WaitForExitAsync(cancellation);
        return _process.ExitCode;
    }

    public void Dispose()
    {
        _readStream?.Dispose();
        _writeStream?.Dispose();

        _readPipe?.Dispose();
        _writePipe?.Dispose();
        _ptyReadPipe?.Dispose();
        _ptyWritePipe?.Dispose();

        // We must dispose the pseudo console after its pipes. Otherwise,
        // a deadlock can occur in older Windows Versions.
        // See https://learn.microsoft.com/en-us/windows/console/closepseudoconsole.
        _pseudoConsoleHandle?.Dispose();


        _processHandle?.Dispose();
        _threadHandle?.Dispose();
        _attributeListHandle?.Dispose();

        _process?.Dispose();
    }
}
