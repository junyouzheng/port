using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Port;

public sealed record PortEntry(
    string Protocol,
    string Address,
    string AddressHint,
    int Port,
    int Pid,
    string ProcessName,
    string Path,
    string Kind,
    string Origin,
    string Service,
    string RemoteAddress,
    int RemotePort,
    string Search);

internal static class PortQuery
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int TcpTableOwnerPidConnections = 4;
    private const int UdpTableOwnerPid = 1;
    private const int ListenState = 2;
    private const int EstablishedState = 5;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceStateAll = 0x3;

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass",
        "winlogon", "dwm", "fontdrvhost", "Secure System", "Memory Compression", "svchost", "LsaIso"
    };

    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass", "LsaIso",
        "winlogon", "dwm", "fontdrvhost", "Secure System", "Memory Compression", "svchost",
        "spoolsv", "dllhost", "RuntimeBroker", "sihost", "ctfmon", "explorer", "taskhostw", "conhost",
        "WmiPrvSE", "unsecapp", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost", "SearchHost",
        "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ApplicationFrameHost",
        "SystemSettings", "SystemSettingsBroker", "LockApp", "UserOOBEBroker", "backgroundTaskHost",
        "CompPkgSrv", "audiodg", "dasHost", "MoUsoCoreWorker", "MusNotification", "MusNotificationUx",
        "TiWorker", "TrustedInstaller", "SecurityHealthService", "SecurityHealthSystray", "MsMpEng",
        "NisSrv", "smartscreen", "SgrmBroker", "WidgetService", "Widgets", "AggregatorHost",
        "DataExchangeHost", "CredentialUIBroker", "msdtc", "WUDFHost", "wlanext", "ShellHost",
        "AppHostRegistrationVerifier", "GameBar", "GameBarFTServer", "GameBarPresenceWriter"
    };

    private static readonly Dictionary<string, bool> MicrosoftSigned = new(StringComparer.OrdinalIgnoreCase);

    internal static List<PortEntry> Snapshot()
    {
        _ = Marshal.SizeOf<MibTcpRowOwnerPid>();
        _ = Marshal.SizeOf<MibTcp6RowOwnerPid>();
        var list = new List<PortEntry>();
        var described = new Dictionary<int, ProcessInfo>();
        var services = ServiceLabels();
        Collect(AfInet, TcpTableOwnerPidListener, tcp: true, established: false, list, described, services);
        Collect(AfInet6, TcpTableOwnerPidListener, tcp: true, established: false, list, described, services);
        Collect(AfInet, TcpTableOwnerPidConnections, tcp: true, established: true, list, described, services);
        Collect(AfInet6, TcpTableOwnerPidConnections, tcp: true, established: true, list, described, services);
        Collect(AfInet, UdpTableOwnerPid, tcp: false, established: false, list, described, services);
        Collect(AfInet6, UdpTableOwnerPid, tcp: false, established: false, list, described, services);
        return list
            .DistinctBy(entry => (entry.Protocol, entry.Address, entry.Port, entry.Pid, entry.RemoteAddress, entry.RemotePort))
            .OrderBy(entry => entry.Origin == "系统")
            .ThenBy(entry => entry.Port)
            .ThenBy(entry => entry.Protocol, StringComparer.Ordinal)
            .ThenBy(entry => entry.Address, StringComparer.Ordinal)
            .ThenBy(entry => entry.RemoteAddress, StringComparer.Ordinal)
            .ThenBy(entry => entry.RemotePort)
            .ToList();
    }

    private static void Collect(int family, int tableClass, bool tcp, bool established, List<PortEntry> into, Dictionary<int, ProcessInfo> described, Dictionary<int, string> services)
    {
        int size = 0;
        var rc = tcp
            ? GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, tableClass, 0)
            : GetExtendedUdpTable(IntPtr.Zero, ref size, true, family, tableClass, 0);
        if (rc != 0 && rc != 122)
            throw new Win32Exception((int)rc);
        if (size <= 0)
            return;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var mem = Marshal.AllocHGlobal(size);
            try
            {
                rc = tcp
                    ? GetExtendedTcpTable(mem, ref size, true, family, tableClass, 0)
                    : GetExtendedUdpTable(mem, ref size, true, family, tableClass, 0);
                if (rc == 122)
                    continue;
                if (rc != 0)
                    throw new Win32Exception((int)rc);
                if (tcp)
                    ParseTcp(mem, family, established, into, described, services);
                else
                    ParseUdp(mem, family, into, described, services);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }

        throw new InvalidOperationException("读取端口表失败");
    }

    private static void ParseTcp(IntPtr mem, int family, bool established, List<PortEntry> into, Dictionary<int, ProcessInfo> described, Dictionary<int, string> services)
    {
        var count = Marshal.ReadInt32(mem);
        if (count <= 0)
            return;

        if (family == AfInet)
        {
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var row = mem + 4;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<MibTcpRowOwnerPid>(row);
                if (WantTcp(item.State, established))
                {
                    var address = new IPAddress(item.LocalAddr).ToString();
                    var remote = established ? new IPAddress(item.RemoteAddr).ToString() : "";
                    var remotePort = established ? HostPort(item.RemotePort) : 0;
                    Add(into, described, services, "TCP", address, HostPort(item.LocalPort), (int)item.OwningPid, remote, remotePort);
                }

                row += rowSize;
            }

            return;
        }

        var rowSize6 = Marshal.SizeOf<MibTcp6RowOwnerPid>();
        var row6 = mem + 4;
        for (var i = 0; i < count; i++)
        {
            var item = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(row6);
            if (WantTcp(item.State, established))
            {
                var address = FormatV6(item.LocalAddr, item.LocalScopeId);
                var remote = established ? FormatV6(item.RemoteAddr, item.RemoteScopeId) : "";
                var remotePort = established ? HostPort(item.RemotePort) : 0;
                Add(into, described, services, "TCP", address, HostPort(item.LocalPort), (int)item.OwningPid, remote, remotePort);
            }

            row6 += rowSize6;
        }
    }

    private static void ParseUdp(IntPtr mem, int family, List<PortEntry> into, Dictionary<int, ProcessInfo> described, Dictionary<int, string> services)
    {
        var count = Marshal.ReadInt32(mem);
        if (count <= 0)
            return;

        if (family == AfInet)
        {
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            var row = mem + 4;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<MibUdpRowOwnerPid>(row);
                var address = new IPAddress(item.LocalAddr).ToString();
                Add(into, described, services, "UDP", address, HostPort(item.LocalPort), (int)item.OwningPid);
                row += rowSize;
            }

            return;
        }

        var rowSize6 = Marshal.SizeOf<MibUdp6RowOwnerPid>();
        var row6 = mem + 4;
        for (var i = 0; i < count; i++)
        {
            var item = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(row6);
            var address = FormatV6(item.LocalAddr, item.LocalScopeId);
            Add(into, described, services, "UDP", address, HostPort(item.LocalPort), (int)item.OwningPid);
            row6 += rowSize6;
        }
    }

    private static bool WantTcp(uint state, bool established) =>
        established ? state == EstablishedState : state == ListenState;

    private static void Add(List<PortEntry> into, Dictionary<int, ProcessInfo> described, Dictionary<int, string> services, string protocol, string address, int port, int pid, string remoteAddress = "", int remotePort = 0)
    {
        if (!described.TryGetValue(pid, out var info))
        {
            info = Describe(pid);
            described[pid] = info;
        }

        var kind = Classify(info.Name, info.Path, port, pid);
        if (string.IsNullOrEmpty(kind) && info.Protected && info.Name is not "权限不足" and not "已退出")
            kind = "系统";

        var shownRemote = remotePort > 0 ? remoteAddress : "";
        var shownRemotePort = remotePort > 0 ? remotePort : 0;
        var origin = IsSystem(info.Name, info.Path, pid) ? "系统" : "第三方";
        var service = ServiceLabel(info.Name, pid, services);
        var hint = Hint(address);
        var search = $"{protocol} {address} {hint} {port} {shownRemote} {shownRemotePort} {info.Name} {service} {info.Path} {pid} {kind} {origin}";
        into.Add(new PortEntry(
            protocol,
            address,
            hint,
            port,
            pid,
            info.Name,
            info.Path,
            kind,
            origin,
            service,
            shownRemote,
            shownRemotePort,
            search));
    }

    private static string ServiceLabel(string processName, int pid, Dictionary<int, string> services)
    {
        if (!processName.Equals("svchost", StringComparison.OrdinalIgnoreCase))
            return "";
        return services.TryGetValue(pid, out var label) ? label : "";
    }

    private static Dictionary<int, string> ServiceLabels()
    {
        try
        {
            return QueryServiceLabels();
        }
        catch (Exception)
        {
            return new Dictionary<int, string>();
        }
    }

    private static Dictionary<int, string> QueryServiceLabels()
    {
        var grouped = new Dictionary<int, List<string>>();
        var manager = OpenSCManager(null, null, ScManagerEnumerateService);
        if (manager == IntPtr.Zero)
            return new Dictionary<int, string>();

        try
        {
            uint needed = 0, returned = 0, resume = 0;
            EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, IntPtr.Zero, 0, out needed, out returned, ref resume, null);
            if (needed == 0)
                return new Dictionary<int, string>();

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                resume = 0;
                if (!EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, buffer, needed, out needed, out returned, ref resume, null))
                    return new Dictionary<int, string>();

                var rowSize = Marshal.SizeOf<EnumServiceStatusProcess>();
                for (var i = 0; i < returned; i++)
                {
                    var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + i * rowSize);
                    var pid = (int)item.Status.ProcessId;
                    if (pid <= 0)
                        continue;
                    var label = string.IsNullOrWhiteSpace(item.DisplayName) ? item.ServiceName : item.DisplayName;
                    if (string.IsNullOrWhiteSpace(label))
                        continue;
                    if (!grouped.TryGetValue(pid, out var names))
                    {
                        names = new List<string>();
                        grouped[pid] = names;
                    }

                    if (!names.Contains(label, StringComparer.OrdinalIgnoreCase))
                        names.Add(label);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }

        return grouped.ToDictionary(pair => pair.Key, pair => string.Join("、", pair.Value), EqualityComparer<int>.Default);
    }

    private readonly record struct ProcessInfo(string Name, string Path, bool Protected);

    private static ProcessInfo Describe(int pid)
    {
        if (pid <= 4)
            return new ProcessInfo(pid == 4 ? "System" : "Idle", "", true);

        var path = QueryPath(pid) ?? "";
        string name;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return new ProcessInfo("已退出", path, true);
        }
        catch
        {
            name = "";
        }

        if (string.IsNullOrEmpty(name) && path.Length > 0)
            name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name))
            return new ProcessInfo("权限不足", "", true);

        return new ProcessInfo(name, path, ProtectedNames.Contains(name));
    }

    private static bool IsSystem(string name, string path, int pid)
    {
        if (pid <= 4 || SystemNames.Contains(name))
            return true;
        if (string.IsNullOrEmpty(path))
            return name is "权限不足" or "已退出";

        var file = Path.GetFileNameWithoutExtension(path);
        if (SystemNames.Contains(file))
            return true;
        return IsWindowsComponent(path) && IsMicrosoftSigned(path);
    }

    private static bool IsWindowsComponent(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length == 0)
            return false;
        var full = Path.GetFullPath(path);
        var root = windows.TrimEnd('\\') + "\\";
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = full[root.Length..];
        return rest.StartsWith("System32\\", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("SysWOW64\\", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("SystemApps\\", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("WinSxS\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMicrosoftSigned(string path)
    {
        if (MicrosoftSigned.TryGetValue(path, out var cached))
            return cached;
        var signed = false;
        try
        {
            var subject = X509Certificate.CreateFromSignedFile(path).Subject;
            signed = subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                || subject.Contains("Microsoft Windows", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            signed = false;
        }

        MicrosoftSigned[path] = signed;
        return signed;
    }

    private static string? QueryPath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string Classify(string name, string path, int port, int pid)
    {
        var file = Path.GetFileName(path).ToLowerInvariant();
        var process = name.ToLowerInvariant();
        if (file is "nginx.exe" || process is "nginx")
            return "Nginx";
        if (file is "redis-server.exe" or "redis.exe" || process is "redis-server" or "redis")
            return "Redis";
        if (file is "node.exe" || process is "node")
            return "Node";
        if (file is "vela.exe" || process is "vela" || process.StartsWith("vela", StringComparison.Ordinal))
            return "vela";
        if (pid == 4 && port is 80 or 443)
            return "HTTP.sys";
        if (port == 6379)
            return "Redis";
        if (port is 80 or 443)
            return "Web";
        return "";
    }

    private static string Hint(string address) => address switch
    {
        "0.0.0.0" or "::" => "全部网卡",
        "127.0.0.1" or "::1" => "仅本机",
        _ => ""
    };

    private static string FormatV6(byte[] bytes, uint scope)
    {
        var ip = scope == 0 ? new IPAddress(bytes) : new IPAddress(bytes, scope);
        return ip.ToString();
    }

    // MIB stores the port in network byte order in the low 16 bits of the DWORD.
    private static int HostPort(uint raw) =>
        (int)((raw & 0xFF) << 8 | (raw >> 8) & 0xFF);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusEx(
        IntPtr manager,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        IntPtr services,
        uint bufferSize,
        out uint bytesNeeded,
        out uint servicesReturned,
        ref uint resumeHandle,
        string? group);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        public string ServiceName;
        public string DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }
}
