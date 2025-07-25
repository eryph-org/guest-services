using System.Diagnostics.CodeAnalysis;

namespace Eryph.GuestServices.Pty;

public interface IPty : IDisposable
{
    [MemberNotNull(nameof(Input), nameof(Output))]
    Task StartAsync(uint width, uint height, string command, string arguments);

    Task<int> WaitForExitAsync(CancellationToken cancellation);

    Task ResizeAsync(uint width, uint height);

    Stream? Input { get; }

    Stream? Output { get; }
}
