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
//   When adding an enum to this JSON surface, annotate the enum type itself
//   with the trim-safe generic converter to preserve snake_case strings:
//
//       [JsonConverter(typeof(JsonStringEnumConverter<MyEnum>))]
//       public enum MyEnum { ... }
//
//   and pass `JsonNamingPolicy.SnakeCaseLower` if the converter ctor is used
//   directly. Failing to do so will silently emit numeric or PascalCase values
//   and break external consumers of the `--json` output.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, JsonElement>>))]
[JsonSerializable(typeof(string))]
internal partial class GuestDataJsonContext : JsonSerializerContext;
