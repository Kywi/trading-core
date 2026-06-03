using Log;
using System;
using System.Buffers;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Telegram
{
    public sealed partial class TelegramService : ITelegramService
    {
        private const int MaxErrorMessageLength = 1000;
        private const long MaxBackupFileSize = 50L * 1024 * 1024; // Telegram bot upload cap
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

        // Matches Telegram bot tokens of the form "<digits>:<35+ url-safe chars>".
        // We strip these from any text we log to keep the bot token out of files.
        [GeneratedRegex(@"\d{6,}:[A-Za-z0-9_-]{30,}")]
        private static partial Regex BotTokenPattern();

        private readonly TelegramBotClient? _bot;
        private readonly long _chatId;
        private readonly bool _enabled;
        private readonly string _symbolTag;

        public TelegramService(string? botToken, string? chatId, bool enabled, string? symbol = null)
        {
            _symbolTag = string.IsNullOrWhiteSpace(symbol) ? "BOT" : symbol.ToUpperInvariant();

            _enabled = enabled
                       && !string.IsNullOrWhiteSpace(botToken)
                       && !string.IsNullOrWhiteSpace(chatId);

            if (!_enabled)
                return;

            if (!long.TryParse(chatId, out _chatId))
            {
                Logger.LogError($"[Telegram] Invalid ChatId: {chatId}");
                _enabled = false;
                return;
            }

            _bot = new TelegramBotClient(botToken!);
        }

        public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            if (!_enabled || _bot == null)
                return;

            try
            {
                await SendWithRateLimitRetryAsync(
                    ct => _bot.SendMessage(_chatId, message, parseMode: ParseMode.MarkdownV2, cancellationToken: ct),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Telegram] Failed to send message: {Sanitize(ex.Message)}");
            }
        }

        /// <summary>Strips bot-token-like substrings before logging.</summary>
        private static string Sanitize(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return BotTokenPattern().Replace(text, "<redacted-token>");
        }

        /// <summary>
        /// Executes a Telegram call with one rate-limit retry. On 429 the
        /// Telegram API returns a Parameters.RetryAfter hint (seconds); we
        /// honor it and try once more. External cancellation aborts the wait.
        /// </summary>
        private static async Task SendWithRateLimitRetryAsync(
            Func<CancellationToken, Task> sendAsync,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(CallTimeout);

                try
                {
                    await sendAsync(cts.Token).ConfigureAwait(false);
                    return;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 429 && attempt == 0)
                {
                    var retryAfter = TimeSpan.FromSeconds(ex.Parameters?.RetryAfter ?? 5);
                    Logger.LogInformation($"[Telegram] Rate-limited, retrying after {retryAfter.TotalSeconds:F0}s");
                    await Task.Delay(retryAfter, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Title prefix — symbol tag goes inside the bold header so the
        // Telegram notification preview (which only shows the first line)
        // immediately identifies which bot sent the message.
        private string Title(string emoji, string label) =>
            $"{emoji} *\\[{Esc(_symbolTag)}\\] {Esc(label)}*";

        public Task SendTradeClosedAsync(string symbol, decimal profit, decimal totalProfit, CancellationToken cancellationToken = default)
        {
            var emoji = profit >= 0m ? "✅" : "❌";
            var msg = Title(emoji, "Trade Closed") + "\n"
                    + $"Symbol: `{Esc(symbol)}`\n"
                    + $"Profit: `{Esc($"{profit:F4}")}` USDT\n"
                    + $"Total Realized: `{Esc($"{totalProfit:F4}")}` USDT";
            return SendMessageAsync(msg, cancellationToken);
        }

        public Task SendBagCleanedAsync(string symbol, decimal cost, decimal entryPrice, CancellationToken cancellationToken = default)
        {
            var msg = Title("\U0001F9F9", "Bag Cleaned") + "\n"
                    + $"Symbol: `{Esc(symbol)}`\n"
                    + $"Entry Price: `{Esc($"{entryPrice:F2}")}`\n"
                    + $"Cleaning Cost: `{Esc($"{cost:F4}")}` USDT";
            return SendMessageAsync(msg, cancellationToken);
        }

        public Task SendDailySummaryAsync(decimal totalProfit, decimal unrealizedPnL, int openPositions, string trendStatus, CancellationToken cancellationToken = default)
        {
            var msg = Title("\U0001F4CA", "Daily Summary") + "\n"
                    + $"Symbol: `{Esc(_symbolTag)}`\n"
                    + $"Realized PnL: `{Esc($"{totalProfit:F4}")}` USDT\n"
                    + $"Unrealized PnL: `{Esc($"{unrealizedPnL:F4}")}` USDT\n"
                    + $"Open Positions: `{openPositions}`\n"
                    + $"Trend: {Esc(trendStatus)}";
            return SendMessageAsync(msg, cancellationToken);
        }

        public Task SendErrorAsync(string errorMessage, CancellationToken cancellationToken = default)
        {
            var safe = Truncate(Sanitize(errorMessage), MaxErrorMessageLength);
            var msg = Title("\U0001F6A8", "Error") + $"\n`{Esc(safe)}`";
            return SendMessageAsync(msg, cancellationToken);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value[..maxLength] + "…";
        }

        // MarkdownV2 reserved characters that must be escaped when they appear
        // inside a non-formatting context. See Telegram Bot API spec.
        private static readonly SearchValues<char> MarkdownV2Reserved = SearchValues.Create(
            "_*[]()~`>#+-=|{}.!\\");

        private static string Esc(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            // Fast path: nothing to escape
            if (value.AsSpan().IndexOfAny(MarkdownV2Reserved) < 0)
                return value;

            var sb = new System.Text.StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                if (MarkdownV2Reserved.Contains(c))
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        public async Task SendBackupAsync(string filePath, string caption, CancellationToken cancellationToken = default)
        {
            if (!_enabled || _bot == null)
                return;

            if (!File.Exists(filePath))
            {
                Logger.LogError($"[Telegram] Backup file not found: {filePath}");
                return;
            }

            try
            {
                await using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 81920,
                    useAsync: true);

                if (stream.Length > MaxBackupFileSize)
                {
                    Logger.LogError($"[Telegram] Backup {filePath} is {stream.Length:N0} bytes, exceeds {MaxBackupFileSize:N0} byte upload cap");
                    return;
                }

                var fileName = Path.GetFileName(filePath);
                var inputFile = InputFile.FromStream(stream, fileName);

                // Backup upload streams the file body, so a retry would need to
                // rewind the stream and re-issue the request. Skip 429 retry
                // here - a missed daily backup is acceptable; a half-uploaded
                // one is not.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(CallTimeout);
                await _bot.SendDocument(_chatId, inputFile, caption: caption, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Telegram] Failed to send backup {filePath}: {Sanitize(ex.Message)}");
            }
        }
    }
}
