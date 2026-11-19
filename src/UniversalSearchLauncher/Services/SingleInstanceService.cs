namespace UniversalSearchLauncher.Services;

public sealed class SingleInstanceService : IDisposable
{
    public const string MutexName = @"Local\UniversalSearchLauncher.SingleInstance.5C0D2B87";

    private Mutex? _mutex;
    private bool _owned;

    public bool IsPrimary { get; private set; }
    public static uint ShowMessage { get; } = NativeMethods.RegisterWindowMessage("UniversalSearchLauncher.Show.5C0D2B87");

    public SingleInstanceService()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            _owned = createdNew;
            IsPrimary = createdNew;
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
            IsPrimary = true;
        }
    }

    public void NotifyExistingInstance()
    {
        NativeMethods.PostMessage((IntPtr)0xFFFF, ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // The process may be shutting down.
            }
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
