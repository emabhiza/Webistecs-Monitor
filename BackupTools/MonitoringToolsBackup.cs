using System.IO.Compression;
using System.Text.Json;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Logging;
using File = System.IO.File;
using ILogger = Serilog.ILogger;

namespace Webistecs_Monitor
{
    public class MonitoringToolsBackup
    {
        private static readonly ILogger Logger = LoggerFactory.Create();
        private readonly GoogleDriveService _googleDriveService;

        public MonitoringToolsBackup(GoogleDriveService googleDriveService)
        {
            _googleDriveService = googleDriveService ??
                                  throw new ArgumentNullException(nameof(googleDriveService),
                                      "❌ GoogleDriveService is null!");
        }

        public async Task RunBackupProcess(CancellationToken cancellationToken)
        {
            try
            {
                Logger.Information("🚀 Starting backup: Prometheus Snapshots & Grafana Dashboards...");

                await CreatePrometheusSnapshot();
                await BackupGrafanaDashboards();

                Logger.Information("✅ Backup process completed.");
            }
            catch (Exception ex)
            {
                Logger.Error("🔥 Backup process failed: {Message} {StackTrace}", ex.Message, ex.StackTrace);
            }
        }

        private async Task CreatePrometheusSnapshot()
        {
            Logger.Information("Creating Prometheus snapshot...");

            try
            {
                // Log the Prometheus API endpoint
                Logger.Debug(
                    "📡 Sending request to Prometheus API: http://192.168.68.107:30090/api/v1/admin/tsdb/snapshot");

                using var httpClient = new HttpClient();
                var response =
                    await httpClient.PostAsync("http://192.168.68.107:30090/api/v1/admin/tsdb/snapshot", null);
                response.EnsureSuccessStatusCode();

                var snapshotResponse = await response.Content.ReadAsStringAsync();
                Logger.Debug("📄 Prometheus snapshot response: {SnapshotResponse}", snapshotResponse);

                var snapshotJson = JsonDocument.Parse(snapshotResponse);
                if (!snapshotJson.RootElement.TryGetProperty("data", out var dataElement) ||
                    !dataElement.TryGetProperty("name", out var snapshotNameElement))
                {
                    Logger.Error("❌ Snapshot response format unexpected. Unable to parse snapshot name.");
                    return;
                }

                var snapshotName = snapshotNameElement.GetString();

                // Locate the snapshot directory in Prometheus's data directory
                var snapshotDir = Path.Combine("/prometheus/data/snapshots", snapshotName);

                Logger.Debug("📂 Checking if snapshot directory exists: {SnapshotDir}", snapshotDir);
                if (!Directory.Exists(snapshotDir))
                {
                    Logger.Error("❌ Prometheus snapshot directory does not exist: {SnapshotDir}", snapshotDir);
                    return;
                }

                // Zip the snapshot directory
                var zipFilePath = Path.Combine("/tmp", $"prometheus-snapshot-{snapshotName}.zip");
                Logger.Information("Zipping Prometheus snapshot: {SnapshotDir} → {ZipFilePath}", snapshotDir,
                    zipFilePath);
                ZipFile.CreateFromDirectory(snapshotDir, zipFilePath, CompressionLevel.Optimal, true);
                Logger.Information("✅ Snapshot ZIP created: {ZipFilePath}", zipFilePath);

                // Upload the snapshot to Google Drive
                Logger.Information("Uploading Prometheus snapshot to Google Drive...");
                await _googleDriveService.UploadFileToGoogleDrive(zipFilePath,
                    MonitoringFolderIds.GetFolderId("prometheus-snapshot"), "Monitoring Backup", "application/zip");
                Logger.Information("✅ Prometheus snapshot uploaded to Google Drive.");

                // Clean up: Delete the temporary ZIP file
                Logger.Debug("🧹 Cleaning up temporary files...");
                File.Delete(zipFilePath);
                Logger.Information("✅ Temporary files deleted.");
            }
            catch (Exception ex)
            {
                Logger.Error("🔥 Failed to create Prometheus snapshot: {Message} {StackTrace}", ex.Message,
                    ex.StackTrace);
            }
        }
        
        private async Task BackupGrafanaDashboards()
        {
            var grafanaDbPath = "/var/lib/grafana/grafana.db";
            var backupFilePath = $"/tmp/grafana-db-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db";

            Logger.Information("Backing up Grafana database: {GrafanaDbPath}", grafanaDbPath);

            Logger.Debug("🔍 Checking if Grafana database file exists: {GrafanaDbPath}", grafanaDbPath);
            if (!File.Exists(grafanaDbPath))
            {
                Logger.Warning("❌ Grafana database file not found!");
                return;
            }

            Logger.Debug("📂 Copying Grafana database to: {BackupFilePath}", backupFilePath);
            File.Copy(grafanaDbPath, backupFilePath, true);
            Logger.Information("✅ Grafana database backup created: {BackupFilePath}", backupFilePath);

            Logger.Information("Uploading Grafana database backup to Google Drive...");
            await _googleDriveService.UploadFileToGoogleDrive(backupFilePath,
                MonitoringFolderIds.GetFolderId("grafana-db"), "Monitoring Backup", "application/sqlite"); 
            Logger.Information("✅ Grafana database backup uploaded to Google Drive.");

            // Clean up: Delete the temporary backup file
            Logger.Debug("🧹 Cleaning up temporary files...");
            File.Delete(backupFilePath);
            Logger.Information("✅ Temporary files deleted.");
        }

    }
}

public static class MonitoringFolderIds
{
    public static string GetFolderId(string tool) => tool switch
    {
        "prometheus-snapshot" => "1SZ7kA2Df9MvlBR158QaqnmQ54VBhm1wG",
        "grafana-db" => "1zd8juWDQvUDhmM45IMm56PNzAXVChRQT", 
        _ => throw new ArgumentException($"Invalid tool name: {tool}", nameof(tool))
    };
}