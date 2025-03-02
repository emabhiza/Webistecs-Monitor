using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Logging;
using ILogger = Serilog.ILogger;

namespace Webistecs_Monitor.BackupTools
{
    public class LogsBackup : IHostedService, IDisposable
    {
        private Timer? _timer;
        private static readonly ILogger Logger = LoggerFactory.Create();
        private readonly ApplicationConfiguration _config;
        private readonly GoogleDriveService _googleDriveService;

        public LogsBackup(ApplicationConfiguration config, GoogleDriveService googleDriveService)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config), "❌ _config is null! Ensure configuration is loaded properly.");
            _googleDriveService = googleDriveService ?? throw new ArgumentNullException(nameof(googleDriveService), "❌ _googleDriveService is null! Ensure Google Drive service is initialized.");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Logger.Information("🚀 Starting Loki Logs Backup Service...");
            await RunBackupProcess();
            Logger.Information("✅ Loki Logs Backup Service process completed.");
        }

        public async Task RunBackupProcess()
        {
            Logger.Information("📡 Fetching logs from Loki...");

            var endTime = DateTimeOffset.UtcNow;
            var startTime = endTime.Date; // Fetch logs from today 00:00 to now

            Logger.Information("📅 Fetching logs from {StartTime} to {EndTime} (UTC)", startTime, endTime);

            var startNs = ToUnixNanoseconds(startTime);
            var endNs = ToUnixNanoseconds(endTime);

            const int limit = 1000;
            var currentEnd = endNs;
            var hasMore = true;
            var allEntries = new List<(DateTimeOffset Timestamp, string LogEntry)>();

            // ✅ STRICT FILTER: 
            //  - Only "webistecs" logs ✅
            //  - Excludes "webistecs-monitor" ✅
            //  - Removes unwanted logs (node_exporter, diskstats, systemd, prometheus) ✅
            const string query =
                "{job=\"kubernetes-logs\", filename=~\"/var/log/containers/webistecs-.*\", filename!~\"/var/log/containers/webistecs-monitor-.*\", source!~\"node_exporter|diskstats|systemd|prometheus\"}";

            using (var client = new HttpClient())
            {
                while (hasMore)
                {
                    var url = $"http://loki.monitoring.svc.cluster.local:3100/loki/api/v1/query_range" +
                              $"?query={Uri.EscapeDataString(query)}&limit={limit}&start={startNs}&end={currentEnd}";

                    Logger.Information("📡 Querying Loki: {Url}", url);
                    var response = await client.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Error("❌ Error querying Loki: {StatusCode}", response.StatusCode);
                        break;
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    if (root.GetProperty("status").GetString() != "success")
                    {
                        Logger.Error("❌ Loki query failed.");
                        break;
                    }

                    var data = root.GetProperty("data");
                    var result = data.GetProperty("result");

                    var count = 0;
                    long? firstLogTimestamp = null, lastLogTimestamp = null;

                    var logBuffer = new StringBuilder();
                    DateTimeOffset? lastTimestamp = null;

                    foreach (var stream in result.EnumerateArray())
                    {
                        if (stream.TryGetProperty("values", out JsonElement valuesElement))
                        {
                            foreach (var value in valuesElement.EnumerateArray())
                            {
                                var timestampStr = value[0].GetString();
                                var message = value[1].GetString();
                                if (!long.TryParse(timestampStr, out long ts))
                                    continue;

                                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ts / 1_000_000);

                                if (firstLogTimestamp == null) firstLogTimestamp = ts;
                                lastLogTimestamp = ts;

                                // Extract readable timestamp
                                string[] messageParts = message.Split(' ');
                                string rawTimestamp = messageParts.Length > 0 ? messageParts[0] : string.Empty;
                                string cleanedMessage = message.Replace("stdout F", "").Trim();

                                // Parse timestamp
                                if (!DateTimeOffset.TryParse(rawTimestamp, out var readableTimestamp))
                                {
                                    readableTimestamp = timestamp;
                                }

                                string formattedTimestamp = readableTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");

                                // ✅ Multi-line log handling (Detect stack traces & continuation logs)
                                if (cleanedMessage.StartsWith(" ") || cleanedMessage.StartsWith("\tat") || cleanedMessage.StartsWith("---"))
                                {
                                    logBuffer.AppendLine(cleanedMessage);
                                }
                                else
                                {
                                    if (logBuffer.Length > 0 && lastTimestamp.HasValue)
                                    {
                                        allEntries.Add((lastTimestamp.Value, logBuffer.ToString().Trim()));
                                        logBuffer.Clear();
                                    }

                                    logBuffer.AppendLine($"{formattedTimestamp} {cleanedMessage}");
                                    lastTimestamp = readableTimestamp;
                                }

                                count++;
                            }
                        }
                    }

                    if (logBuffer.Length > 0 && lastTimestamp.HasValue)
                    {
                        allEntries.Add((lastTimestamp.Value, logBuffer.ToString().Trim()));
                    }

                    Logger.Information("📊 Fetched {Count} log entries.", count);

                    hasMore = count >= limit;
                    if (hasMore)
                    {
                        Logger.Information("🔄 Continuing pagination: New end timestamp {NewEnd}", lastLogTimestamp);
                        currentEnd = lastLogTimestamp ?? startNs;
                    }
                }
            }

            // ✅ Ensure logs append to today's file in DESC order
            var logFileName = $"logs-{DateTime.UtcNow:dd-MM}.log";
            var logsFilePath = Path.Combine(_config.BackupPath, logFileName);

            // ✅ Sort logs in DESC order before writing
            allEntries = allEntries.OrderByDescending(e => e.Timestamp).ToList();
            var logLines = allEntries.Select(e => e.LogEntry).ToList();

            if (File.Exists(logsFilePath))
            {
                Logger.Information("📁 Appending {Count} log entries to existing file {FilePath}", logLines.Count, logsFilePath);
                File.AppendAllLines(logsFilePath, logLines);
            }
            else
            {
                Logger.Information("📁 Creating new log file {FilePath} with {Count} entries", logsFilePath, logLines.Count);
                File.WriteAllLines(logsFilePath, logLines);
            }

            // ✅ Maintain only the last 7 days of logs
            CleanupOldLogs();

            // ✅ Upload logs to Google Drive
            try
            {
                
                await _googleDriveService.UploadLogsToGoogleDrive(logsFilePath);
                Logger.Information("✅ Log file uploaded to Google Drive: {FileName}", Path.GetFileName(logsFilePath));
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Error uploading log file to Google Drive: {Message}", ex.Message);
            }
        }

        private void CleanupOldLogs()
        {
            var logFiles = Directory.GetFiles(_config.BackupPath, "logs-*.log")
                                    .Select(file => new FileInfo(file))
                                    .OrderByDescending(file => file.CreationTimeUtc)
                                    .ToList();

            if (logFiles.Count > 7)
            {
                foreach (var file in logFiles.Skip(7))
                {
                    Logger.Information("🗑 Deleting old log file: {FileName}", file.Name);
                    file.Delete();
                }
            }
        }

        private static long ToUnixNanoseconds(DateTimeOffset dto)
        {
            return dto.ToUnixTimeMilliseconds() * 1_000_000;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
