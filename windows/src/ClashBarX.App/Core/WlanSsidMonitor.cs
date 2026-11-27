using System.Runtime.InteropServices;

namespace ClashBarX.App.Core;

/// <summary>
/// WLAN SSID 监听（源：SSIDMonitorService.swift 的 CoreWLAN 对应物；P/Invoke 隔离在 App 层）。
/// 事件源：WLANRegisterNotification（ACM 全量通知）→ 500ms 防抖后重查 SSID，变化才上抛。
/// 兼容回退：本机 WLAN 服务只协商 v1 句柄时（Vista+ 接口不可用，实测 Win11 24H2 部分
/// 驱动栈如此），注册失败自动降级为 netsh 解析 + 2s 轮询（源 CWEventDelegate 事件驱动的
/// Windows 回退对应物；netsh 字段名不本地化，可稳定解析）。
/// Windows 读 SSID 无定位授权门槛（macOS 的 authorizationStatus 语义不适用，恒为可用）。
/// </summary>
public sealed class WlanSsidMonitor : IDisposable
{
    /// <summary>事件通知后的防抖（连接完成瞬间查询可能拿不到 SSID，稍候重查）。</summary>
    private static readonly TimeSpan NotificationDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>netsh 回退轮询间隔（事件驱动不可用时的替代）。</summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);

    private IntPtr _clientHandle;
    private bool _registered;
    /// <summary>netsh 轮询模式（WLAN API 不可用时的回退）。</summary>
    private bool _pollingMode;
    private readonly WlanNotificationCallbackDelegate _notificationCallback;
    private System.Threading.Timer? _debounceTimer;
    private System.Threading.Timer? _pollingTimer;
    private string? _lastSsid;
    private readonly object _gate = new();

    /// <summary>SSID 变化（null = 未连接/不可读；可能从 WLAN 回调线程发出，UI 编组由订阅方承担）。</summary>
    public event Action<string?>? SsidChanged;

    public WlanSsidMonitor()
    {
        // 委托必须保持强引用，否则 GC 回收后本机回调会崩
        _notificationCallback = OnWlanNotification;
    }

    /// <summary>启动监听（注册通知 + 首查上报）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_registered)
            {
                return;
            }

            if (!TryOpenHandle())
            {
                return;
            }

            int registerResult = WlanRegisterNotification(
                    _clientHandle,
                    unchecked((int)0xFFFFFFFF),   // WLAN_NOTIFICATION_SOURCE_ALL
                    true,                         // 忽略重复通知
                    _notificationCallback,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out _);
            Diag($"WlanRegisterNotification rc={registerResult}");
            if (registerResult != 0)
            {
                // WLAN API 不可用（v1 句柄环境）：降级 netsh 轮询（备案：事件驱动的回退对应物）
                WlanCloseHandle(_clientHandle, IntPtr.Zero);
                _clientHandle = IntPtr.Zero;
                _pollingMode = true;
                _registered = true;
                _pollingTimer = new System.Threading.Timer(
                    _ => PublishCurrentSsid(), null, PollingInterval, PollingInterval);
            }
            else
            {
                _registered = true;
            }
        }

        PublishCurrentSsid();
    }

    /// <summary>停止监听（注销通知、关闭句柄）。timer 摘除在锁内、Dispose 在锁外（防回调互等死锁）。</summary>
    public void Stop()
    {
        System.Threading.Timer? staleTimer;
        lock (_gate)
        {
            if (!_registered)
            {
                return;
            }

            _registered = false;
            staleTimer = _debounceTimer;
            _debounceTimer = null;
            if (_pollingMode)
            {
                _pollingTimer?.Dispose();
                _pollingTimer = null;
                _pollingMode = false;
            }
            else
            {
                WlanRegisterNotification(_clientHandle, 0, true, _notificationCallback, IntPtr.Zero, IntPtr.Zero, out _);
                WlanCloseHandle(_clientHandle, IntPtr.Zero);
            }

            _clientHandle = IntPtr.Zero;
        }

        staleTimer?.Dispose();
    }

    /// <summary>立即查询当前 SSID（trim 非空；不可读返回 null；源：currentSSID 的多接口遍历）。</summary>
    public string? QueryCurrentSsid()
    {
        lock (_gate)
        {
            if (!_registered)
            {
                return null;
            }

            if (_pollingMode)
            {
                return QueryCurrentSsidViaNetsh();
            }
        }

        IntPtr interfaceList = IntPtr.Zero;
        try
        {
            int result = WlanEnumInterfaces(_clientHandle, IntPtr.Zero, out interfaceList);
            Diag($"WlanEnumInterfaces rc={result}");
            if (result != 0)
            {
                return null;
            }

            int entryOffset = Marshal.SizeOf<WlanInterfaceInfoListHeader>();
            int entrySize = Marshal.SizeOf<WlanInterfaceInfo>();
            int count = Marshal.ReadInt32(interfaceList, 0);
            for (int index = 0; index < count; index++)
            {
                IntPtr entry = interfaceList + entryOffset + index * entrySize;
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(entry);
                if (QueryInterfaceSsid(info.InterfaceGuid, out string? ssid))
                {
                    return ssid;
                }
            }

            return null;
        }
        finally
        {
            if (interfaceList != IntPtr.Zero)
            {
                WlanFreeMemory(interfaceList);
            }
        }
    }

    /// <summary>当前 SSID 上报（变化才发事件；返回查询结果）。</summary>
    public string? PublishCurrentSsid()
    {
        string? ssid = QueryCurrentSsid();
        PublishIfChanged(ssid);
        return ssid;
    }

    private void PublishIfChanged(string? ssid)
    {
        string? normalized = string.IsNullOrWhiteSpace(ssid) ? null : ssid.Trim();
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_lastSsid, normalized, StringComparison.Ordinal);
            if (changed)
            {
                _lastSsid = normalized;
            }
        }

        if (changed)
        {
            SsidChanged?.Invoke(normalized);
        }
    }

    /// <summary>P/Invoke 返回码诊断（验收排障；写入 AppData\ClashBarX\wlan-diag.log）。</summary>
    private static void Diag(string message) => System.IO.File.AppendAllText(
        System.IO.Path.Combine(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClashBarX"),
            "wlan-diag.log"),
        $"[{DateTime.Now:HH:mm:ss.fff}] {message}" + Environment.NewLine);

    private void OnWlanNotification(ref WlanNotificationData data, IntPtr context)
    {
        // 任何 WLAN 通知后防抖重查（连接完成瞬间状态可能未就绪）。
        // 旧 timer 在锁外 Dispose：Dispose 会等待执行中的回调，而回调（PublishCurrentSsid）
        // 要取 _gate——锁内 Dispose 会与之互等死锁（评审 P1）。
        System.Threading.Timer? staleTimer;
        lock (_gate)
        {
            if (!_registered)
            {
                return;
            }

            staleTimer = _debounceTimer;
            _debounceTimer = new System.Threading.Timer(
                _ => PublishCurrentSsid(),
                null,
                NotificationDebounce,
                Timeout.InfiniteTimeSpan);
        }

        staleTimer?.Dispose();
    }

    private bool TryOpenHandle()
    {
        if (_clientHandle != IntPtr.Zero)
        {
            return true;
        }

        int result = WlanOpenHandle(2, IntPtr.Zero, out IntPtr handle, out int negotiated);
        Diag($"WlanOpenHandle rc={result} negotiated={negotiated}");
        if (result != 0)
        {
            return false;   // WLAN 服务不可用（源：servicesDisabled 的对应物）
        }

        _clientHandle = handle;
        return true;
    }

    /// <summary>查单接口的当前 SSID（wlan_intf_opcode_current_connection → DOT11_SSID）。
    /// 布局由结构体定义保证（strProfileName 为 WCHAR[256] 占 512 字节——
    /// dot11Ssid 的偏移是 4+4+512=520，评审 P0：手写偏移曾误按 256 字节计算）。</summary>
    private bool QueryInterfaceSsid(Guid interfaceGuid, out string? ssid)
    {
        ssid = null;
        IntPtr data = IntPtr.Zero;
        try
        {
            int result = WlanQueryInterface(
                _clientHandle,
                ref interfaceGuid,
                11,   // wlan_intf_opcode_current_connection
                IntPtr.Zero,
                out int dataSize,
                out data,
                IntPtr.Zero);
            if (result != 0 || dataSize < Marshal.SizeOf<WlanConnectionAttributes>())
            {
                Diag($"WlanQueryInterface rc={result} dataSize={dataSize} need={Marshal.SizeOf<WlanConnectionAttributes>()}");
                return false;
            }

            WlanConnectionAttributes attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
            int ssidLength = attributes.Dot11Ssid.SsidLength;
            if (ssidLength <= 0 || ssidLength > 32)
            {
                return false;
            }

            ssid = System.Text.Encoding.UTF8.GetString(attributes.Dot11Ssid.Ssid, 0, ssidLength);
            return !string.IsNullOrWhiteSpace(ssid);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                WlanFreeMemory(data);
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>netsh 解析当前 SSID（`netsh wlan show interfaces` 的 SSID 行；字段名不本地化）。</summary>
    internal static string? QueryCurrentSsidViaNetsh()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo("netsh", "wlan show interfaces")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill();
                return null;
            }

            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r').Trim();
                // 「SSID : xxx」（跳过 BSSID——前缀锚定）
                if (trimmed.StartsWith("SSID", StringComparison.Ordinal) && !trimmed.StartsWith("BSSID", StringComparison.Ordinal))
                {
                    int colon = trimmed.IndexOf(':');
                    if (colon > 0)
                    {
                        string value = trimmed[(colon + 1)..].Trim();
                        return value.Length > 0 ? value : null;
                    }
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;   // netsh 不可用按未连接处理
        }
    }

    // ---- wlanapi.dll 互操作（P/Invoke 一律隔离在 App 层） ----

    /// <summary>WLAN_CONNECTION_ATTRIBUTES 前缀（只声明到 dot11Ssid；尾部字段不读）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public int IsState;

        public int WlanConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]   // WLAN_MAX_NAME_LENGTH（WCHAR → 512 字节）
        public string StrProfileName;

        public Dot11Ssid Dot11Ssid;
    }

    /// <summary>DOT11_SSID（uSSIDLength + ucSSID[32]）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public int SsidLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanInterfaceInfoListHeader
    {
        public int NumberOfItems;

        public int Index;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string InterfaceDescription;

        public int IsState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanNotificationData
    {
        public int NotificationSource;

        public int NotificationCode;

        public Guid InterfaceGuid;

        public int DataSize;

        public IntPtr Data;
    }

    private delegate void WlanNotificationCallbackDelegate(ref WlanNotificationData data, IntPtr context);

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(
        int negotiatedVersion, IntPtr reserved, out IntPtr clientHandle, out int negotiatedVersionOut);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(
        IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        int opcode,
        IntPtr reserved,
        out int dataSize,
        out IntPtr data,
        IntPtr opcodeValue);

    [DllImport("wlanapi.dll")]
    private static extern int WlanRegisterNotification(
        IntPtr clientHandle,
        int notificationSource,
        bool ignoreDuplicate,
        WlanNotificationCallbackDelegate callback,
        IntPtr callbackContext,
        IntPtr reserved,
        out IntPtr previousCallback);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
