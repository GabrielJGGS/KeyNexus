using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace KeyNexus.Core;

/// <summary>
/// Log assíncrono: quem chama só enfileira; um escritor em segundo plano grava em disco.
/// A thread de entrada nunca espera por E/S.
/// </summary>
public static class Logger
{
    private const long MaxLogBytes = 5 * 1024 * 1024;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyNexus");
    private static readonly string LogFile = Path.Combine(LogDir, "keynexus.log");

    private static readonly Channel<string> Queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    private static readonly Task Writer;

    static Logger()
    {
        Directory.CreateDirectory(LogDir);
        Writer = Task.Run(WriteLoopAsync);
    }

    public static string LogDirectory => LogDir;

    public static void Info(string message) => Enqueue("INFO", message);
    public static void Error(string message) => Enqueue("ERROR", message);
    public static void Error(string message, Exception ex) => Enqueue("ERROR", $"{message}: {ex.Message}");

    [Conditional("DEBUG")]
    public static void Debug(string message) => Enqueue("DEBUG", message);

    /// <summary>Espera o escritor terminar de gravar o que estiver na fila (ao sair).</summary>
    public static void Flush(TimeSpan timeout)
    {
        Queue.Writer.TryComplete();
        try
        {
            Writer.Wait(timeout);
        }
        catch
        {
            // Encerrando: nada a fazer se o log falhar.
        }
    }

    private static void Enqueue(string level, string message) =>
        Queue.Writer.TryWrite($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}");

    private static async Task WriteLoopAsync()
    {
        var reader = Queue.Reader;
        var batch = new StringBuilder();

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (reader.TryRead(out var line))
                batch.AppendLine(line);

            try
            {
                File.AppendAllText(LogFile, batch.ToString());
                RotateIfNeeded();
            }
            catch
            {
                // Silencia erros de gravação de log
            }
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(LogFile);
        if (!info.Exists || info.Length <= MaxLogBytes)
            return;

        string backup = LogFile + ".old";
        if (File.Exists(backup))
            File.Delete(backup);
        File.Move(LogFile, backup);
    }
}
