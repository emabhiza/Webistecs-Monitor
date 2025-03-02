using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Logging;
using File = System.IO.File;
using static Webistecs_Monitor.WebistecsConstants;
using ILogger = Serilog.ILogger;

namespace Webistecs_Monitor
{
    public class MonitoringToolsBackup
    {
        private static readonly ILogger Logger = LoggerFactory.Create();
        private readonly ApplicationConfiguration _config;
        private readonly GoogleDriveService _googleDriveService;

        public MonitoringToolsBackup(ApplicationConfiguration config, GoogleDriveService googleDriveService)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config), "❌ _config is null!");
            _googleDriveService = googleDriveService ?? throw new ArgumentNullException(nameof(googleDriveService), "❌ GoogleDriveService is null!");
        }

        public async Task RunBackupProcess(CancellationToken cancellationToken)
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
            Logger.Debug("Using date string for backup: {TodayDate}", todayDate);

            foreach (var tool in MonitoringFolderIds.Tools)
            {
                Logger.Information("Processing tool: {Tool}", tool);

                // Backup data directory
                var dataDir = MonitoringFolderIds.GetLocalPath(tool);
                Logger.Debug("Local data path for {Tool}: {DataDir}", tool, dataDir);
                if (!string.IsNullOrEmpty(dataDir))
                {
                    await BackupDirectory(tool, dataDir, todayDate);
                }
                else
                {
                    Logger.Warning("No local data directory defined for {Tool}. Skipping data backup.", tool);
                }

                // Backup configuration files (if applicable)
                var configDir = MonitoringFolderIds.GetConfigPath(tool);
                Logger.Debug("Configuration path for {Tool}: {ConfigDir}", tool, configDir);
                if (!string.IsNullOrEmpty(configDir))
                {
                    await BackupDirectory($"{tool}-config", configDir, todayDate);
                }
                else
                {
                    Logger.Warning("No configuration directory defined for {Tool}. Skipping config backup.", tool);
                }

                // Create Prometheus snapshot (if applicable)
                if (tool == "prometheus")
                {
                    Logger.Information("Prometheus detected. Initiating snapshot creation...");
                    await CreatePrometheusSnapshot();
                }
            }
        }

        private async Task BackupDirectory(string tool, string sourceDir, string todayDate)
        {
            Logger.Information("Backing up directory for {Tool}: {SourceDir}", tool, sourceDir);

            var zipFileName = $"{tool}-{todayDate}.zip";
            var zipFilePath = Path.Combine(_config.LocalBackUpPath, zipFileName);
            Logger.Debug("ZIP file will be created at: {ZipFilePath}", zipFilePath);

            var createdZipFile = await TryZipDirectory(sourceDir, zipFilePath);
            if (createdZipFile == null)
            {
                Logger.Information("Skipping backup for {Tool} - directory missing or zipping failed.", tool);
                return;
            }

            var folderId = MonitoringFolderIds.GetFolderId(tool);
            if (string.IsNullOrEmpty(folderId))
            {
                Logger.Information("No Google Drive folder ID found for {Tool}. Skipping upload.", tool);
                return;
            }

            // Check if file already exists
            var existingFileId = await _googleDriveService.FindFileInGoogleDrive(zipFileName, folderId);
            if (!string.IsNullOrEmpty(existingFileId))
            {
                Logger.Debug("Existing backup file found (ID: {ExistingFileId}). Deleting it...", existingFileId);
                await _googleDriveService.DeleteFileFromGoogleDrive(existingFileId);
                Logger.Information("Deleted previous {Tool} backup from Google Drive (File ID: {ExistingFileId})", tool, existingFileId);
            }

            // Upload the new backup file
            Logger.Debug("Uploading new backup file {ZipFileName} to folder {FolderId}", zipFileName, folderId);
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
                Logger.Debug("Creating ZIP file: {DestinationZipFile}", destinationZipFile);
                using var zipToCreate = new FileStream(destinationZipFile, FileMode.Create);
                using var archive = new ZipArchive(zipToCreate, ZipArchiveMode.Create, true);

                int fileCount = 0;
                foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    var entryName = file.Substring(sourceDir.Length + 1);
                    try
                    {
                        if (file.EndsWith("lock"))
                        {
                            Logger.Debug("Skipping lock file: {File}", file);
                            continue;
                        }

                        Logger.Debug("Adding file to ZIP: {File}", file);
                        await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        await using var entryStream = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                        await fileStream.CopyToAsync(entryStream);
                        fileCount++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("Skipping file {File} due to error: {Message}", file, ex.Message);
                    }
                }
                Logger.Information("ZIP file created: {DestinationZipFile}. Total files added: {FileCount}", destinationZipFile, fileCount);
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
                Logger.Debug("Sending POST request to Prometheus snapshot API...");
                var response = await httpClient.PostAsync("http://192.168.68.107:30090/api/v1/admin/tsdb/snapshot", null);
                response.EnsureSuccessStatusCode();
                Logger.Debug("Snapshot API response received.");

                var snapshotResponse = await response.Content.ReadAsStringAsync();
                Logger.Debug("Snapshot response content: {SnapshotResponse}", snapshotResponse);

                // Parse the snapshot name from the response
                var snapshotNameParts = snapshotResponse.Split('"');
                if (snapshotNameParts.Length < 4)
                {
                    Logger.Error("Snapshot response format unexpected. Unable to parse snapshot name.");
                    return;
                }
                var snapshotName = snapshotNameParts[3];
                Logger.Debug("Parsed snapshot name: {SnapshotName}", snapshotName);

                var snapshotDir = Path.Combine("/mnt/source/prometheus/data/snapshots", snapshotName);
                Logger.Information("Prometheus snapshot directory: {SnapshotDir}", snapshotDir);

                // Backup the snapshot directory
                await BackupDirectory("prometheus-snapshot", snapshotDir, DateTime.Now.ToString(DateFormat));
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to create Prometheus snapshot: {Message} {StackTrace}", ex.Message, ex.StackTrace);
            }
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
