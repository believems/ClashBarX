using System.Globalization;

namespace ClashBarX.Core.Proxies;

/// <summary>
/// Provider 展示格式化（源：Core/Utils/Formatters.swift 的 relativeTime /
/// daysUntilExpiryShort / bytesCompactNoSpace，中文文案）。
/// </summary>
public static class ProviderFormatting
{
    /// <summary>距到期不足 15 天的警示阈值（源：expiresSoon 的 15 * 86400）。</summary>
    public const double ExpiryWarningSeconds = 15 * 86400;

    /// <summary>相对时间（分钟/小时/天；解析失败或空为「未知」）。</summary>
    public static string RelativeTime(string? updatedAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(updatedAt))
        {
            return "未知";
        }

        if (!TryParseIso8601(updatedAt.Trim(), out DateTimeOffset date))
        {
            return "未知";
        }

        double interval = Math.Max(0, (now - date).TotalSeconds);
        int minutes = (int)(interval / 60);
        if (minutes < 60)
        {
            return $"{minutes} 分钟前";
        }

        int hours = (int)(interval / 3600);
        if (hours < 24)
        {
            return $"{hours} 小时前";
        }

        return $"{(int)(interval / 86400)} 天前";
    }

    /// <summary>到期天数短文案（0 = 长期；过期/今天/剩余 N 天）。</summary>
    public static string DaysUntilExpiryShort(long? unixSeconds, DateTimeOffset now)
    {
        if (unixSeconds is null)
        {
            return "未知";
        }

        if (unixSeconds == 0)
        {
            return "长期有效";
        }

        if (unixSeconds < 0)
        {
            return "未知";
        }

        double seconds = unixSeconds.Value - now.ToUnixTimeSeconds();
        if (seconds < 0)
        {
            return "已过期";
        }

        int days = (int)Math.Floor(seconds / 86400);
        return days <= 0 ? "今天到期" : $"{days} 天后到期";
    }

    /// <summary>到期是否临近（源：expiresSoon——有效且余量 < 15 天）。</summary>
    public static bool ExpiresSoon(long? unixSeconds, DateTimeOffset now) =>
        unixSeconds is { } expire
        && expire > 0
        && expire - now.ToUnixTimeSeconds() < ExpiryWarningSeconds;

    /// <summary>紧凑字节文案（TB/GB/MB/KB；接近整数不带小数；源：bytesCompactNoSpace）。</summary>
    public static string BytesCompactNoSpace(long value)
    {
        double normalized = Math.Max(0, value);
        return normalized switch
        {
            >= 1024L * 1024 * 1024 * 1024 => CompactNoSpace(normalized / (1024L * 1024 * 1024 * 1024), "TB"),
            >= 1024L * 1024 * 1024 => CompactNoSpace(normalized / (1024L * 1024 * 1024), "GB"),
            >= 1024L * 1024 => CompactNoSpace(normalized / (1024L * 1024), "MB"),
            _ => CompactNoSpace(normalized / 1024, "KB"),
        };
    }

    private static string CompactNoSpace(double value, string unit) =>
        Math.Abs(Math.Round(value) - value) < 0.05
            ? $"{(int)Math.Round(value)}{unit}"
            : string.Create(CultureInfo.InvariantCulture, $"{value:F1}{unit}");

    /// <summary>ISO8601 解析（带 Z/偏移/无时区三种形态；源：parseISO8601Date）。</summary>
    internal static bool TryParseIso8601(string text, out DateTimeOffset date)
    {
        if (DateTimeOffset.TryParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out date))
        {
            return true;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);
    }
}
