using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace PortPanel;

public sealed record PortEntry(
    string Protocol,
    string Address,
    string AddressHint,
    int Port,
    int Pid,
    string ProcessName,
    string Path,
    string Kind);

internal static class PortQuery
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;
    private const int ListenState = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass",
        "winlogon", "dwm", "fontdrvhost", "Secure System", "Memory Compression", "svchost", "LsaIso"
    };

    internal static List<PortEntry> Snapshot()
    {
        _ = Marshal.SizeOf<MibTcpRowOwnerPid>();
        _ = Marshal.SizeOf<MibTcp6RowOwnerPid>();
        var list = new List<PortEntry>();
        var described = new Dictionary<int, ProcessInfo>();
        Collect(AfInet, TcpTableOwnerPidListener, tcp: true, list, described);
        Collect(AfInet6, TcpTableOwnerPidListener, tcp: true, list, described);
        Collect(AfInet, UdpTableOwnerPid, tcp: false, list, described);
        Collect(AfInet6, UdpTableOwnerPid, tcp: false, list, described);
        return list
            .DistinctBy(entry => (entry.Protocol, entry.Address, entry.Port, entry.Pid))
            .OrderBy(entry => entry.Port)
            .ThenBy(entry => entry.Protocol, StringComparer.Ordinal)
            .ThenBy(entry => entry.Address, StringComparer.Ordinal)
            .ToList();
    }

    private static void Collect(int family, int tableClass, bool tcp, List<PortEntry> into, Dictionary<int, ProcessInfo> described)
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
                    ParseTcp(mem, family, into, described);
                else
                    ParseUdp(mem, family, into, described);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }

        throw new InvalidOperationException("读取端口表失败");
    }

    private static void ParseTcp(IntPtr mem, int family, List<PortEntry> into, Dictionary<int, ProcessInfo> described)
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
                if (item.State == ListenState)
                {
                    var address = new IPAddress(item.LocalAddr).ToString();
                    Add(into, described, "TCP", address, HostPort(item.LocalPort), (int)item.OwningPid);
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
            if (item.State == ListenState)
            {
                var address = FormatV6(item.LocalAddr, item.LocalScopeId);
                Add(into, described, "TCP", address, HostPort(item.LocalPort), (int)item.OwningPid);
            }

            row6 += rowSize6;
        }
    }

    private static void ParseUdp(IntPtr mem, int family, List<PortEntry> into, Dictionary<int, ProcessInfo> described)
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
                Add(into, described, "UDP", address, HostPort(item.LocalPort), (int)item.OwningPid);
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
            Add(into, described, "UDP", address, HostPort(item.LocalPort), (int)item.OwningPid);
            row6 += rowSize6;
        }
    }

    private static void Add(List<PortEntry> into, Dictionary<int, ProcessInfo> described, string protocol, string address, int port, int pid)
    {
        if (!described.TryGetValue(pid, out var info))
        {
            info = Describe(pid);
            described[pid] = info;
        }

        var kind = Classify(info.Name, info.Path, port, pid);
        if (string.IsNullOrEmpty(kind) && info.Protected && info.Name is not "权限不足" and not "已退出")
            kind = "系统";

        into.Add(new PortEntry(
            protocol,
            address,
            Hint(address),
            port,
            pid,
            info.Name,
            info.Path,
            kind));
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
