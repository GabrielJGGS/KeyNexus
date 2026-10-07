using System;
using System.Threading;

namespace KeyNexus.Core;

/// <summary>
/// Garante um único KeyNexus por sessão. Uma segunda execução só pede para a primeira mostrar a janela.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\KeyNexus.SingleInstance";
    private const string ShowEventName = @"Local\KeyNexus.ShowWindow";

    private readonly Mutex _mutex = new(false, MutexName);
    private readonly EventWaitHandle _showEvent = new(false, EventResetMode.AutoReset, ShowEventName);
    private RegisteredWaitHandle? _registration;
    private bool _owned;

    /// <summary>Tenta ser a instância principal, esperando até <paramref name="wait"/> (reinício após atualização).</summary>
    public bool TryAcquire(TimeSpan wait)
    {
        try
        {
            _owned = _mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
        }
        return _owned;
    }

    public void SignalFirstInstance() => _showEvent.Set();

    public void ListenForSecondInstances(Action onShowRequested)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Libera o mutex antes de iniciar outra cópia do app (reinício).</summary>
    public void Release()
    {
        if (!_owned)
            return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Já liberado ou outra thread: nada a fazer.
        }
        _owned = false;
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        Release();
        _showEvent.Dispose();
        _mutex.Dispose();
    }
}
