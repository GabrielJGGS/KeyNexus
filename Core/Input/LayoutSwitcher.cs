using System;
using System.Collections.Concurrent;
using System.Threading;

namespace KeyNexus.Core.Input;

/// <summary>
/// Troca o layout da janela em foco. Na thread de entrada só faz PostMessage (nunca bloqueia);
/// se a janela não aceitar, uma thread auxiliar tenta o caminho antigo com AttachThreadInput.
/// </summary>
internal sealed class LayoutSwitcher : IDisposable
{
    private const int FallbackDelayMs = 120;

    private readonly BlockingCollection<(IntPtr Hkl, IntPtr Hwnd)> _fallback = new(boundedCapacity: 16);
    private readonly Thread _fallbackThread;

    public LayoutSwitcher()
    {
        _fallbackThread = new Thread(RunFallback)
        {
            IsBackground = true,
            Name = "KeyNexus.LayoutFallback"
        };
        _fallbackThread.Start();
    }

    /// <summary>Retorna true quando pediu a troca (a janela estava com outro layout).</summary>
    public bool Apply(IntPtr hkl, IntPtr hwnd)
    {
        if (hkl == IntPtr.Zero || hwnd == IntPtr.Zero)
            return false;

        uint thread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (thread == 0 || NativeMethods.GetKeyboardLayout(thread) == hkl)
            return false;

        NativeMethods.PostMessage(hwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        _fallback.TryAdd((hkl, hwnd));
        return true;
    }

    private void RunFallback()
    {
        // AttachThreadInput exige fila de mensagens nas duas threads.
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);

        try
        {
            foreach (var (hkl, hwnd) in _fallback.GetConsumingEnumerable())
            {
                Thread.Sleep(FallbackDelayMs);

                if (NativeMethods.GetForegroundWindow() != hwnd)
                    continue;

                uint target = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
                if (target == 0 || NativeMethods.GetKeyboardLayout(target) == hkl)
                    continue;

                ForceLayout(hkl, hwnd, target);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("LayoutSwitcher: falha na thread auxiliar", ex);
        }
    }

    private static void ForceLayout(IntPtr hkl, IntPtr hwnd, uint targetThread)
    {
        uint current = NativeMethods.GetCurrentThreadId();
        bool attached = false;
        try
        {
            attached = NativeMethods.AttachThreadInput(current, targetThread, true);
            NativeMethods.ActivateKeyboardLayout(hkl, NativeMethods.KLF_SETFORPROCESS);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
            Logger.Info($"LayoutSwitcher: troca reforçada para {(uint)hkl.ToInt64():X8} na janela 0x{hwnd.ToInt64():X}");
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(current, targetThread, false);
        }
    }

    public void Dispose()
    {
        _fallback.CompleteAdding();
        _fallbackThread.Join(500);
        _fallback.Dispose();
    }
}
