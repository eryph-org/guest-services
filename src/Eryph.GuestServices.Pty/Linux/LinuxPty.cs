using Eryph.GuestServices.Pty.Windows;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Pty.Linux;

public sealed partial class LinuxPty : IPty
{
    private SafeFileHandle? _masterFd;

    private SafeProcessHandle? _processHandle;

    private Process? _process;

    private FileStream? _masterFdReadStream;
    private FileStream? _masterFdWriteStream;

    public Stream? Input { get; private set; }

    public Stream? Output { get; private set; }

    [MemberNotNull(nameof(_process), nameof(Input), nameof(Output))]
    public Task StartAsync(uint width, uint height, string command)
    {
        var termios = new Termios
        {
            InputFlag = InputFlag.ICRNL | InputFlag.IXON | InputFlag.IXANY | InputFlag.IMAXBEL | InputFlag.BRKINT | InputFlag.IUTF8,
            OutputFlags = OutputFlag.OPOST | OutputFlag.ONLCR,
            ControlFlags = ControlFlag.CS8 | ControlFlag.CREAD | ControlFlag.HUPCL,
            LFlag = LocalFlag.ECHOKE | LocalFlag.ECHOE | LocalFlag.ECHOK | LocalFlag.ECHO | LocalFlag.ECHOCTL | LocalFlag.ISIG | LocalFlag.ICANON | LocalFlag.IEXTEN,
            ControlCharacters = new ControlCharacters
            {
                VEOF = 4,
                VEOL = 255,
                VEOL2 = 255,
                VERASE = 0x7f,
                VWERASE = 23,
                VKILL = 21,
                VREPRINT = 18,
                VINTR = 3,
                VQUIT = 0x1c,
                VSUSP = 26,
                VSTART = 17,
                VSTOP = 19,
                VLNEXT = 22,
                VDISCARD = 15,
                VMIN = 1,
                VTIME = 0,
            },
            InputSpeed = 0xf, //B38400
            OutputSpeed = 0xf, //B38400
        };

        var winSize = new WinSize
        {
            Columns = (ushort)Math.Min(width, ushort.MaxValue),
            Rows = (ushort)Math.Min(height, ushort.MaxValue),
        };

        var result = SpawnPty(
            command,
            termios,
            winSize,
            out var masterFd,
            out var processId);
        _masterFd = new SafeFileHandle(masterFd, ownsHandle: true);
        _processHandle = new SafeProcessHandle(processId, ownsHandle: true);

        if (result != 0)
            throw new InvalidOperationException($"Could not create pseudo console or process: {result}.");
        _masterFdReadStream = new FileStream(_masterFd, FileAccess.Read);
        _masterFdWriteStream = new FileStream(_masterFd, FileAccess.Write);

        Input = _masterFdWriteStream;
        Output = _masterFdReadStream;
        _process = Process.GetProcessById(processId);
        return Task.CompletedTask;
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        await Task.Factory.StartNew(
            () =>
            {
                // On Linux, we must use waitpid to check if the process 
                WaitPid(_processHandle!, out var status, 0);
            },
            TaskCreationOptions.LongRunning);
        
        return 0;
    }

    public Task ResizeAsync(uint width, uint height)
    {
        var winSize = new WinSize
        {
            Columns = (ushort)Math.Min(width, ushort.MaxValue),
            Rows = (ushort)Math.Min(height, ushort.MaxValue),
        };
        if (IoCtl(_masterFd!, OpCode, winSize) == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            var i = 0;
        };
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _masterFdReadStream?.Dispose();
        _masterFdWriteStream?.Dispose();
        _masterFd?.Dispose();
        _processHandle?.Dispose();
        _process?.Dispose();
    }


    [Flags]
    private enum InputFlag : uint
    {
        BRKINT = 0x2,
        ICRNL = 0x100,
        IXON = 0x400,
        IXANY = 0x800,
        IMAXBEL = 0x2000,
        IUTF8 = 0x4000,
    }

    [Flags]
    public enum OutputFlag : uint
    {
        OPOST = 1,
        ONLCR = 4,
    }

    [Flags]
    public enum ControlFlag : uint
    {
        CS8 = 0x30,
        CREAD = 0x80,
        HUPCL = 0x400,
    }

    [Flags]
    public enum LocalFlag : uint
    {
        ECHOKE = 0x800,
        ECHOE = 0x10,
        ECHOK = 0x20,
        ECHO = 0x8,
        ECHOCTL = 0x200,
        ISIG = 0x1,
        ICANON = 0x2,
        IEXTEN = 0x8000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Termios
    {
        public InputFlag InputFlag;
        public OutputFlag OutputFlags;
        public ControlFlag ControlFlags;
        public LocalFlag LFlag;

        public byte LineDiscipline;

        public ControlCharacters ControlCharacters;
        public uint InputSpeed;
        public uint OutputSpeed;
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct ControlCharacters
    {
        public byte VINTR;
        public byte VQUIT;
        public byte VERASE;
        public byte VKILL;
        public byte VEOF;
        public byte VTIME;
        public byte VMIN;
        public byte VSWTC;
        public byte VSTART;
        public byte VSTOP;
        public byte VSUSP;
        public byte VEOL;
        public byte VREPRINT;
        public byte VDISCARD;
        public byte VWERASE;
        public byte VLNEXT;
        public byte VEOL2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixel;
        public ushort YPixel;
    }

    [LibraryImport("runtimes/linux-x64/native/spawnptylib.so", EntryPoint = "spawn_pty", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SpawnPty(string command, in Termios termios, in WinSize winSize, out int masterFd, out int childPid);

    [LibraryImport("libc", EntryPoint = "ioctl", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int IoCtl(SafeFileHandle fd, int opcode, in WinSize winSize);

    [LibraryImport("libc", EntryPoint = "waitpid", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int WaitPid(SafeProcessHandle pid, out int status, int options);

    /// <summary>
    /// 
    /// </summary>
    private const int OpCode = 0x5414;
}
