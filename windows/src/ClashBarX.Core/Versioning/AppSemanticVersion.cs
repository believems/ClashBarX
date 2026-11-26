namespace ClashBarX.Core.Versioning;

/// <summary>
/// 语义版本比较（源：AppRelease.swift 的 AppSemanticVersion）：
/// 去掉 build metadata（+）与预发布段（-），从首个数字位起按「.」取各段前导数字，
/// 缺失位补 0 比较。无数字段视为不可解析。
/// </summary>
public readonly struct AppSemanticVersion : IEquatable<AppSemanticVersion>, IComparable<AppSemanticVersion>
{
    private readonly int[] _components;

    private AppSemanticVersion(int[] components) => _components = components;

    public static AppSemanticVersion? Parse(string? rawVersion)
    {
        if (rawVersion is null)
        {
            return null;
        }

        string trimmed = rawVersion.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        string withoutBuildMetadata = trimmed.Split('+')[0];
        string withoutPrerelease = withoutBuildMetadata.Split('-')[0];
        int firstDigitIndex = IndexOfFirstDigit(withoutPrerelease);
        if (firstDigitIndex < 0)
        {
            return null;
        }

        string numericPortion = withoutPrerelease[firstDigitIndex..];
        var components = new List<int>();
        foreach (string segment in numericPortion.Split('.'))
        {
            int digitCount = 0;
            while (digitCount < segment.Length && char.IsDigit(segment[digitCount]))
            {
                digitCount++;
            }

            if (digitCount > 0)
            {
                components.Add(int.Parse(segment[..digitCount]));
            }
        }

        return components.Count > 0 ? new AppSemanticVersion(components.ToArray()) : null;
    }

    public string DisplayString => string.Join(".", Components);

    private int[] Components => _components ?? Array.Empty<int>();

    /// <summary>归一化显示版本（源：normalizedDisplayVersion——可解析取组件；v 前缀剥离；原样兜底）。</summary>
    public static string NormalizedDisplayVersion(string rawVersion)
    {
        if (Parse(rawVersion) is { } parsed)
        {
            return parsed.DisplayString;
        }

        string trimmed = rawVersion.Trim();
        return trimmed.StartsWith('v')
            && trimmed.Length > 1
            && char.IsDigit(trimmed[1])
            ? trimmed[1..]
            : trimmed;
    }

    public bool Equals(AppSemanticVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppSemanticVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (int component in Components)
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    public int CompareTo(AppSemanticVersion other)
    {
        int[] left = Components;
        int[] right = other.Components;
        int maxCount = Math.Max(left.Length, right.Length);

        for (int index = 0; index < maxCount; index++)
        {
            int l = index < left.Length ? left[index] : 0;
            int r = index < right.Length ? right[index] : 0;
            if (l != r)
            {
                return l.CompareTo(r);
            }
        }

        return 0;
    }

    public static bool operator <(AppSemanticVersion left, AppSemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppSemanticVersion left, AppSemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppSemanticVersion left, AppSemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppSemanticVersion left, AppSemanticVersion right) => left.CompareTo(right) >= 0;

    public static bool operator ==(AppSemanticVersion left, AppSemanticVersion right) => left.Equals(right);

    public static bool operator !=(AppSemanticVersion left, AppSemanticVersion right) => !left.Equals(right);

    private static int IndexOfFirstDigit(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsDigit(text[index]))
            {
                return index;
            }
        }

        return -1;
    }
}
