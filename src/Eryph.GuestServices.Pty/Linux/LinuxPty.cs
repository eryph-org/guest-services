using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Eryph.GuestServices.Pty.Linux;

public sealed partial class LinuxPty : IPty
{
    private int _disposed;

    private SafeFileHandle? _masterFd;
    private SafeProcessHandle? _processHandle;

    public Stream? Input { get; private set; }

    public Stream? Output { get; private set; }

    [MemberNotNull(nameof(Input), nameof(Output))]
    public async Task StartAsync(uint width, uint height, string fileName, string arguments)
    {
        await Task.Yield();

        var winSize = new WinSize
        {
            Columns = (ushort)Math.Min(width, ushort.MaxValue),
            Rows = (ushort)Math.Min(height, ushort.MaxValue),
        };
        
        var result = spawnpty(
            // The arguments array must be terminated with null
            [..ParseArguments(fileName, arguments), null],
            0, // We do not pass the termios struct at the moment as the defaults seem to work
            winSize,
            out var masterFd,
            out var processId);
        _masterFd = new SafeFileHandle(masterFd, ownsHandle: true);
        _processHandle = new SafeProcessHandle(processId, ownsHandle: true);

        if (result != 0)
            throw new PtyException($"Could not create pseudo console or process: {result}.", result);

        Input = new FileStream(_masterFd, FileAccess.Write);
        Output = new FileStream(_masterFd, FileAccess.Read);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        while (true)
        {
            await Task.Delay(100, cancellation);

            // On Linux, we must use waitpid to check if the process has exited.
            // Otherwise, the process remains as a zombie process. Also,
            // Process.WaitForExitAsync() just never completes.
            if (waitpid(_processHandle!, out var status, WaitPidOptions.WNOHANG) != 0)
            {
                // The exit code is only 8 bits and encoded within the returned status.
                var exitCode = (status & 0xff00) >> 8;
                // Wrap the exit code into a custom HResult with our custom facility code.
                var result = PtyErrorCodes.LinuxExitCode & exitCode;
                return result;
            }
        }
    }

    public async Task ResizeAsync(uint width, uint height)
    {
        await Task.Yield();

        var winSize = new WinSize
        {
            Columns = (ushort)Math.Min(width, ushort.MaxValue),
            Rows = (ushort)Math.Min(height, ushort.MaxValue),
        };

        if (ioctl(_masterFd!, IOCtlCodes.TIOCGWINSZ, winSize) == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new PtyException($"Could not resize the PTY: {error}.", error);
        };
    }

    private void KillProcess()
    {
        if (_processHandle is not null)
        {
            // killpg kills the processes in the process group but not
            // processes in other process groups of the same session.
            // Even OpenSSH does not perform such a cleanup.
            killpg(_processHandle!, Signals.SIGKILL);
        }
    }

    private static string[] ParseArguments(string fileName, string arguments)
    {
        if (!Path.IsPathFullyQualified(fileName))
            throw new ArgumentException($"The {nameof(fileName)} must be fully qualified.", nameof(fileName));

        var processStartInfo = new ProcessStartInfo()
        {
            FileName = fileName,
            Arguments = arguments,
        };

        // For Linux, we need the arguments as an array. We reuse an existing method in
        // the .NET runtime for this to achieve consistent behavior. Unfortunately, this
        // method is exposed, so we use reflection to access it. The method has been
        // stable for a long time.
        var method = typeof(Process).GetMethod("ParseArgv", BindingFlags.Static | BindingFlags.NonPublic);
        if (method is null)
            throw new PtyException(
                "The method Process.ParseArgv does not exist.",
                PtyErrorCodes.FailedToParseArguments);

        var result = method.Invoke(null, [processStartInfo, null, false]);
        if (result is not string[] args)
            throw new PtyException(
                "The method Process.ParseArgv did not return a string array.",
                PtyErrorCodes.FailedToParseArguments);

        return args;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        Input?.Dispose();
        Output?.Dispose();
        _masterFd?.Dispose();

        KillProcess();
        _processHandle?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Termios
    {
        public InputFlag InputFlags;
        public OutputFlag OutputFlags;
        public ControlFlag ControlFlags;
        public LocalFlag LocalFlags;

        public byte LineDiscipline;

        public ControlCharacter ControlCharacters;

        public uint InputSpeed;
        public uint OutputSpeed;

        [Flags]
        public enum InputFlag : uint
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

        /// <summary>
        /// In the Linux kernel, the control characters are defined
        /// as an array. Both the size of the array and the position
        /// of the control characters are fixed by constants. Hence,
        /// it is easier to encode this a struct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Size = 32)]
        public struct ControlCharacter
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixel;
        public ushort YPixel;
    }

    private enum IOCtlCodes
    {
        TIOCGWINSZ = 0x5414,
    }

    [Flags]
    private enum WaitPidOptions
    {
        WNOHANG = 0x1,
    }

    private enum Signals
    {
        SIGKILL = 9,
    }

    [LibraryImport("native/linux-x64/spawnpty.so", EntryPoint = "spawnpty", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int spawnpty(string?[] args, nint termios, in WinSize winSize, out int masterFd, out int childPid);

    [LibraryImport("libc", EntryPoint = "ioctl", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int ioctl(SafeFileHandle fd, IOCtlCodes opcode, in WinSize winSize);

    [LibraryImport("libc", EntryPoint = "waitpid", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int waitpid(SafeProcessHandle pid, out int status, WaitPidOptions options);

    [LibraryImport("libc", EntryPoint = "killpg", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int killpg(SafeProcessHandle pid, Signals signal);
}
