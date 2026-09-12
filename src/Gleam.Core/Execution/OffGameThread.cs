using System.Runtime.CompilerServices;

namespace Gleam.Core.Execution;

/// <summary>
/// Continues on the thread pool, always. Awaiting work that is already on the game's thread runs it inline, and
/// ConfigureAwait(false) after a task that has already finished does not leave the thread either. So a scan started
/// from a click, or from a container opening, ran the whole planner inside one game frame.
/// </summary>
public static class OffGameThread
{
    public static Awaitable Hop() => default;

    public readonly struct Awaitable : INotifyCompletion
    {
        public Awaitable GetAwaiter() => this;
        public bool IsCompleted => false;
        public void OnCompleted(Action continuation) => ThreadPool.UnsafeQueueUserWorkItem(static c => c(), continuation, preferLocal: false);
        public void GetResult() { }
    }
}
