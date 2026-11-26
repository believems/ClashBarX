using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClashBarX.App.Core;

/// <summary>
/// 托盘常驻服务：图标、提示文本、右键菜单（显示主窗 / 退出）与单击唤起主窗。
/// 图标直接以 System.Drawing.Icon 赋给基类 Icon 属性（同步注册，不走
/// IconSource 的异步转换链——该链在部分环境下静默失败，图标不出现在托盘）。
/// 退出仅发出事件，由 App 层执行内核终止与关停（保持职责分离）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly H.NotifyIcon.TaskbarIcon _taskbarIcon;
    private readonly System.Drawing.Icon _icon;

    /// <summary>用户请求显示主窗（托盘单击或菜单）。</summary>
    public event Action? ShowMainWindowRequested;

    /// <summary>用户请求退出应用。</summary>
    public event Action? ExitRequested;

    public TrayIconService()
    {
        var showItem = new MenuItem { Header = "显示主窗(_S)" };
        showItem.Click += (_, _) => ShowMainWindowRequested?.Invoke();

        var exitItem = new MenuItem { Header = "退出(_X)" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        string iconPath = ClashBarXPaths.TrayIconCachePath();
        EnsureIconFile(iconPath);
        _icon = new System.Drawing.Icon(iconPath);

        _taskbarIcon = new H.NotifyIcon.TaskbarIcon
        {
            ToolTipText = "ClashBarX",
            Icon = _icon,
            ContextMenu = menu,
        };
        _taskbarIcon.TrayLeftMouseUp += (_, _) => ShowMainWindowRequested?.Invoke();

        // 无窗应用（StartupUri 移除、实例不挂视觉树）必须显式 ForceCreate 才会注册托盘图标
        //（源：H.NotifyIcon README 的 Windowless 用法）；不开节能模式——代理客户端需要正常调度
        _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose()
    {
        _taskbarIcon.Dispose();
        _icon.Dispose();
    }

    private static void EnsureIconFile(string iconPath)
    {
        if (File.Exists(iconPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        File.WriteAllBytes(iconPath, RenderIconAsIco());
    }

    /// <summary>渲染 32×32 图形（深底白色上箭头）并封装为单图像 ICO 文件（PNG 载荷）。</summary>
    internal static byte[] RenderIconAsIco()
    {
        const int Size = 32;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x2D, 0x33, 0x3B)),
                null,
                new Rect(1, 1, Size - 2, Size - 2),
                7, 7);

            var arrow = new StreamGeometry();
            using (StreamGeometryContext geometry = arrow.Open())
            {
                geometry.BeginFigure(new Point(16, 6), isFilled: true, isClosed: true);
                geometry.LineTo(new Point(26, 17), isStroked: true, isSmoothJoin: false);
                geometry.LineTo(new Point(21, 17), isStroked: true, isSmoothJoin: false);
                geometry.LineTo(new Point(21, 26), isStroked: true, isSmoothJoin: false);
                geometry.LineTo(new Point(11, 26), isStroked: true, isSmoothJoin: false);
                geometry.LineTo(new Point(11, 17), isStroked: true, isSmoothJoin: false);
                geometry.LineTo(new Point(6, 17), isStroked: true, isSmoothJoin: false);
            }

            arrow.Freeze();
            context.DrawGeometry(Brushes.White, null, arrow);
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var pngStream = new MemoryStream();
        encoder.Save(pngStream);
        byte[] png = pngStream.ToArray();

        // ICONDIR(6) + ICONDIRENTRY(16) + PNG 载荷
        using var icoStream = new MemoryStream();
        using var writer = new BinaryWriter(icoStream);
        writer.Write((ushort)0);            // Reserved
        writer.Write((ushort)1);            // Type = icon
        writer.Write((ushort)1);            // Count
        writer.Write((byte)Size);           // Width
        writer.Write((byte)Size);           // Height
        writer.Write((byte)0);              // ColorCount
        writer.Write((byte)0);              // Reserved
        writer.Write((ushort)1);            // Planes
        writer.Write((ushort)32);           // BitCount
        writer.Write((uint)png.Length);     // BytesInRes
        writer.Write((uint)22);             // ImageOffset
        writer.Write(png);
        writer.Flush();
        return icoStream.ToArray();
    }
}
