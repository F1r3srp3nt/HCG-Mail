using HCG.Mail.Core.Delivery;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Storage;

/// <summary>
/// Keeps the most recent send attempts in <c>delivery-history.json</c> (private to the user, like the other
/// settings files). Records hold timestamps, the Message-ID, recipient counts and the server's reply — no subject,
/// body or attachments.
/// </summary>
public sealed class JsonDeliveryLog : IDeliveryLog
{
    public const int MaxRecords = 300;

    private readonly string _path;
    private readonly ILogger<JsonDeliveryLog> _logger;
    private readonly Lock _lock = new();
    private List<DeliveryRecord>? _records;

    public JsonDeliveryLog(AppPaths paths, ILogger<JsonDeliveryLog> logger)
    {
        _path = paths.DeliveryHistoryFile;
        _logger = logger;
    }

    public void Add(DeliveryRecord record)
    {
        lock (_lock)
        {
            var records = Load();
            records.Add(record);
            if (records.Count > MaxRecords)
                records.RemoveRange(0, records.Count - MaxRecords);
            JsonFile.Write(_path, records);
        }
    }

    public IReadOnlyList<DeliveryRecord> GetRecent(string? accountId = null, int max = 100)
    {
        lock (_lock)
            return Load().Where(r => accountId is null || r.AccountId == accountId).OrderByDescending(r => r.Timestamp).Take(max).ToList();
    }

    public void Clear(string? accountId = null)
    {
        lock (_lock)
        {
            var records = Load();
            records.RemoveAll(r => accountId is null || r.AccountId == accountId);
            JsonFile.Write(_path, records);
        }
    }

    private List<DeliveryRecord> Load() => _records ??= JsonFile.Read<List<DeliveryRecord>>(_path, _logger) ?? [];
}
