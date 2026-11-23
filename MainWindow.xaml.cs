using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Port;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly TrayService _tray;
    private IReadOnlyList<PortEntry> _all = [];
    private string _snapSig = "";
    private string _filterSig = "";
    private bool _refreshing;
    private bool _exitRequested;
    private bool _suppressStartup;
    private readonly DispatcherTimer _refreshTimer;
    public MainWindow()
    {
        DataContext = this;
        InitializeComponent();
        Icon = PortIcon.CreateImage();
        _tray = new TrayService(ToggleFromTray);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
        Dispatcher.BeginInvoke(BeginSession, DispatcherPriority.ApplicationIdle);
    }

    private void BeginSession()
    {
        Refresh();
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

    public string AddressHeader => _nav == "link" ? "对方" : "地址";
    public string PortHeader => _nav == "link" ? "本机" : "端口";
    public string EmptyText => _nav == "link" ? "没有已建立的连接" : "没有匹配的监听端口";

    public string Nav
    {
        get => _nav;
        set
        {
            if (Set(ref _nav, value))
            {
                OnPropertyChanged(nameof(AddressHeader));
                OnPropertyChanged(nameof(PortHeader));
                OnPropertyChanged(nameof(EmptyText));
                ApplyFilter();
            }
        }
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (!Set(ref _autoRefresh, value))
                return;
            if (value)
                _refreshTimer.Start();
            else
                _refreshTimer.Stop();
        }
    }

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
                catch (Exception)
                {
                    return;
                }
            }

            Set(ref _startupEnabled, value);
        }
    }

    public string CountText { get => _countText; private set => Set(ref _countText, value); }
    public Visibility EmptyVisibility { get => _emptyVisibility; private set => Set(ref _emptyVisibility, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, Marshal.SizeOf<int>());
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

    public void BringToFront()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(BringToFront);
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
            SystemCommands.RestoreWindow(this);
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
        else if (e.Key == Key.Escape && ExitConfirm.Visibility == Visibility.Visible)
        {
            HideExitConfirm();
            e.Handled = true;
        }
    }

    private void OnRowMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;
        var row = RowOf(menu);
        var path = row?.Entry.Path ?? "";
        var directory = string.IsNullOrEmpty(path) ? "" : Path.GetDirectoryName(path) ?? "";
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Header is "复制路径")
                item.IsEnabled = path.Length > 0;
            else if (item.Header is "打开所在文件夹")
                item.IsEnabled = directory.Length > 0 && Directory.Exists(directory);
        }
    }

    private void OnCopyPort(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            CopyText(row.Port.ToString());
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { Entry.Path: { Length: > 0 } path })
            CopyText(path);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
            return;
        var directory = Path.GetDirectoryName(row.Entry.Path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directory}\"",
            UseShellExecute = true
        });
    }

    private static RowVm? RowOf(object sender)
    {
        var menu = sender switch
        {
            ContextMenu context => context,
            MenuItem { Parent: ContextMenu parent } => parent,
            _ => null
        };
        return menu?.PlacementTarget is FrameworkElement { DataContext: RowVm row } ? row : null;
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
        }
    }

    private void OnExitAsk(object sender, RoutedEventArgs e)
    {
        ExitAsk.Visibility = Visibility.Collapsed;
        ExitConfirm.Visibility = Visibility.Visible;
    }

    private void OnExitCancel(object sender, RoutedEventArgs e) => HideExitConfirm();

    private void OnExitConfirm(object sender, RoutedEventArgs e) => ExitApp();

    private void HideExitConfirm()
    {
        ExitConfirm.Visibility = Visibility.Collapsed;
        ExitAsk.Visibility = Visibility.Visible;
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string nav })
            Nav = nav;
    }

    private void OnListCardSize(object sender, SizeChangedEventArgs e)
    {
        ListCard.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || e.OriginalSource is not DependencyObject source)
            return;

        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: RowVm row })
            {
                var service = string.IsNullOrEmpty(row.Service) ? "" : $"  {row.Service}";
                var where = row.Entry.RemotePort > 0
                    ? $"{row.AddressText}:{row.Entry.RemotePort}  本机 {row.Entry.Address}:{row.Port}"
                    : $"{row.Address}:{row.Port}";
                var text = $"{where}  {row.ProcessName}{service}  {row.Origin}  PID {row.Pid}  {row.PathTip}";
                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
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
                $"{entry.Protocol}|{entry.Address}|{entry.Port}|{entry.Pid}|{entry.ProcessName}|{entry.Service}|{entry.Path}|{entry.Kind}|{entry.Origin}|{entry.RemoteAddress}|{entry.RemotePort}"));
            if (sig != _snapSig)
            {
                _snapSig = sig;
                _all = list;
                var tcpCount = list.Count(entry => entry.Protocol == "TCP" && entry.RemotePort == 0);
                _tray.SetTip($"port · {tcpCount} 个 TCP");
            }

            ApplyFilter();
        }
        catch (Exception)
        {
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyFilter()
    {
        var query = _searchText.Trim();
        var searching = query.Length > 0;
        var filtered = _all.Where(entry =>
        {
            if (!searching)
            {
                return _nav switch
                {
                    "system" => entry.RemotePort == 0 && entry.Origin == "系统" && entry.Protocol != "UDP",
                    "udp" => entry.RemotePort == 0 && entry.Protocol == "UDP",
                    "exposed" => entry.RemotePort == 0 && entry.AddressHint == "全部网卡",
                    "link" => entry.RemotePort > 0,
                    _ => entry.RemotePort == 0 && entry.Origin != "系统" && entry.Protocol != "UDP"
                };
            }

            var blob = $"{entry.Protocol} {entry.Address} {entry.AddressHint} {entry.Port} {entry.RemoteAddress} {entry.RemotePort} {entry.ProcessName} {entry.Service} {entry.Path} {entry.Pid} {entry.Kind} {entry.Origin}";
            return blob.Contains(query, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        var sig = string.Join('\n', filtered.Select(entry => $"{entry.Protocol}|{entry.Address}|{entry.Port}|{entry.Pid}|{entry.Service}|{entry.Path}|{entry.Kind}|{entry.Origin}|{entry.RemoteAddress}|{entry.RemotePort}"))
                  + $"|{query}|{(searching ? "*" : _nav)}";
        CountText = searching
            ? $"搜索 {filtered.Count}"
            : _nav switch
            {
                "system" => $"系统 {filtered.Count}",
                "udp" => $"UDP {filtered.Count}",
                "exposed" => $"对外 {filtered.Count}",
                "link" => $"连接 {filtered.Count}",
                _ => $"第三方 {filtered.Count}"
            };

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

        if (!IsVisible || WindowState == WindowState.Minimized)
            BringToFront();
        else
            HideToTray();
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
        HideExitConfirm();
        Hide();
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
        _refreshTimer.Stop();
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
        catch (Exception)
        {
            preferred = false;
        }

        _suppressStartup = true;
        StartupEnabled = preferred;
        _suppressStartup = false;
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
    private string _nav = "system";
    private bool _autoRefresh = true;
    private bool _startupEnabled;
    private string _countText = "";
    private Visibility _emptyVisibility = Visibility.Collapsed;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
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
    public bool IsLink => Entry.RemotePort > 0;
    public string AddressText => IsLink ? Entry.RemoteAddress : Entry.Address;
    public string AddressHintText => IsLink ? Entry.RemotePort.ToString() : Entry.AddressHint;
    public Brush HintForeground => BrushOf(!IsLink && Entry.AddressHint == "仅本机" ? "LocalBrush" : "AnyBrush");
    public Brush HintBackground => BrushOf(!IsLink && Entry.AddressHint == "仅本机" ? "LocalSoftBrush" : "AnySoftBrush");
    public Visibility HintVisibility => string.IsNullOrEmpty(AddressHintText) ? Visibility.Collapsed : Visibility.Visible;

    private static Brush BrushOf(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
    public int Port => Entry.Port;
    public string ProcessName => Entry.ProcessName;
    public string ProcessDisplay => WithWrapPoints(Entry.ProcessName);
    public string Service => Entry.Service;
    public Visibility ServiceVisibility => string.IsNullOrEmpty(Entry.Service) ? Visibility.Collapsed : Visibility.Visible;
    public int Pid => Entry.Pid;
    public string PathText => string.IsNullOrEmpty(Entry.Path) ? "—" : Entry.Path;
    public string PathDisplay => string.IsNullOrEmpty(Entry.Path) ? "—" : WithWrapPoints(Entry.Path);
    public string PathTip => string.IsNullOrEmpty(Entry.Path) ? Entry.ProcessName : Entry.Path;

    private static string WithWrapPoints(string text) =>
        text.Replace("\\", "\\\u200B")
            .Replace("/", "/\u200B")
            .Replace("_", "_\u200B")
            .Replace("-", "-\u200B");
}
