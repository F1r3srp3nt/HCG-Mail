using System.Text.Json.Serialization;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;

namespace HCG.Mail.Infrastructure.Storage;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CachedValue<IReadOnlyList<MailFolder>>))]
[JsonSerializable(typeof(CachedValue<FolderPage>))]
[JsonSerializable(typeof(CachedValue<MessageContent>))]
[JsonSerializable(typeof(JsonOutboxStore.OutboxFile))]
[JsonSerializable(typeof(JsonOutboxStore.OutboxEnvelope))]
internal sealed partial class MailStorageJsonContext : JsonSerializerContext;
