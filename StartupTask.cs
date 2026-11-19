using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace PortPanel;

internal static class StartupTask
{
    private const string TaskName = "PortPanel";

    public static bool IsEnabled() => Run($"/Query /TN {TaskName}") == 0;

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
        var xmlPath = Path.Combine(Path.GetTempPath(), "port-panel-startup.xml");
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
                </Exec>
              </Actions>
            </Task>
            """;
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
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "port-panel",
        "startup.txt");

    public static bool? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            return File.ReadAllText(FilePath).Trim() == "1";
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
