using AwesomeAssertions;
using Eryph.GuestServices.Tool.Eryph;

namespace Eryph.GuestServices.Tool.Tests;

public class PublicKeyReaderTests
{
    [Fact]
    public async Task ResolveAsync_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pub");

        (await PublicKeyReader.ResolveAsync(path)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_UnreadableFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pub");
        await File.WriteAllTextAsync(path, "ssh-ed25519 AAAA test");
        try
        {
            // Hold an exclusive lock so the read fails with a sharing violation:
            // an IO failure must surface as "could not read" (null), not a throw.
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            (await PublicKeyReader.ResolveAsync(path)).Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
