namespace Eryph.GuestServices.Provisioning.Tests.Cli;

// Tests that mutate ProvisioningPaths.RootOverride must not run in parallel
// with each other (xunit defaults to parallel collections). Putting them in a
// single CollectionDefinition serialises them.
[CollectionDefinition(nameof(ProvisioningPathsCollection), DisableParallelization = true)]
public sealed class ProvisioningPathsCollection;
