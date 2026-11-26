using System.Text;
using System.Windows;

namespace ClashBarX.App;

/// <summary>
/// M1 骨架主窗：内核状态与运行日志的只读视图；关闭按钮最小化到托盘（常驻），
/// 退出走托盘菜单。
/// </summary>
public partial class MainWindow : Window
{
    private readonly StringBuilder _logBuffer = new(capacity: 4096);

    public MainWindow()
    {
        InitializeComponent();
    }

    public void SetCoreStatus(string status) => CoreStatusText.Text = status;

    /// <summary>追加一行运行日志并滚动到底。调用方负责 UI 线程编组。</summary>
    public void AppendLog(string line)
    {
        _logBuffer.AppendLine(line);
        LogTextBox.Text = _logBuffer.ToString();
        LogTextBox.ScrollToEnd();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关窗 = 隐藏到托盘，保持常驻；真正的退出在托盘菜单
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
