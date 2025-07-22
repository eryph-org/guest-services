using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Pty.Linux;

public sealed class LinuxPty : IPty, IDisposable
{
    public Stream Input => throw new NotImplementedException();

    public Stream Output => throw new NotImplementedException();

    public Task StartAsync(uint width, uint height, string command)
    {
        throw new NotImplementedException();
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        throw new NotImplementedException();
    }

    public Task ResizeAsync(uint width, uint height)
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
