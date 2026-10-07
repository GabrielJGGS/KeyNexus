using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using KeyNexus.Core;
using Forms = System.Windows.Forms;
using System.Drawing;

namespace KeyNexus;

public partial class App : System.Windows.Application
{
    private const string RestartArgument = "--restarted";

    private Forms.NotifyIcon? notifyIcon;
    private Forms.ToolStripMenuItem? pauseMenuItem;
    private MainWindow? mainWindow;
    private SingleInstance? singleInstance;
    private string trayText = "KeyNexus";

    public DeviceMonitor Monitor { get; private set; } = null!;

    partial void StartUpdateCheck();

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        singleInstance = new SingleInstance();
        var wait = e.Args.Contains(RestartArgument) ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
        if (!singleInstance.TryAcquire(wait))
        {
            // Já existe um KeyNexus rodando: pede para ele mostrar a janela e sai.
            singleInstance.SignalFirstInstance();
            singleInstance.Dispose();
            singleInstance = null;
            Shutdown();
            return;
        }
        singleInstance.ListenForSecondInstances(() => Dispatcher.BeginInvoke(ShowMainWindow));

        Logger.Info("═══ KeyNexus iniciado ═══");
        CreateTrayIcon();

        Monitor = new DeviceMonitor();
        Monitor.ActiveKeyboardChanged += OnActiveKeyboardChanged;
        Monitor.PausedChanged += OnPausedChanged;
        Monitor.Start();

#if DEBUG
        if (e.Args.Contains("--self-check"))
        {
            SelfCheck.Run(this);
            return;
        }
#endif

        if (Monitor.Config.IsFirstRun)
            ShowMainWindow();

        StartUpdateCheck();
    }

    private void CreateTrayIcon()
    {
        notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = trayText,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir KeyNexus", null, (_, _) => ShowMainWindow());

        pauseMenuItem = new Forms.ToolStripMenuItem("Pausar KeyNexus") { CheckOnClick = true };
        pauseMenuItem.Click += (_, _) => Monitor.SetPaused(pauseMenuItem.Checked);
        menu.Items.Add(pauseMenuItem);

        menu.Items.Add("Soltar teclas presas", null, (_, _) => Monitor.ReleaseStuckKeys());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Current.Shutdown());
        notifyIcon.ContextMenuStrip = menu;
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
            if (System.IO.File.Exists(iconPath))
                return new Icon(iconPath);
        }
        catch
        {
            // Sem ícone próprio: usa o padrão.
        }
        return SystemIcons.Application;
    }

    private void OnActiveKeyboardChanged(string groupKey)
    {
        var config = Monitor.Config;
        string? path = Monitor.ActiveDevicePath;
        string? alias = config.GetDeviceAlias(groupKey);
        string? hkl = config.GetLayoutForDevice(groupKey);

        Task.Run(() =>
            {
                string name = alias ?? (path != null ? DeviceNameResolver.GetFriendlyName(path) : "Teclado");
                string layout = string.IsNullOrEmpty(hkl)
                    ? "layout do Windows"
                    : config.GetLayoutAlias(hkl) ?? KeyboardLayoutCatalog.Describe(hkl).LayoutName;
                return $"{name}: {layout}";
            })
            .ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    trayText = task.Result;
                    UpdateTrayText();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnPausedChanged(bool paused)
    {
        if (pauseMenuItem != null)
            pauseMenuItem.Checked = paused;
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        if (notifyIcon == null)
            return;

        string text = Monitor.IsPaused ? $"KeyNexus (pausado) — {trayText}" : $"KeyNexus — {trayText}";
        notifyIcon.Text = text.Length > 120 ? text[..117] + "..." : text;
    }

    internal void ShowMainWindow()
    {
        if (mainWindow == null)
        {
            mainWindow = new MainWindow();
            mainWindow.Closed += (_, _) => mainWindow = null;
            mainWindow.Show();
        }
        else
        {
            if (mainWindow.WindowState == WindowState.Minimized)
                mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
    }

    /// <summary>Libera a instância única antes de abrir a nova versão.</summary>
    internal void PrepareRestart() => singleInstance?.Release();

    internal static string RestartArgumentValue => RestartArgument;

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (Monitor != null)
        {
            Logger.Info("═══ KeyNexus encerrado ═══");
            Monitor.Stop();
        }

        notifyIcon?.Dispose();
        singleInstance?.Dispose();
        Logger.Flush(TimeSpan.FromSeconds(1));
    }
}
