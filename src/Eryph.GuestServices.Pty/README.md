# Pseudo terminal (PTY) abstractions
This project contains logic to create pseudo terminals (PTYs) and manage their lifecycle.
It considers three distinct cases:
- Modern Windows (Windows 1809 or later): these versions of Windows have native PTY support.
  We need to invoke the Win32 APIs directly as no support is available in .NET.
- Legacy Windows: older versions of Windows do not support PTYs natively.
  We use the `ssh-shellhost.exe` from Windows OpenSSH project.
- Linux: native support for PTY exists. We must use a small native helper library for
  starting the PTY process. The helper can be found here: [spawnpty](./../spawnpty/).

The code for the native Windows support is based on a sample from the Microsoft Terminal project.
See https://github.com/microsoft/terminal. Licensed under the MIT License.

The `ssh-shellhost.exe` helper is part of the Windows OpenSSH project.
See https://github.com/PowerShell/openssh-portable. Licensed as follows:
[LICENSE.TXT](./native/win-x64/LICENSE.txt).

## Design notes
- There is also the winpty library which provides PTY support for legacy Windows.
  The library does not seem to be maintained anymore and seemingly requires an
  executable as well.
- There are some older nuget packages (e.g. `vs-pty`) which provide PTY support for Linux.
  Unfortunately,  these packages no longer work with .NET 8+. These packages just perform
  the necessary libc calls with `[DllImport]` with include a `fork()` and `exec`. This
  fails in .NET 8+ due to security changes in the runtime but also has never been supported.
  See https://github.com/dotnet/runtime/issues/95890.