using System;
using System.Windows;
using KeyNexus.Core;
using Forms = System.Windows.Forms;
using System.Drawing;

namespace KeyNexus;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon notifyIcon = null!;
    private MainWindow? mainWindow;

    // ══════════════════════════════════════
    // Propriedade pública (sem Reflection!)
    // ══════════════════════════════════════
    public DeviceMonitor Monitor { get; private set; } = null!;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Logger.Info("═══ KeyNexus iniciado ═══");

        // Configura a bandeja do sistema
        notifyIcon = new Forms.NotifyIcon();
        notifyIcon.Icon = SystemIcons.Application;
        notifyIcon.Visible = true;
        notifyIcon.Text = "KeyNexus — Gerenciador de Layouts";

        notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("⚙ Configurações", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("✕ Sair", null, (_, _) => Current.Shutdown());
        notifyIcon.ContextMenuStrip = menu;

        // Inicializar DeviceMonitor
        Monitor = new DeviceMonitor();
        Monitor.Start();
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
