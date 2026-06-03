using System.Threading.Tasks;
using GripTrader.Core.Models;

namespace GripTrader.Core.Abstractions
{
    /// <summary>
    /// The seam between a backtest feeder and a strategy: the feeder pushes one
    /// synchronous tick (and the macro long-filter flag) into whatever strategy is
    /// under test, with no knowledge of the concrete strategy type. GridBot
    /// implements this (forwarding to its internal ProcessBacktestTickAsync); a
    /// future, different strategy implements it the same way.
    /// <para><paramref name="isUptrend"/> means "is the macro long-filter currently
    /// permissive?" — strategies that don't use a trend filter may ignore it.</para>
    /// </summary>
    public interface IBacktestTickReceiver
    {
        Task OnBacktestTickAsync(BidAsk quote, bool? isUptrend);
    }
}
