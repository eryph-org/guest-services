using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Pty;

public interface IPty
{
    Task StartAsync(uint width, uint height, string command);

    Task<int> WaitForExitAsync(CancellationToken cancellation);

    Task ResizeAsync(uint width, uint height);

    Stream Input { get; }

    Stream Output { get; }
}
