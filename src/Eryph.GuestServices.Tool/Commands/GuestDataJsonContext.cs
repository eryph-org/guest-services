using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eryph.GuestServices.Tool.Commands;

// Source-generation context for the `egs-tool get-data --json` output.
//
// Convention for new enum fields on this JSON surface:
//   The original implementation registered a global
//   `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)` so any enum
//   that ever entered the payload would be serialized as a snake_case string.
//   That converter is reflection-based and not trim-safe; there is no global
//   trim-safe equivalent in System.Text.Json.
//
//   The trim-safe substitute is per-enum. The generic
//   `JsonStringEnumConverter<TEnum>` exists, but the `[JsonConverter]`
//   attribute can only invoke a parameterless constructor — so applying it
//   directly produces PascalCase names, not snake_case. Subclass the
//   generic converter with a parameterless constructor that supplies the
//   naming policy, then attach the subclass to each enum:
//
//       public sealed class SnakeCaseLowerEnumConverter<TEnum>()
//           : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.SnakeCaseLower)
//           where TEnum : struct, Enum;
//
//       [JsonConverter(typeof(SnakeCaseLowerEnumConverter<MyEnum>))]
//       public enum MyEnum { ... }
//
//   Forgetting to add the converter (or using the bare
//   `JsonStringEnumConverter<TEnum>`) silently emits PascalCase values and
//   breaks external consumers of the `--json` output.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, JsonElement>>))]
[JsonSerializable(typeof(string))]
internal partial class GuestDataJsonContext : JsonSerializerContext;
