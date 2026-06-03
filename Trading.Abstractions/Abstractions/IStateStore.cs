using System.Threading.Tasks;

namespace GripTrader.Core.Abstractions
{
    /// <summary>
    /// Strategy-agnostic persistence seam for a strategy's serialisable state
    /// blob. The reusable backtest/host layers depend only on this generic
    /// contract; each strategy closes it over its own state type (e.g. GridBot
    /// over GridState). Keeps the backtest layer free of any concrete strategy
    /// state shape.
    /// </summary>
    public interface IStateStore<TState> where TState : class
    {
        Task<TState?> LoadAsync(string symbol);
        Task SaveAsync(TState state);
        Task SaveIfDueAsync(TState state);
    }
}
