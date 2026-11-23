using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Port;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private readonly bool _isAdmin;
    private readonly TrayService _tray;
    private IReadOnlyList<PortEntry> _all = [];
    private string _snapSig = "";
    private string _filterSig = "";
    private bool _refreshing;
    private bool _exitRequested;
    private bool _suppressStartup;
    private bool _ignoreTrayToggle;
    private bool _hiding;
    private bool _forcingState;
    private string? _notice;

    public MainWindow()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        using (var identity = WindowsIdentity.GetCurrent())
            _isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        AdminText = _isAdmin ? "管理员" : "标准用户";
        AdminBrush = Freeze(_isAdmin ? Color.FromRgb(0x8F, 0xD0, 0xB0) : Color.FromRgb(0xC9, 0xB0, 0xA0));
        ElevateVisibility = _isAdmin ? Visibility.Collapsed : Visibility.Visible;
        DataContext = this;
        InitializeComponent();
        _tray = new TrayService(ToggleFromTray);
        Dispatcher.BeginInvoke(BeginSession, DispatcherPriority.ApplicationIdle);
    }

    private void BeginSession()
    {
        Refresh();
        if (AutoRefresh)
            _timer.Start();
        SyncStartup();
    }

    public ObservableCollection<RowVm> Rows { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
                ApplyFilter();
        }
    }

    public bool IncludeSystem
    {
        get => _includeSystem;
        set
        {
            if (Set(ref _includeSystem, value))
                ApplyFilter();
        }
    }

    public bool IncludeUdp
    {
        get => _includeUdp;
        set
        {
            if (Set(ref _includeUdp, value))
                ApplyFilter();
        }
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (!Set(ref _autoRefresh, value) || !IsLoaded)
                return;
            if (value)
                _timer.Start();
            else
                _timer.Stop();
        }
    }

    public string AdminText { get; }
    public Brush AdminBrush { get; }
    public Visibility ElevateVisibility { get; }

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (_startupEnabled == value)
                return;
            if (!_suppressStartup)
            {
                try
                {
                    StartupTask.SetEnabled(value);
                    StartupPreference.Save(value);
                }
                catch (Exception ex)
                {
                    StatusText = Stamp("开机启动没有写上：" + ex.Message);
                    return;
                }
            }

            Set(ref _startupEnabled, value);
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string CountText { get => _countText; private set => Set(ref _countText, value); }
    public Visibility EmptyVisibility { get => _emptyVisibility; private set => Set(ref _emptyVisibility, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_exitRequested)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    public void BringToFront()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(BringToFront);
            return;
        }

        Show();
        WindowState = WindowState.Maximized;
        Activate();
    }

    private void OnPreviewKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            Refresh();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_forcingState || _hiding || !IsVisible || _exitRequested || WindowState == WindowState.Maximized)
            return;
        _forcingState = true;
        WindowState = WindowState.Maximized;
        _forcingState = false;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!IsVisible || _exitRequested || _hiding)
            return;
        HideToTray();
        // A tray left-click deactivates the window before MouseClick. Ignore that click so it stays hidden.
        _ignoreTrayToggle = true;
        Dispatcher.BeginInvoke(() => _ignoreTrayToggle = false, DispatcherPriority.ApplicationIdle);
    }

    private void OnElevate(object sender, RoutedEventArgs e) => RelaunchElevated();

    private void OnExit(object sender, RoutedEventArgs e) => ExitApp();

    private void OnListCardSize(object sender, SizeChangedEventArgs e)
    {
        ListCard.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 12, 12);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || e.OriginalSource is not DependencyObject source)
            return;

        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: RowVm row })
            {
                var text = $"{row.Address}:{row.Port}  {row.ProcessName}  {row.Origin}  PID {row.Pid}  {row.PathTip}";
                try
                {
                    Clipboard.SetText(text);
                    _notice = $"已复制 {row.Address}:{row.Port}";
                    StatusText = Stamp(_notice);
                }
                catch
                {
                    StatusText = Stamp("复制失败");
                }

                return;
            }
        }
    }

    private void Refresh()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var list = await Task.Run(PortQuery.Snapshot);
            if (_exitRequested)
                return;
            var sig = string.Join('\n', list.Select(entry =>
                $"{entry.Protocol}|{entry.Address}|{entry.Port}|{entry.Pid}|{entry.ProcessName}|{entry.Path}|{entry.Kind}|{entry.Origin}"));
            if (sig != _snapSig)
            {
                _snapSig = sig;
                _all = list;
                var tcpCount = list.Count(entry => entry.Protocol == "TCP");
                _tray.SetTip($"本机端口与进程面板 · {tcpCount} 个 TCP");
            }

            ApplyFilter();
            StatusText = Stamp(_notice ?? "已更新 · 只读本机，不访问网络");
            _notice = null;
        }
        catch (Exception ex)
        {
            StatusText = Stamp("读取失败：" + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyFilter()
    {
        var filtered = _all.Where(entry =>
        {
            if (!IncludeSystem && entry.Origin == "系统")
                return false;
            if (!IncludeUdp && entry.Protocol == "UDP")
                return false;
            if (string.IsNullOrWhiteSpace(_searchText))
                return true;
            var blob = $"{entry.Protocol} {entry.Address} {entry.AddressHint} {entry.Port} {entry.ProcessName} {entry.Path} {entry.Pid} {entry.Kind} {entry.Origin}";
            return blob.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase);
        }).ToList();

        var sig = string.Join('\n', filtered.Select(entry => $"{entry.Protocol}|{entry.Address}|{entry.Port}|{entry.Pid}|{entry.Path}|{entry.Kind}|{entry.Origin}"))
                  + $"|{_searchText}|{_includeUdp}|{_includeSystem}";
        var third = filtered.Count(entry => entry.Origin != "系统");
        var system = filtered.Count - third;
        CountText = IncludeSystem ? $"第三方 {third} · 系统 {system}" : $"第三方 {third}";

        if (sig == _filterSig)
            return;
        _filterSig = sig;

        var offset = ListScroll?.VerticalOffset ?? 0;
        Rows.Clear();
        string? previous = null;
        foreach (var entry in filtered)
        {
            Rows.Add(new RowVm(entry, entry.Origin != previous));
            previous = entry.Origin;
        }
        EmptyVisibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ListScroll != null)
            Dispatcher.BeginInvoke(() => ListScroll.ScrollToVerticalOffset(offset), DispatcherPriority.Background);
    }

    private void ToggleFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ToggleFromTray);
            return;
        }

        if (_ignoreTrayToggle)
            return;

        if (IsVisible)
            HideToTray();
        else
            BringToFront();
    }

    private void HideToTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(HideToTray);
            return;
        }

        if (!IsVisible)
            return;
        _hiding = true;
        try
        {
            Hide();
        }
        finally
        {
            _hiding = false;
        }
    }

    private void ExitApp()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ExitApp);
            return;
        }

        if (_exitRequested)
            return;
        _exitRequested = true;
        _timer.Stop();
        _tray.Dispose();
        Close();
        System.Windows.Application.Current?.Shutdown();
    }

    private void SyncStartup()
    {
        var preferred = StartupPreference.Load() ?? true;
        try
        {
            StartupTask.RemoveLegacy();
            if (!StartupTask.Matches(preferred))
                StartupTask.SetEnabled(preferred);
            StartupPreference.Save(preferred);
        }
        catch (Exception ex)
        {
            StatusText = Stamp("开机启动没有写上：" + ex.Message);
            preferred = false;
        }

        _suppressStartup = true;
        StartupEnabled = preferred;
        _suppressStartup = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmGetMinMaxInfo = 0x0024;
        const int wmSysCommand = 0x0112;
        if (msg == wmGetMinMaxInfo)
        {
            FitMaximizedToWorkArea(hwnd, lParam);
            handled = true;
        }
        else if (msg == wmSysCommand)
        {
            var command = wParam.ToInt32() & 0xFFF0;
            if (command is 0xF000 or 0xF010 or 0xF020 or 0xF120)
                handled = true;
        }

        return IntPtr.Zero;
    }

    private static void FitMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, 2);
        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var work = monitorInfo.rcWork;
        var screen = monitorInfo.rcMonitor;
        info.ptMaxPosition.x = work.Left - screen.Left;
        info.ptMaxPosition.y = work.Top - screen.Top;
        info.ptMaxSize.x = work.Right - work.Left;
        info.ptMaxSize.y = work.Bottom - work.Top;
        Marshal.StructureToPtr(info, lParam, true);
    }

    private void RelaunchElevated()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path) || !path.EndsWith("port.exe", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = Stamp("请从 port.exe 打开后再提权");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(path) ?? ""
            });
            ExitApp();
        }
        catch (Win32Exception)
        {
            StatusText = Stamp("没有授予管理员权限");
        }
    }

    private static string Stamp(string text) => $"{DateTime.Now:HH:mm:ss}  {text}";

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _searchText = "";
    private bool _includeSystem;
    private bool _includeUdp;
    private bool _autoRefresh = true;
    private bool _startupEnabled;
    private string _statusText = "正在读取本机端口";
    private string _countText = "";
    private Visibility _emptyVisibility = Visibility.Collapsed;

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxPoint
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public MinMaxPoint ptReserved;
        public MinMaxPoint ptMaxSize;
        public MinMaxPoint ptMaxPosition;
        public MinMaxPoint ptMinTrackSize;
        public MinMaxPoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectNative rcMonitor;
        public RectNative rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

