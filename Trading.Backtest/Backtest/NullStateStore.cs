using System.Threading.Tasks;
using GripTrader.Core.Abstractions;

namespace GripTrader.Core.Backtest
{
    /// <summary>
    /// Strategy-agnostic no-op state store for backtests: never persists, always
    /// loads <c>null</c> (a fresh start every run). Any strategy closes it over its
    /// own state type, e.g. <c>NullStateStore&lt;GridState&gt;</c>. Replaces the
    /// strategy-bound NullGridStateStore for reuse by new strategies.
    /// </summary>
    public sealed class NullStateStore<TState> : IStateStore<TState> where TState : class
    {
        public Task<TState?> LoadAsync(string symbol) => Task.FromResult<TState?>(null);
        public Task SaveAsync(TState state) => Task.CompletedTask;
        public Task SaveIfDueAsync(TState state) => Task.CompletedTask;
    }
}
