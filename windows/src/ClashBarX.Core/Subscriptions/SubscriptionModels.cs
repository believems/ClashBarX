using System.Text.Json;

namespace ClashBarX.Core.Subscriptions;

/// <summary>订阅记录（源：RemoteConfigSubscription 的稳定子集；文件名是存储键）。</summary>
public sealed record SubscriptionInfo(
    string Url,
    string FileName,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastSuccessAt);

/// <summary>订阅存储的持久化根（subscriptions.json）。</summary>
public sealed record SubscriptionStore(
    IReadOnlyList<SubscriptionInfo> Subscriptions,
    string? SelectedFileName)
{
    public static readonly SubscriptionStore Empty = new(Array.Empty<SubscriptionInfo>(), null);
}

/// <summary>订阅操作异常（对应 ConfigImportService 的 NSError 语义）。</summary>
public sealed class SubscriptionException : Exception
{
    public SubscriptionException(string message)
        : base(message)
    {
    }
}

internal static class SubscriptionStoreJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
