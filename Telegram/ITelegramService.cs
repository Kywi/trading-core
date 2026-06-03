using System.Threading;
using System.Threading.Tasks;

namespace Telegram
{
    public interface ITelegramService
    {
        Task SendMessageAsync(string message, CancellationToken cancellationToken = default);
        Task SendTradeClosedAsync(string symbol, decimal profit, decimal totalProfit, CancellationToken cancellationToken = default);
        Task SendBagCleanedAsync(string symbol, decimal cost, decimal entryPrice, CancellationToken cancellationToken = default);
        Task SendDailySummaryAsync(decimal totalProfit, decimal unrealizedPnL, int openPositions, string trendStatus, CancellationToken cancellationToken = default);
        Task SendErrorAsync(string errorMessage, CancellationToken cancellationToken = default);
        Task SendBackupAsync(string filePath, string caption, CancellationToken cancellationToken = default);
    }
}
