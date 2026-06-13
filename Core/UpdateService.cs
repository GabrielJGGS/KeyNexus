using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace KeyNexus.Core;

public class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public bool IsNewer { get; set; }
}

public static class UpdateService
{
    public const string GitHubRepo = "GabrielJGGS/KeyNexus";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "KeyNexus-Updater");
        Http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public static string GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
    }

    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            string url = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            var response = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
            string body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
                return null;

            var current = Version.Parse(GetCurrentVersion());
            var latest = Version.Parse(tag);

            return new UpdateInfo
            {
                Version = tag,
                DownloadUrl = downloadUrl,
                ReleaseNotes = body,
                IsNewer = latest > current
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao verificar atualizações", ex);
            return null;
        }
    }

    public static async Task<bool> DownloadAndApplyUpdateAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Logger.Error("UpdateService: ProcessPath nulo");
                return false;
            }

            string dir = Path.GetDirectoryName(exePath)!;
            string tempPath = Path.Combine(dir, "KeyNexus.update.exe");
            string oldPath = Path.Combine(dir, "KeyNexus.old.exe");

            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(oldPath)) File.Delete(oldPath);

            using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;
                if (totalBytes > 0)
                    progress?.Report((double)downloaded / totalBytes);
            }

            fileStream.Close();

            File.Move(exePath, oldPath, overwrite: true);
            File.Move(tempPath, exePath, overwrite: true);

            Logger.Info($"Atualização {info.Version} aplicada com sucesso");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao baixar/aplicar atualização", ex);
            return false;
        }
    }

    public static void CleanupOldExecutable()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return;

            string oldPath = Path.Combine(Path.GetDirectoryName(exePath)!, "KeyNexus.old.exe");
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
                Logger.Info("Arquivo KeyNexus.old.exe removido");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao limpar executável antigo", ex);
        }
    }

    public static void RestartApplication()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return;

        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }
}
