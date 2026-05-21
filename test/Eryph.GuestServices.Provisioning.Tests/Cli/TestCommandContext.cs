using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Tests.Cli;

/// <summary>
/// Spectre.Console.Cli does not expose a parameterless factory for
/// <see cref="CommandContext"/>; tests need to feed one to a command's
/// <c>ExecuteAsync</c>. This helper builds an inert context whose values
/// are never inspected by our commands but satisfy the type signature.
/// </summary>
internal static class TestCommandContext
{
    public static CommandContext Create(string commandName = "test") =>
        new(Array.Empty<string>(), new EmptyRemainingArguments(), commandName, data: null);

    private sealed class EmptyRemainingArguments : IRemainingArguments
    {
        public ILookup<string, string?> Parsed { get; } =
            Array.Empty<KeyValuePair<string, string?>>().ToLookup(kv => kv.Key, kv => kv.Value);

        public IReadOnlyList<string> Raw { get; } = Array.Empty<string>();
    }
}
