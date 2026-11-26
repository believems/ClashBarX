namespace ClashBarX.Core.SystemProxy;

/// <summary>
/// 系统代理读写通道（Windows 实现位于 App 层：注册表 HKCU\...\Internet Settings +
/// InternetSetOption 广播；本接口在 Core 供编排逻辑与单测替换）。
/// </summary>
public interface ISystemProxyController
{
    /// <summary>读取当前系统代理快照（ProxyEnable / ProxyServer / ProxyOverride）。</summary>
    SystemProxySnapshot Read();

    /// <summary>
    /// 启用系统代理：ProxyEnable=1、ProxyServer=host:port、ProxyOverride=绕过列表，
    /// 随后广播设置变更（源：迁移指南 §5 的注册表方案）。
    /// </summary>
    void Enable(SystemProxyTarget target);

    /// <summary>禁用系统代理：仅翻 ProxyEnable=0（保留 Server/Override 值，可再启用），随后广播。</summary>
    void Disable();
}
