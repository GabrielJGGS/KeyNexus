using System;
using System.IO;

namespace KeyNexus.Core;

public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyNexus");
    private static readonly string LogFile = Path.Combine(LogDir, "keynexus.log");
    private static readonly object _lock = new();

    static Logger()
    {
        Directory.CreateDirectory(LogDir);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                File.AppendAllText(LogFile, line + Environment.NewLine);

                // Rotaciona o log se exceder 5MB
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    string backup = LogFile + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(LogFile, backup);
                }
            }
        }
        catch
        {
            // Silencia erros de gravação de log
        }
    }
}
