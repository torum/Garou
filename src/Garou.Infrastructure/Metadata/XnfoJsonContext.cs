using System.Text.Json.Serialization;

namespace Garou.Infrastructure.Metadata;

[JsonSerializable(typeof(List<string>))]
internal partial class XnfoJsonContext : JsonSerializerContext
{
}
