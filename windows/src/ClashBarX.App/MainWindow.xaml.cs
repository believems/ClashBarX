using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ClashBarX.App;

/// <summary>
/// M2 骨架主窗：内核状态、系统代理开关与运行日志的只读视图；关闭按钮最小化到托盘（常驻），
/// 退出走托盘菜单。开关事件上抛 App 层处理（窗口不持业务）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly StringBuilder _logBuffer = new(capacity: 4096);
    private bool _suppressToggleEvents;

    /// <summary>用户切换系统代理开关（参数为新状态）。</summary>
    public event Action<bool>? SystemProxyToggled;

    public MainWindow()
    {
        InitializeComponent();
        SystemProxyToggle.Checked += (_, _) => RaiseToggled(true);
        SystemProxyToggle.Unchecked += (_, _) => RaiseToggled(false);
    }

    public void SetCoreStatus(string status) => CoreStatusText.Text = status;

    /// <summary>程序化同步开关勾选态（不回环触发事件）。调用方负责 UI 线程编组。</summary>
    public void SetSystemProxyChecked(bool isChecked)
    {
        _suppressToggleEvents = true;
        try
        {
            SystemProxyToggle.IsChecked = isChecked;
        }
        finally
        {
            _suppressToggleEvents = false;
        }
    }

    /// <summary>追加一行运行日志并滚动到底。调用方负责 UI 线程编组。</summary>
    public void AppendLog(string line)
    {
        _logBuffer.AppendLine(line);
        LogTextBox.Text = _logBuffer.ToString();
        LogTextBox.ScrollToEnd();
    }

    private void RaiseToggled(bool isChecked)
    {
        if (!_suppressToggleEvents)
        {
            SystemProxyToggled?.Invoke(isChecked);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关窗 = 隐藏到托盘，保持常驻；真正的退出在托盘菜单
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
