namespace AutoVolume.App;

/// <summary>单实例：第二个实例唤起第一个实例的设置窗口后退出。</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;

    public bool IsOwner { get; }

    public event Action? Activated;

    public SingleInstance(bool dev)
    {
        string suffix = dev ? "-dev" : "";
        _mutex = new Mutex(true, @"Local\AutoVolume" + suffix, out bool created);
        IsOwner = created;

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AutoVolume-activate" + suffix);

        if (IsOwner)
        {
            ThreadPool.RegisterWaitForSingleObject(_activateEvent,
                (_, _) => Activated?.Invoke(), null, Timeout.Infinite, false);
        }
    }

    public void Signal()
    {
        try { _activateEvent.Set(); } catch { }
    }

    public void Dispose()
    {
        if (IsOwner)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
        _activateEvent.Dispose();
    }
}
