namespace ClashBarX.Core.Mihomo;

/// <summary>HTTP 方法（源：MihomoAPIService.swift 的 HTTPMethod 枚举）。</summary>
public enum MihomoHttpMethod
{
    Get,
    Put,
    Post,
    Patch,
    Delete,
}

public static class MihomoHttpMethodExtensions
{
    /// <summary>映射到 <see cref="System.Net.Http.HttpMethod"/> 实例。</summary>
    public static HttpMethod ToHttpMethod(this MihomoHttpMethod method) => method switch
    {
        MihomoHttpMethod.Get => HttpMethod.Get,
        MihomoHttpMethod.Put => HttpMethod.Put,
        MihomoHttpMethod.Post => HttpMethod.Post,
        MihomoHttpMethod.Patch => HttpMethod.Patch,
        MihomoHttpMethod.Delete => HttpMethod.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    /// <summary>HTTP 报文中的方法名。</summary>
    public static string ToHttpString(this MihomoHttpMethod method) => method switch
    {
        MihomoHttpMethod.Get => "GET",
        MihomoHttpMethod.Put => "PUT",
        MihomoHttpMethod.Post => "POST",
        MihomoHttpMethod.Patch => "PATCH",
        MihomoHttpMethod.Delete => "DELETE",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };
}
