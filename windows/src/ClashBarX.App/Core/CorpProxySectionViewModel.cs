using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using ClashBarX.Core.CorpProxy;

namespace ClashBarX.App.Core;

/// <summary>
/// 内网前置代理区块（Issue #2，源：macOS SystemTabView corp 区块 + @AppStorage 群）：
/// 开关 + 服务器/端口/用户名/密码。字段变化发 <see cref="Changed"/>（App 层持久化），
/// 回车/失焦/开关切换发 <see cref="Committed"/>（App 层 PUT /configs 热重载，源：onSubmit 语义）。
/// 端口仅接受 1-65535，非法输入不回写（源：corpProxyPortText 绑定语义）。
/// </summary>
public sealed class CorpProxySectionViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>任一字段变化（App：写设置文件；不触发内核重载）。</summary>
    public event Action? Changed;

    /// <summary>提交语义（回车/失焦/开关切换；App：热重载配置，内核未跑则下次启动生效）。</summary>
    public event Action? Committed;

    private bool _isEnabled;
    private string _host = string.Empty;
    private int _port = 8080;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _restoring;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value))
            {
                return;
            }

            // 开关切换即生效（源：onChange(of: corpProxyEnabled) → reload）
            if (!_restoring)
            {
                Committed?.Invoke();
            }
        }
    }

    public string Host
    {
        get => _host;
        set => Set(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => Set(ref _port, value);
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    /// <summary>端口输入框文本：合法才回写模型，非法输入留在框里不污染设置（源：corpProxyPortText）。</summary>
    public string PortText
    {
        get => Port.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value.Trim(), out int parsed) && parsed is >= 1 and <= 65535)
            {
                Port = parsed;
            }
        }
    }

    public CorpProxySettings ToSettings() => new(IsEnabled, Host, Port, Username, Password);

    /// <summary>启动恢复（不触发持久化与重载）。</summary>
    public void Restore(CorpProxySettings settings)
    {
        _restoring = true;
        try
        {
            IsEnabled = settings.IsEnabled;
            Host = settings.Host;
            Port = settings.Port is >= 1 and <= 65535 ? settings.Port : 8080;
            Username = settings.Username;
            Password = settings.Password;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>回车/失焦时由视图调用：触发内核热重载。</summary>
    public void Commit()
    {
        if (!_restoring)
        {
            Committed?.Invoke();
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (!_restoring)
        {
            Changed?.Invoke();
        }

        return true;
    }
}
