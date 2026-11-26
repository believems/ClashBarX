using System.Runtime.InteropServices;

namespace ClashBarX.App.Core;

/// <summary>
/// WLAN SSID 监听（源：SSIDMonitorService.swift 的 CoreWLAN 对应物；P/Invoke 隔离在 App 层）。
/// 事件源：WLANRegisterNotification（ACM 全量通知）→ 500ms 防抖后重查 SSID，变化才上抛。
/// Windows 读 SSID 无定位授权门槛（macOS 的 authorizationStatus 语义不适用，恒为可用）。
/// </summary>
public sealed class WlanSsidMonitor : IDisposable
{
    /// <summary>事件通知后的防抖（连接完成瞬间查询可能拿不到 SSID，稍候重查）。</summary>
    private static readonly TimeSpan NotificationDebounce = TimeSpan.FromMilliseconds(500);

    private IntPtr _clientHandle;
    private bool _registered;
    private readonly WlanNotificationCallbackDelegate _notificationCallback;
    private System.Threading.Timer? _debounceTimer;
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

            if (WlanRegisterNotification(
                    _clientHandle,
                    unchecked((int)0xFFFFFFFF),   // WLAN_NOTIFICATION_SOURCE_ALL
                    true,                         // 忽略重复通知
                    _notificationCallback,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out _) != 0)
            {
                WlanCloseHandle(_clientHandle, IntPtr.Zero);
                _clientHandle = IntPtr.Zero;
                return;
            }

            _registered = true;
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
            WlanRegisterNotification(_clientHandle, 0, true, _notificationCallback, IntPtr.Zero, IntPtr.Zero, out _);
            WlanCloseHandle(_clientHandle, IntPtr.Zero);
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
        }

        IntPtr interfaceList = IntPtr.Zero;
        try
        {
            int result = WlanEnumInterfaces(_clientHandle, IntPtr.Zero, out interfaceList);
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

        int result = WlanOpenHandle(2, IntPtr.Zero, out IntPtr handle, out _);
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
