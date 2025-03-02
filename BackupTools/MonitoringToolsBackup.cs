using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Logging;
using File = System.IO.File;
using static Webistecs_Monitor.WebistecsConstants;
using ILogger = Serilog.ILogger;

namespace Webistecs_Monitor
{
    public class MonitoringToolsBackup : IHostedService, IDisposable
    {
        private static readonly ILogger Logger = LoggerFactory.Create();
        private readonly ApplicationConfiguration _config;
        private readonly GoogleDriveService _googleDriveService;
        private Timer? _timer;

        public MonitoringToolsBackup(ApplicationConfiguration config, GoogleDriveService googleDriveService)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config), "❌ _config is null!");
            _googleDriveService = googleDriveService ?? throw new ArgumentNullException(nameof(googleDriveService), "❌ GoogleDriveService is null!");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                Logger.Information("🚀 Starting Monitoring Tools Backup...");
                await RunBackupProcess();
                Logger.Information("✅ Backup process completed.");
            }
            catch (Exception ex)
            {
                Logger.Error("🔥 Error during startup: {Message} {StackTrace}", ex.Message, ex.StackTrace);
            }
        }

        private async Task RunBackupProcess()
        {
            var todayDate = DateTime.Now.ToString(DateFormat);

            foreach (var tool in MonitoringFolderIds.Tools)
            {
                Logger.Information("Processing tool: {Tool}", tool);

                // Backup data directory
                var dataDir = MonitoringFolderIds.GetLocalPath(tool);
                if (!string.IsNullOrEmpty(dataDir))
                {
                    await BackupDirectory(tool, dataDir, todayDate);
                }

                // Backup configuration files (if applicable)
                var configDir = MonitoringFolderIds.GetConfigPath(tool);
                if (!string.IsNullOrEmpty(configDir))
                {
                    await BackupDirectory($"{tool}-config", configDir, todayDate);
                }

                // Create Prometheus snapshot (if applicable)
                if (tool == "prometheus")
                {
                    await CreatePrometheusSnapshot();
                }
            }
        }

        private async Task BackupDirectory(string tool, string sourceDir, string todayDate)
        {
            Logger.Information("Backing up directory for {Tool}: {SourceDir}", tool, sourceDir);

            var zipFileName = $"{tool}-{todayDate}.zip";
            var zipFilePath = Path.Combine(_config.LocalBackUpPath, zipFileName);
            var createdZipFile = await TryZipDirectory(sourceDir, zipFilePath);

            if (createdZipFile == null)
            {
                Logger.Information("Skipping backup for {Tool} - directory missing or zipping failed.", tool);
                return;
            }

            var folderId = MonitoringFolderIds.GetFolderId(tool);
            if (string.IsNullOrEmpty(folderId))
            {
                Logger.Information("No Google Drive folder ID found for {Tool}", tool);
                return;
            }

            // Check if file already exists
            var existingFileId = await _googleDriveService.FindFileInGoogleDrive(zipFileName, folderId);
            if (!string.IsNullOrEmpty(existingFileId))
            {
                await _googleDriveService.DeleteFileFromGoogleDrive(existingFileId);
                Logger.Information("Deleted previous {Tool} backup from Google Drive (File ID: {ExistingFileId})", tool, existingFileId);
            }

            // ✅ Corrected function call
            await _googleDriveService.UploadFileToGoogleDrive(createdZipFile, folderId, "Monitoring Backup", JsonApplicationType);
            Logger.Information("✅ {Tool} backup successfully uploaded to Google Drive.", tool);
        }

        private async Task<string?> TryZipDirectory(string sourceDir, string destinationZipFile)
        {
            if (!Directory.Exists(sourceDir))
            {
                Logger.Warning("Directory {SourceDir} does not exist. Skipping ZIP creation.", sourceDir);
                return null;
            }

            if (File.Exists(destinationZipFile))
            {
                Logger.Information("Existing backup file {DestinationZipFile} found. Deleting...", destinationZipFile);
                File.Delete(destinationZipFile);
            }

            try
            {
                using var zipToCreate = new FileStream(destinationZipFile, FileMode.Create);
                using var archive = new ZipArchive(zipToCreate, ZipArchiveMode.Create, true);

                foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    var entryName = file.Substring(sourceDir.Length + 1);
                    try
                    {
                        if (file.EndsWith("lock"))
                        {
                            Logger.Information("Skipping lock file: {File}", file);
                            continue;
                        }

                        await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        await using var entryStream = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                        await fileStream.CopyToAsync(entryStream);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("Skipping file {File} due to error: {Message}", file, ex.Message);
                    }
                }

                Logger.Information("ZIP file created: {DestinationZipFile}", destinationZipFile);
                return destinationZipFile;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to create ZIP file: {Message}", ex.Message);
                return null;
            }
        }

        private async Task CreatePrometheusSnapshot()
        {
            Logger.Information("Creating Prometheus snapshot...");

            try
            {
                using var httpClient = new HttpClient();
                var response = await httpClient.PostAsync("http://localhost:9090/api/v1/admin/tsdb/snapshot", null);
                response.EnsureSuccessStatusCode();

                var snapshotResponse = await response.Content.ReadAsStringAsync();
                var snapshotName = snapshotResponse.Split('"')[3];

                var snapshotDir = Path.Combine("/mnt/source/prometheus/data/snapshots", snapshotName);
                Logger.Information("Prometheus snapshot created: {SnapshotDir}", snapshotDir);

                await BackupDirectory("prometheus-snapshot", snapshotDir, DateTime.Now.ToString(DateFormat));
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to create Prometheus snapshot: {Message} {StackTrace}", ex.Message, ex.StackTrace);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Logger.Information("🛑 Stopping MonitoringToolsBackup Service...");
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}

public static class MonitoringFolderIds
{
    public static readonly List<string> Tools = new() { "grafana", "prometheus" };

    public static string GetLocalPath(string tool) => tool switch
    {
        "grafana" => "/mnt/source/grafana/data",
        "prometheus" => "/mnt/source/prometheus/data",
        _ => null
    } ?? throw new InvalidOperationException();

    public static string GetConfigPath(string tool) => tool switch
    {
        "grafana" => "/etc/grafana",
        "prometheus" => "/etc/prometheus",
        _ => null
    } ?? throw new InvalidOperationException();

    public static string GetFolderId(string tool) => tool switch
    {
        "grafana" => "1zd8juWDQvUDhmM45IMm56PNzAXVChRQT",
        "prometheus" => "1SZ7kA2Df9MvlBR158QaqnmQ54VBhm1wG",
        "prometheus-config" => "1SZ7kA2Df9MvlBR158QaqnmQ54VBhm1wG",
        "prometheus-snapshot" => "1SZ7kA2Df9MvlBR158QaqnmQ54VBhm1wG",
        _ => null
    } ?? throw new InvalidOperationException();
}