public sealed class RowVm
{
    public RowVm(PortEntry entry, bool showGroup)
    {
        Entry = entry;
        GroupHeaderVisibility = showGroup ? Visibility.Visible : Visibility.Collapsed;
    }

    public PortEntry Entry { get; }
    public string Origin => Entry.Origin;
    public string GroupTitle => Entry.Origin == "系统" ? "系统" : "第三方软件";
    public Visibility GroupHeaderVisibility { get; }
    public Brush OriginForeground => BrushOf(Entry.Origin == "系统" ? "SystemFgBrush" : "GoldBrush");
    public Brush OriginBackground => BrushOf(Entry.Origin == "系统" ? "SystemSoftBrush" : "GoldSoftBrush");
    public Brush RowBackground => BrushOf(Entry.Origin == "系统" ? "SurfaceBrush" : "ThirdPartyRowBrush");
    public string Protocol => Entry.Protocol;
    public Brush ProtocolForeground => BrushOf(Entry.Protocol == "UDP" ? "UdpBrush" : "TcpBrush");
    public Brush ProtocolBackground => BrushOf(Entry.Protocol == "UDP" ? "UdpSoftBrush" : "TcpSoftBrush");
    public string Address => Entry.Address;
    public string AddressHint => Entry.AddressHint;
    public Brush HintForeground => BrushOf(Entry.AddressHint == "仅本机" ? "LocalBrush" : "AnyBrush");
    public Brush HintBackground => BrushOf(Entry.AddressHint == "仅本机" ? "LocalSoftBrush" : "AnySoftBrush");
    public Visibility HintVisibility => string.IsNullOrEmpty(Entry.AddressHint) ? Visibility.Collapsed : Visibility.Visible;

    private static Brush BrushOf(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
    public int Port => Entry.Port;
    public string ProcessName => Entry.ProcessName;
    public int Pid => Entry.Pid;
    public string PathText => string.IsNullOrEmpty(Entry.Path) ? "—" : Entry.Path;
    public string PathTip => string.IsNullOrEmpty(Entry.Path) ? Entry.ProcessName : Entry.Path;
}
