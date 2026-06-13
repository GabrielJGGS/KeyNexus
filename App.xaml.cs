using System;
using System.Threading.Tasks;
using System.Windows;
using KeyNexus.Core;
using Forms = System.Windows.Forms;
using System.Drawing;

namespace KeyNexus;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon notifyIcon = null!;
    private MainWindow? mainWindow;

    public DeviceMonitor Monitor { get; private set; } = null!;
    public UpdateInfo? PendingUpdate { get; set; }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Logger.Info("═══ KeyNexus iniciado ═══");

        UpdateService.CleanupOldExecutable();

        notifyIcon = new Forms.NotifyIcon();
        try
        {
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
            if (System.IO.File.Exists(iconPath))
                notifyIcon.Icon = new Icon(iconPath);
            else
                notifyIcon.Icon = SystemIcons.Application;
        }
        catch
        {
            notifyIcon.Icon = SystemIcons.Application;
        }

        notifyIcon.Visible = true;
        notifyIcon.Text = "KeyNexus — Gerenciador de Layouts";

        notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("⚙ Configurações", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("✕ Sair", null, (_, _) => Current.Shutdown());
        notifyIcon.ContextMenuStrip = menu;

        Monitor = new DeviceMonitor();
        Monitor.Start();

        _ = CheckForUpdatesSilentlyAsync();
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            var info = await UpdateService.CheckForUpdateAsync();
            if (info?.IsNewer == true)
            {
                PendingUpdate = info;
                Dispatcher.Invoke(() =>
                {
                    notifyIcon.ShowBalloonTip(
                        5000,
                        "KeyNexus — Atualização disponível",
                        $"Versão {info.Version} disponível. Abra as configurações para atualizar.",
                        Forms.ToolTipIcon.Info);
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Falha na verificação silenciosa de atualização", ex);
        }
    }

    private void ShowMainWindow()
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

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        Logger.Info("═══ KeyNexus encerrado ═══");
        notifyIcon?.Dispose();
        Monitor?.Stop();
    }
}
