using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private IReadOnlyList<PortEntry> _narrow = [];
    private string _narrowQuery = "";
    private string _narrowScope = "";
    private IReadOnlyList<PortEntry> _shown = [];
    private string _shownQuery = "";
    private string _shownScope = "";
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
        foreach (var query in SearchHistoryStore.Load())
            SearchHistory.Add(query);
        UpdateHistoryChrome();
        Dispatcher.BeginInvoke(BeginSession, DispatcherPriority.ApplicationIdle);
    }

    private void BeginSession()
    {
        Refresh();
        SyncStartup();
    }

    public RowList Rows { get; } = new();

    private ScrollViewer? ListScroll
    {
        get
        {
            PortList.ApplyTemplate();
            return PortList.Template?.FindName("ListScroll", PortList) as ScrollViewer;
        }
    }

    public ObservableCollection<string> SearchHistory { get; } = new();
    public Visibility HistoryListVisibility => SearchHistory.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HistoryEmptyVisibility => SearchHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
        else if (e.Key == Key.Escape && SearchHistoryPopup.IsOpen)
        {
            SearchHistoryPopup.IsOpen = false;
            CommitSearch();
            e.Handled = true;
        }
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchHistoryPopup.IsOpen)
        {
            SearchHistoryPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            if (!SearchHistoryPopup.IsOpen)
                OpenHistory();
            if (HistoryList.Items.Count > 0 && HistoryList.SelectedIndex < HistoryList.Items.Count - 1)
            {
                HistoryList.SelectedIndex++;
                HistoryList.ScrollIntoView(HistoryList.SelectedItem);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up && SearchHistoryPopup.IsOpen)
        {
            if (HistoryList.SelectedIndex > 0)
            {
                HistoryList.SelectedIndex--;
                HistoryList.ScrollIntoView(HistoryList.SelectedItem);
            }
            else
                HistoryList.SelectedIndex = -1;
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;
        if (SearchHistoryPopup.IsOpen && HistoryList.SelectedItem is string picked)
            ApplyHistory(picked);
        else
        {
            CommitSearch();
            SearchHistoryPopup.IsOpen = false;
        }

        e.Handled = true;
    }

    private void OnSearchLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && (IsInside(next, HistoryPanel) || IsInside(next, HistoryButton)))
        {
            var query = SearchBox.Text.Trim();
            if (!string.Equals(_searchText, query, StringComparison.Ordinal))
                SearchText = query;
            return;
        }

        CommitSearch();
        SearchHistoryPopup.IsOpen = false;
    }

    private void OnPreviewMouse(object sender, MouseButtonEventArgs e)
    {
        if (!SearchHistoryPopup.IsOpen || e.OriginalSource is not DependencyObject source)
            return;
        if (IsInside(source, HistoryButton) || IsInside(source, HistoryPanel))
            return;
        SearchHistoryPopup.IsOpen = false;
        CommitSearch();
    }

    private void OnHistoryToggle(object sender, RoutedEventArgs e)
    {
        if (SearchHistoryPopup.IsOpen)
        {
            SearchHistoryPopup.IsOpen = false;
            CommitSearch();
        }
        else
            OpenHistory();
    }

    private void OnHistoryPick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string query })
            ApplyHistory(query);
    }

    private void OnHistoryRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string query })
            return;
        RemoveHistory(query);
        e.Handled = true;
    }

    private void OnHistoryClear(object sender, RoutedEventArgs e)
    {
        SearchHistory.Clear();
        SearchHistoryStore.Save(SearchHistory);
        UpdateHistoryChrome();
        HistoryList.SelectedIndex = -1;
    }

    private void OpenHistory()
    {
        if (SearchBox.ActualWidth > 0)
            HistoryPanel.Width = SearchBox.ActualWidth;
        HistoryList.SelectedIndex = -1;
        SearchHistoryPopup.IsOpen = true;
    }

    private void ApplyHistory(string query)
    {
        SearchText = query;
        RememberSearch(query);
        SearchHistoryPopup.IsOpen = false;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void CommitSearch()
    {
        var query = SearchBox.Text.Trim();
        if (!string.Equals(_searchText, query, StringComparison.Ordinal))
            SearchText = query;
        if (query.Length > 0)
            RememberSearch(query);
    }

    private void RememberSearch(string query)
    {
        query = query.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (query.Length == 0)
            return;
        if (query.Length > 200)
            query = query[..200];
        if (SearchHistory.Count > 0 && string.Equals(SearchHistory[0], query, StringComparison.Ordinal))
            return;

        for (var i = SearchHistory.Count - 1; i >= 0; i--)
        {
            if (string.Equals(SearchHistory[i], query, StringComparison.OrdinalIgnoreCase))
                SearchHistory.RemoveAt(i);
        }

        SearchHistory.Insert(0, query);
        while (SearchHistory.Count > SearchHistoryStore.Limit)
            SearchHistory.RemoveAt(SearchHistory.Count - 1);
        SearchHistoryStore.Save(SearchHistory);
        UpdateHistoryChrome();
    }

    private void RemoveHistory(string query)
    {
        for (var i = SearchHistory.Count - 1; i >= 0; i--)
        {
            if (string.Equals(SearchHistory[i], query, StringComparison.OrdinalIgnoreCase))
                SearchHistory.RemoveAt(i);
        }

        SearchHistoryStore.Save(SearchHistory);
        UpdateHistoryChrome();
        if (HistoryList.SelectedIndex >= HistoryList.Items.Count)
            HistoryList.SelectedIndex = HistoryList.Items.Count - 1;
    }

    private void UpdateHistoryChrome()
    {
        OnPropertyChanged(nameof(HistoryListVisibility));
        OnPropertyChanged(nameof(HistoryEmptyVisibility));
    }

    private static bool IsInside(DependencyObject? node, DependencyObject? root)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, root))
                return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return false;
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
            var previous = _all;
            var hadView = _shownScope.Length > 0;
            var list = await Task.Run(() =>
            {
                var snapshot = PortQuery.Snapshot();
                return hadView && SameSnapshot(snapshot, previous) ? null : snapshot;
            });
            if (_exitRequested || list == null)
                return;

            _all = list;
            _narrowQuery = "";
            var tcpCount = 0;
            foreach (var entry in list)
            {
                if (entry.Protocol == "TCP" && entry.RemotePort == 0)
                    tcpCount++;
            }

            _tray.SetTip($"port · {tcpCount} 个 TCP");
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
        var scope = searching ? "*" : _nav;
        IReadOnlyList<PortEntry> source = _all;
        if (searching
            && _narrowScope == "*"
            && _narrowQuery.Length > 0
            && query.StartsWith(_narrowQuery, StringComparison.OrdinalIgnoreCase))
            source = _narrow;

        var filtered = new List<PortEntry>();
        if (searching)
        {
            foreach (var entry in source)
            {
                if (entry.Search.Contains(query, StringComparison.OrdinalIgnoreCase))
                    filtered.Add(entry);
            }
        }
        else
        {
            foreach (var entry in source)
            {
                if (InNav(entry))
                    filtered.Add(entry);
            }
        }

        _narrow = filtered;
        _narrowQuery = searching ? query : "";
        _narrowScope = scope;
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

        if (scope == _shownScope
            && string.Equals(query, _shownQuery, StringComparison.OrdinalIgnoreCase)
            && SameSnapshot(filtered, _shown))
            return;

        _shown = filtered;
        _shownQuery = query;
        _shownScope = scope;

        var offset = ListScroll?.VerticalOffset ?? 0;
        var rows = new List<RowVm>(filtered.Count);
        string? previous = null;
        foreach (var entry in filtered)
        {
            rows.Add(new RowVm(entry, entry.Origin != previous));
            previous = entry.Origin;
        }

        Rows.Reset(rows);
        EmptyVisibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (offset > 0)
            Dispatcher.BeginInvoke(() => ListScroll?.ScrollToVerticalOffset(offset), DispatcherPriority.Background);
    }

    private bool InNav(PortEntry entry) => _nav switch
    {
        "system" => entry.RemotePort == 0 && entry.Origin == "系统" && entry.Protocol != "UDP",
        "udp" => entry.RemotePort == 0 && entry.Protocol == "UDP",
        "exposed" => entry.RemotePort == 0 && entry.AddressHint == "全部网卡",
        "link" => entry.RemotePort > 0,
        _ => entry.RemotePort == 0 && entry.Origin != "系统" && entry.Protocol != "UDP"
    };

    private static bool SameSnapshot(IReadOnlyList<PortEntry> next, IReadOnlyList<PortEntry> prev)
    {
        if (next.Count != prev.Count)
            return false;
        for (var i = 0; i < next.Count; i++)
        {
            var a = next[i];
            var b = prev[i];
            if (a.Port != b.Port
                || a.Pid != b.Pid
                || a.RemotePort != b.RemotePort
                || !string.Equals(a.Protocol, b.Protocol, StringComparison.Ordinal)
                || !string.Equals(a.Address, b.Address, StringComparison.Ordinal)
                || !string.Equals(a.RemoteAddress, b.RemoteAddress, StringComparison.Ordinal)
                || !string.Equals(a.ProcessName, b.ProcessName, StringComparison.Ordinal)
                || !string.Equals(a.Service, b.Service, StringComparison.Ordinal)
                || !string.Equals(a.Path, b.Path, StringComparison.Ordinal)
                || !string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
                || !string.Equals(a.Origin, b.Origin, StringComparison.Ordinal))
                return false;
        }

        return true;
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
        SearchHistoryPopup.IsOpen = false;
        CommitSearch();
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

public sealed class RowList : ObservableCollection<RowVm>
{
    public void Reset(IReadOnlyList<RowVm> rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows)
            Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class RowVm
{
    private static readonly Dictionary<string, Brush> BrushCache = new(StringComparer.Ordinal);
    private string? _processDisplay;
    private string? _pathDisplay;

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

    private static Brush BrushOf(string key)
    {
        if (BrushCache.TryGetValue(key, out var brush))
            return brush;
        brush = (Brush)System.Windows.Application.Current.FindResource(key);
        BrushCache[key] = brush;
        return brush;
    }

    public int Port => Entry.Port;
    public string ProcessName => Entry.ProcessName;
    public string ProcessDisplay => _processDisplay ??= WithWrapPoints(Entry.ProcessName);
    public string Service => Entry.Service;
    public Visibility ServiceVisibility => string.IsNullOrEmpty(Entry.Service) ? Visibility.Collapsed : Visibility.Visible;
    public int Pid => Entry.Pid;
    public string PathText => string.IsNullOrEmpty(Entry.Path) ? "—" : Entry.Path;
    public string PathDisplay => _pathDisplay ??= string.IsNullOrEmpty(Entry.Path) ? "—" : WithWrapPoints(Entry.Path);
    public string PathTip => string.IsNullOrEmpty(Entry.Path) ? Entry.ProcessName : Entry.Path;

    private static string WithWrapPoints(string text) =>
        text.Replace("\\", "\\\u200B")
            .Replace("/", "/\u200B")
            .Replace("_", "_\u200B")
            .Replace("-", "-\u200B");
}
