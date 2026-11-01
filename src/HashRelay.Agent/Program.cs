using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Toolkit.Uwp.Notifications;

namespace HashRelay.Agent;

internal static class Program
{
    public const string Aumid = "Avlon.HashRelay";
    private const string MutexName = @"Local\HashRelay.Agent.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // A stable AppUserModelID so Windows shows "HashRelay" with our icon in Notification Center and Settings.
        SetCurrentProcessExplicitAppUserModelID(Aumid);

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            try { ToastNotificationManagerCompat.Uninstall(); } catch { }
            try { Startup.Remove(); } catch { }
            return 0;
        }
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var p in Process.GetProcessesByName("HashRelay.Agent").Where(p => p.Id != Environment.ProcessId))
                try { p.Kill(); } catch { }
            return 0;
        }

        // Toast activation (click) relaunches us via COM; the toolkit routes it to OnActivated in the running instance.
        // When the agent was not running, Windows starts this process just for the click: wait for the click to be
        // handled (a one-click decision included, up to its own HTTP timeout) instead of exiting after a fixed delay.
        if (ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        {
            var started = new ManualResetEventSlim();
            Task? work = null;
            ToastNotificationManagerCompat.OnActivated += e => { work = Task.Run(() => ToastActivation.HandleAsync(e.Argument)); started.Set(); };
            if (started.Wait(TimeSpan.FromSeconds(10))) work?.Wait(TimeSpan.FromSeconds(60));
            return 0;
        }

        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Already running: bring attention via a toast and exit.
            try { Toasts.ShowInfo("HashRelay is already running", "Look for the # icon in the system tray."); } catch { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        Log.Info($"HashRelay agent {AgentInfo.Version} starting");
        using var app = new AgentContext(args.Contains("--silent", StringComparer.OrdinalIgnoreCase));
        Application.Run(app);
        return 0;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);
}

internal static class AgentInfo
{
    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string DefaultServer { get; } =
        typeof(AgentInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DefaultServer")?.Value
        ?? Client.RelayConfig.DefaultServer;
}
