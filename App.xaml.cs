using System.IO;
using System.Text;
using System.Windows;

namespace PortPanel;

public partial class App : System.Windows.Application
{
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showEvent;
    private static CancellationTokenSource? _showWatch;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dump = DumpPath(e.Args);
        if (dump is not null)
        {
            try
            {
                var text = Format(PortQuery.Snapshot());
                File.WriteAllText(dump, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(dump, ex.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
                catch
                {
                    // The dump file itself could not be written.
                }

                Shutdown(1);
            }

            return;
        }

        _instanceMutex = new Mutex(true, @"Local\PortPanel.Single", out var created);
        if (!created)
        {
            SignalExisting();
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PortPanel.Show");
        var window = new MainWindow();
        _showWatch = new CancellationTokenSource();
        var token = _showWatch.Token;
        var show = _showEvent;
        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (show.WaitOne(500) && !token.IsCancellationRequested)
                    window.Dispatcher.BeginInvoke(window.BringToFront);
            }
        }, token);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWatch?.Cancel();
        _showEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void SignalExisting()
    {
        try
        {
            using var show = EventWaitHandle.OpenExisting(@"Local\PortPanel.Show");
            show.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance has not created the event yet.
        }
    }

    private static string? DumpPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals("--dump", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                return args[i + 1];
            return Path.Combine(AppContext.BaseDirectory, "port-panel-dump.txt");
        }

        return null;
    }

    private static string Format(IReadOnlyList<PortEntry> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.Append(row.Protocol).Append('\t')
                .Append(row.Address).Append('\t')
                .Append(row.Port).Append('\t')
                .Append(row.Pid).Append('\t')
                .Append(row.ProcessName).Append('\t')
                .Append(row.Origin).Append('\t')
                .Append(row.Kind).Append('\t')
                .Append(row.Path).Append('\n');
        }

        return builder.ToString();
    }
}
