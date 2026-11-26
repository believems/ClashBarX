using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace ClashBarX.App.Core;

/// <summary>
/// 托盘常驻服务：图标、提示文本、右键菜单（显示主窗 / 退出）与单击唤起主窗。
/// 退出仅发出事件，由 App 层执行内核终止与关停（保持职责分离）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _icon;

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

        _icon = new TaskbarIcon
        {
            ToolTipText = "ClashBarX",
            IconSource = CreateIconSource(),
            ContextMenu = menu,
        };
        _icon.TrayLeftMouseUp += (_, _) => ShowMainWindowRequested?.Invoke();
    }

    public void Dispose() => _icon.Dispose();

    /// <summary>
    /// 程序化生成托盘图标（深底白色上箭头），不依赖外部资源文件。
    /// H.NotifyIcon 的 IconSource 仅支持带 UriSource 的 BitmapImage，
    /// 因此首次运行把渲染结果封装成 ICO 文件缓存到数据目录再以 Uri 加载。
    /// </summary>
    internal static ImageSource CreateIconSource()
    {
        string iconPath = ClashBarXPaths.TrayIconCachePath();
        EnsureIconFile(iconPath);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(iconPath);
        image.EndInit();
        image.Freeze();
        return image;
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

    /// <summary>渲染 32×32 图形并封装为单图像 ICO 文件（PNG 载荷，Vista+ 原生支持）。</summary>
    private static byte[] RenderIconAsIco()
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
