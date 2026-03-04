using System.Configuration;
using System.Data;
using System.Windows;
using Forms = System.Windows.Forms;
using System.Drawing;

namespace KeyNexus;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon notifyIcon = null!;
    private MainWindow? mainWindow;
    private Core.DeviceMonitor deviceMonitor = null!;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Impede que a MainWindow abra automaticamente (roda em background)
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Configura o ícone da bandeja
        notifyIcon = new Forms.NotifyIcon();
        // Fallback icon - em produção seria ideal carregar um ícone real com notifyIcon.Icon = new Icon("icon.ico");
        notifyIcon.Icon = SystemIcons.Information; 
        notifyIcon.Visible = true;
        notifyIcon.Text = "KeyNexus - Gerenciador de Layouts";
        
        notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

        // Context Menu
        notifyIcon.ContextMenuStrip = new Forms.ContextMenuStrip();
        notifyIcon.ContextMenuStrip.Items.Add("Configurações", null, OnSettingsClicked);
        notifyIcon.ContextMenuStrip.Items.Add("Sair", null, OnExitClicked);

        // Inicializar monitor de hardware
        deviceMonitor = new Core.DeviceMonitor();
        deviceMonitor.Start();
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnSettingsClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (mainWindow == null)
        {
            mainWindow = new MainWindow();
            mainWindow.Closed += (s, args) => mainWindow = null;
            mainWindow.Show();
        }
        else
        {
            if (mainWindow.WindowState == WindowState.Minimized)
                mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        Current.Shutdown();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        notifyIcon?.Dispose();
        deviceMonitor?.Stop();
    }
}
