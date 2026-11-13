using System;
using System.Threading;

namespace HelmetDesk.Core
{
    /// <summary>Time source, replaceable in tests.</summary>
    public interface IClock
    {
        DateTimeOffset Now { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new();
        public DateTimeOffset Now => DateTimeOffset.Now;
    }

    /// <summary>
    /// Lets the services take a consistent copy of the data files: while held, the app does not write them.
    /// The app's save routine takes the same gate.
    /// </summary>
    public interface IDataGate
    {
        IDisposable Enter();
    }

    public sealed class SemaphoreGate : IDataGate
    {
        private readonly SemaphoreSlim _sem = new(1, 1);
        public IDisposable Enter()
        {
            _sem.Wait();
            return new Releaser(_sem);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim _s;
            public Releaser(SemaphoreSlim s) => _s = s;
            public void Dispose() { Interlocked.Exchange(ref _s, null)?.Release(); }
        }
    }

    /// <summary>Progress of a long operation: 0–100 and a short Portuguese message for the UI.</summary>
    public readonly record struct OperationProgress(int Percent, string Message);
}
