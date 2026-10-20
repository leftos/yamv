using System.Text.Json.Serialization;

namespace Yamv.Core.Session;

/// <summary>Source-generated serializer metadata, so the session file works under AOT and trimming.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionState))]
internal sealed partial class SessionJsonContext : JsonSerializerContext { }
