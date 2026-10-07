using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Colors = System.Windows.Media.Colors;

namespace KeyNexus.Themes;

/// <summary>
/// Barra de título escura e, no Windows 11 22H2+, fundo Mica. Em versões antigas fica a cor sólida do tema.
/// </summary>
internal static class WindowEffects
{
    private const int MicaMinimumBuild = 22621;

    public static void Apply(Window window, bool useMica)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                int enabled = 1;
                if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
                    NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enabled, sizeof(int));

                if (useMica && Environment.OSVersion.Version.Build >= MicaMinimumBuild)
                    TryEnableMica(window, hwnd);
            }
            catch (Exception ex)
            {
                Core.Logger.Error("Falha ao aplicar efeitos da janela", ex);
            }
        };
    }

    private static void TryEnableMica(Window window, IntPtr hwnd)
    {
        int backdrop = NativeMethods.DWMSBT_MAINWINDOW;
        if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
            return;

        var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0)
            return;

        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        window.Background = Brushes.Transparent;
    }
}
