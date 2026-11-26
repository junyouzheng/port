using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace Port;

internal static class StartupTask
{
    internal const string SilentSwitch = "--silent";
    private const string TaskName = "port";
    private const string LegacyTaskName = "PortPanel";

    public static bool IsEnabled() => Run($"/Query /TN {TaskName}") == 0;

    public static bool Matches(bool enabled)
    {
        var xml = QueryXml();
        if (!enabled)
            return xml is null;

        var exe = Environment.ProcessPath;
        if (xml is null || string.IsNullOrEmpty(exe))
            return false;

        var escaped = SecurityElement.Escape(exe) ?? exe;
        var hasExe = xml.Contains(exe, StringComparison.OrdinalIgnoreCase)
            || xml.Contains(escaped, StringComparison.OrdinalIgnoreCase);
        return hasExe && xml.Contains(SilentSwitch, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (IsEnabled())
            {
                var code = Run($"/Delete /TN {TaskName} /F");
                if (code != 0 && IsEnabled())
                    throw new InvalidOperationException("没能取消开机启动");
            }

            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("找不到程序路径");

        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var xmlPath = Path.Combine(Path.GetTempPath(), "port-startup.xml");
        File.WriteAllText(xmlPath, BuildXml(exe, user), Encoding.Unicode);
        try
        {
            var code = Run($"/Create /TN {TaskName} /XML \"{xmlPath}\" /F");
            if (code != 0)
                throw new InvalidOperationException("没能写入开机启动");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* temp file */ }
        }
    }

    private static string BuildXml(string exe, string user)
    {
        var command = SecurityElement.Escape(exe);
        var account = SecurityElement.Escape(user);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{account}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{account}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{SilentSwitch}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string? QueryXml()
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN {TaskName} /XML")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null)
            return null;

        using var stdout = new MemoryStream();
        // Read off the UI thread. Waiting here would deadlock if the async read resumed on it.
        var readOut = Task.Run(() => process.StandardOutput.BaseStream.CopyTo(stdout));
        var readErr = Task.Run(() => process.StandardError.BaseStream.CopyTo(Stream.Null));
        if (!process.WaitForExit(8000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* leaving */ }
            try { Task.WaitAll(readOut, readErr); } catch { /* process gone */ }
            return null;
        }

        process.WaitForExit();
        try
        {
            Task.WaitAll(readOut, readErr);
        }
        catch
        {
            return null;
        }

        if (process.ExitCode != 0 || stdout.Length == 0)
            return null;

        var xml = Decode(stdout.ToArray());
        return xml.Contains("<Task", StringComparison.OrdinalIgnoreCase) ? xml : null;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes);
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    public static void RemoveLegacy()
    {
        try
        {
            if (Run($"/Query /TN {LegacyTaskName}") != 0)
                return;
            Run($"/Delete /TN {LegacyTaskName} /F");
        }
        catch
        {
            // The next elevated launch can remove the old task.
        }
    }

    private static int Run(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null)
            return -1;
        process.WaitForExit(8000);
        return process.HasExited ? process.ExitCode : -1;
    }
}

internal static class StartupPreference
{
    private static string FilePath => PreferencePath("port");
    private static string LegacyFilePath => PreferencePath("port-panel");

    private static string PreferencePath(string folder) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        folder,
        "startup.txt");

    public static bool? Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (!File.Exists(path))
                return null;
            return File.ReadAllText(path).Trim() == "1";
        }
        catch
        {
            return null;
        }
    }

    public static void Save(bool enabled)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, enabled ? "1" : "0");
    }
}

internal static class SearchHistoryStore
{
    public const int Limit = 8;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "port",
        "search.txt");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<string>();
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var query = line.Trim();
                if (query.Length == 0 || !seen.Add(query))
                    continue;
                items.Add(query);
                if (items.Count == Limit)
                    break;
            }

            return items;
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IReadOnlyList<string> items)
    {
        try
        {
            if (items.Count == 0)
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, items);
        }
        catch
        {
        }
    }
}
