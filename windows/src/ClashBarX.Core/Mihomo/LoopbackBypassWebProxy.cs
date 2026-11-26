using System.Net;

namespace ClashBarX.Core.Mihomo;

/// <summary>
/// 环回地址绕过代理：对 127.0.0.1 / ::1 / localhost 的请求一律直连，其余地址沿用系统/环境代理。
/// macOS 的 CFNetwork 对环回地址有隐式代理绕过，本类在 Windows 上显式对齐该行为，
/// 避免 ClashBarX 自己设置的系统代理截获发往本机 mihomo 控制器的请求。
/// </summary>
public sealed class LoopbackBypassWebProxy : IWebProxy
{
    private readonly IWebProxy _fallback;

    public LoopbackBypassWebProxy(IWebProxy? fallback = null)
    {
        _fallback = fallback ?? HttpClient.DefaultProxy;
    }

    public ICredentials? Credentials
    {
        get => _fallback.Credentials;
        set => _fallback.Credentials = value;
    }

    public Uri? GetProxy(Uri destination) =>
        destination.IsLoopback ? null : _fallback.GetProxy(destination);

    public bool IsBypassed(Uri host) =>
        host.IsLoopback || _fallback.IsBypassed(host);
}
