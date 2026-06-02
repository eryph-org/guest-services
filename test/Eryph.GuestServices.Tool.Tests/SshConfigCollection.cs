namespace Eryph.GuestServices.Tool.Tests;

// SshConfigHelperTests mutate the static SshConfigHelper.RootPathOverride.
// xunit runs collections in parallel by default, so serialise these tests in a
// single CollectionDefinition to keep them from racing another collection over
// that global.
[CollectionDefinition(nameof(SshConfigCollection), DisableParallelization = true)]
public sealed class SshConfigCollection;
