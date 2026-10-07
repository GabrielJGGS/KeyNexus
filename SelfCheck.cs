#if DEBUG
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using KeyNexus.Core;
using KeyNexus.ViewModels;

namespace KeyNexus;

/// <summary>
/// Diagnóstico de desenvolvimento (só Debug, argumento --self-check): abre cada janela fora da tela,
/// registra no log os nomes resolvidos e encerra com código 0 se nada falhou.
/// </summary>
internal static class SelfCheck
{
    public static async void Run(App app)
    {
        int failures = 0;
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            failures++;
            Logger.Error($"SELF-CHECK exceção na UI: {e.Exception}");
            e.Handled = true;
        }
        app.DispatcherUnhandledException += OnUnhandled;

        async Task Check(string name, Func<Task> action)
        {
            try
            {
                await action();
                Logger.Info($"SELF-CHECK ok: {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Logger.Error($"SELF-CHECK falhou: {name}: {ex}");
            }
        }

        var groups = app.Monitor.GetConnectedKeyboardGroups();

        await Check("layouts", () =>
        {
            foreach (var info in KeyboardLayoutCatalog.GetInstalledLayouts())
                Logger.Info($"SELF-CHECK layout {info.Hkl} → KLID {info.Klid}: {info.LayoutName} · {info.LanguageName}");
            return Task.CompletedTask;
        });

        await Check("teclados", () =>
        {
            foreach (var group in groups)
                Logger.Info($"SELF-CHECK teclado {group.GroupKey}: {DeviceNameResolver.GetFriendlyName(group.RepresentativePath)} ({group.CollectionCount} coleções)");
            return Task.CompletedTask;
        });

        var target = groups.FirstOrDefault(g => app.Monitor.Config.GetRemapRuleCount(g.GroupKey) > 0)
            ?? groups.FirstOrDefault(g => app.Monitor.Config.GetLayoutForDevice(g.GroupKey) != null)
            ?? groups.FirstOrDefault();
        string groupKey = target?.GroupKey ?? "HID#TESTE";

        await Check("MainWindow", async () =>
        {
            var main = new MainWindow();
            Show(main);
            await Task.Delay(2500);
            typeof(MainWindow)
                .GetMethod("OnActiveKeyboardChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                .Invoke(main, new object[] { groupKey });
            main.tglSaved.IsChecked = true;
            await Task.Delay(300);
            SaveScreenshot(main, "main");
            main.settingsPopup.IsOpen = true;
            main.UpdateLayout();
            await Task.Delay(300);
            main.settingsPopup.IsOpen = false;
            main.Close();
        });

        var configured = app.Monitor.Config.GetConfiguredGroupKeys()
            .FirstOrDefault(k => app.Monitor.Config.GetRemapRuleCount(k) > 0) ?? groupKey;

        await Check("RemapEditorWindow", async () =>
        {
            var editor = new RemapEditorWindow(configured, "FreeWolf", app.Monitor);
            Show(editor);
            await Task.Delay(500);
            SaveScreenshot(editor, "editor");
            editor.Close();
        });

        await Check("DeviceInfoWindow", async () =>
        {
            var vm = new KeyboardItemViewModel(app.Monitor.Config, groupKey, new ObservableCollection<LayoutOption>())
            {
                RawDevicePath = target?.RepresentativePath ?? string.Empty,
                RawPaths = target?.RawPaths ?? new()
            };
            var info = new DeviceInfoWindow(vm, app.Monitor.Config);
            Show(info);
            await Task.Delay(1500);
            SaveScreenshot(info, "details");
            info.Close();
        });

        app.DispatcherUnhandledException -= OnUnhandled;
        Logger.Info($"SELF-CHECK terminou com {failures} falha(s)");
        app.Shutdown(failures == 0 ? 0 : 1);
    }

    /// <summary>Salva a janela em %TEMP%\kn-selfcheck (sem o Mica, que não entra no bitmap).</summary>
    private static void SaveScreenshot(Window window, string name)
    {
        window.UpdateLayout();
        if (window.Content is not FrameworkElement root)
            return;

        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0)
            return;

        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = (System.Windows.Media.Brush)window.FindResource("BgDarkBrush");
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            var offset = root.TranslatePoint(new System.Windows.Point(0, 0), window);
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null,
                new Rect(offset.X, offset.Y, root.ActualWidth, root.ActualHeight));
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kn-selfcheck");
        System.IO.Directory.CreateDirectory(dir);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"));
        encoder.Save(file);
    }

    private static void Show(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();
    }
}
#endif
