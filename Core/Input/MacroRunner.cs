using System;
using System.Collections.Concurrent;
using System.Threading;
using KeyNexus.Core.Profiles;

namespace KeyNexus.Core.Input;

/// <summary>
/// Executa macros com atraso numa thread única, em ordem. O hook nunca espera por elas.
/// </summary>
internal sealed class MacroRunner : IDisposable
{
    private readonly BlockingCollection<(CompiledAction Action, IntPtr Hkl)> _queue = new(new ConcurrentQueue<(CompiledAction, IntPtr)>());
    private readonly Thread _thread;

    public MacroRunner()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "KeyNexus.Macro"
        };
        _thread.Start();
    }

    public void Enqueue(CompiledAction action, IntPtr hkl)
    {
        if (!_queue.IsAddingCompleted)
            _queue.TryAdd((action, hkl));
    }

    private void Run()
    {
        var buffer = new NativeMethods.INPUT[64];
        try
        {
            foreach (var (action, hkl) in _queue.GetConsumingEnumerable())
            {
                foreach (var step in action.Steps)
                {
                    if (step.DelayMs > 0)
                        Thread.Sleep(step.DelayMs);

                    int needed = Math.Max(16, 2 * step.Text.Length + 16);
                    if (buffer.Length < needed)
                        buffer = new NativeMethods.INPUT[needed];

                    int n = InputBatchBuilder.AppendStep(buffer, 0, step, hkl);
                    if (n > 0)
                        NativeMethods.SendInput((uint)n, buffer, NativeMethods.InputStructSize);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("MacroRunner: falha ao executar macro", ex);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(500);
        _queue.Dispose();
    }
}
