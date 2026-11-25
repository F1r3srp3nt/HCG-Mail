using System.Text.Json.Serialization;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Settings;

namespace HCG.Mail.Infrastructure.Storage;

/// <summary>
/// Source-generated serialization for everything HCG Mail persists (settings.json, accounts.json,
/// delivery-history.json). No reflection: safe under trimming and full AOT (the iOS Release build, where Mono runs
/// AOT-compiled code only). The options produce exactly the JSON the reflection-based serializer wrote before
/// (camelCase, enums as names, nulls omitted, indented), so existing files keep loading
/// (StorageTests.Source_generated_json_matches_the_previous_format).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(JsonAccountStore.AccountsFile))]
[JsonSerializable(typeof(List<DeliveryRecord>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
