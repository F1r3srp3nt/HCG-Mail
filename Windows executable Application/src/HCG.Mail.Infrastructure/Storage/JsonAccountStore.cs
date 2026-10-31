using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Settings;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Storage;

/// <summary>
/// Persists account server settings (never passwords) to accounts.json. A versioned DTO keeps
/// the on-disk format independent of the in-memory model.
/// </summary>
public sealed class JsonAccountStore(AppPaths paths, ILogger<JsonAccountStore> logger) : IAccountStore
{
    private readonly object _lock = new();

    public IReadOnlyList<MailAccount> Load()
    {
        lock (_lock)
        {
            var file = JsonFile.Read<AccountsFile>(paths.AccountsFile, logger);
            if (file?.Accounts is null)
                return [];

            var result = new List<MailAccount>();
            foreach (var dto in file.Accounts)
            {
                var account = dto.ToAccount();
                if (account is null)
                {
                    logger.LogWarning("Skipping an invalid account entry in accounts.json");
                    continue;
                }
                result.Add(account);
            }
            return result;
        }
    }

    public void Save(IReadOnlyList<MailAccount> accounts)
    {
        lock (_lock)
            JsonFile.Write(paths.AccountsFile, new AccountsFile { Version = 1, Accounts = accounts.Select(AccountDto.From).ToList() });
    }

    internal sealed class AccountsFile
    {
        public int Version { get; set; }
        public List<AccountDto>? Accounts { get; set; }
    }

    internal sealed class AccountDto
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? EmailAddress { get; set; }
        public string? Username { get; set; }
        public EndpointDto? Imap { get; set; }
        public EndpointDto? Smtp { get; set; }
        public string? PresetId { get; set; }

        public static AccountDto From(MailAccount a) => new()
        {
            Id = a.Id,
            DisplayName = a.DisplayName,
            EmailAddress = a.EmailAddress,
            Username = a.Username,
            Imap = EndpointDto.From(a.Imap),
            Smtp = EndpointDto.From(a.Smtp),
            PresetId = a.PresetId,
        };

        public MailAccount? ToAccount()
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(EmailAddress) || string.IsNullOrWhiteSpace(Username)
                || Imap?.ToEndpoint() is not { } imap || Smtp?.ToEndpoint() is not { } smtp)
                return null;
            return new MailAccount
            {
                Id = Id,
                DisplayName = DisplayName ?? "",
                EmailAddress = EmailAddress,
                Username = Username,
                Imap = imap,
                Smtp = smtp,
                PresetId = PresetId,
            };
        }
    }

    internal sealed class EndpointDto
    {
        public string? Host { get; set; }
        public int Port { get; set; }
        public MailSecurityMode Security { get; set; }

        public static EndpointDto From(ServerEndpoint e) => new() { Host = e.Host, Port = e.Port, Security = e.Security };

        public ServerEndpoint? ToEndpoint() =>
            string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 || !Enum.IsDefined(Security)
                ? null
                : new ServerEndpoint(Host, Port, Security);
    }
}

public sealed class InMemoryAccountStore : IAccountStore
{
    private IReadOnlyList<MailAccount> _accounts = [];

    public IReadOnlyList<MailAccount> Load() => _accounts;

    public void Save(IReadOnlyList<MailAccount> accounts) => _accounts = accounts.ToList();
}
