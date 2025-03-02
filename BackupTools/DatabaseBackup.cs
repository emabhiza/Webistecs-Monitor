using Microsoft.Extensions.Hosting;
using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Logging;
using ILogger = Serilog.ILogger;

namespace Webistecs_Monitor.BackupTools;

public class DatabaseBackup : IHostedService, IDisposable 
{
    private Timer? _timer;
    private static readonly ILogger Logger = LoggerFactory.Create();
    private readonly ApplicationConfiguration _config;
    private readonly GoogleDriveService _googleDriveService; 

    public DatabaseBackup(ApplicationConfiguration config, GoogleDriveService googleDriveService)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config), "❌ _config is null! Ensure configuration is loaded properly.");
        _googleDriveService = googleDriveService ?? throw new ArgumentNullException(nameof(googleDriveService), "❌ Google Drive Service is null! Ensure it is registered in DI.");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.Information("🚀 Starting Webistecs DB Backup Service...");

        await RunBackupProcess();

        Logger.Information("✅ Webistecs DB process completed, stopping service.");
    }

    public async Task RunBackupProcess()
    {
        Logger.Information("📤 Starting database backup process...");

        // 🔹 Define the backup file name & path
        var dbBackupFileName = $"webistecs-db-{DateTime.UtcNow:yyyy-MM-dd}.db";
        var dbFilePath = Path.Combine(_config.BackupPath, dbBackupFileName);

        // 🔍 Debugging: Log the path before creating the backup
        Logger.Information("🛠 Database backup will be saved at: {FilePath}", dbFilePath);

        // ✅ Ensure backup is created before uploading
        await CreateDatabaseBackup(dbFilePath);

        // 🔍 Verify file existence before attempting upload
        if (!File.Exists(dbFilePath))
        {
            Logger.Error("❌ ERROR: Backup file `{FilePath}` does NOT exist. Skipping upload.", dbFilePath);

            // 🔍 List all files in the backup directory for debugging
            var backupFiles = Directory.GetFiles(_config.BackupPath);
            Logger.Warning("📂 Available backup files in `{BackupPath}`: {BackupFiles}", 
                _config.BackupPath, string.Join(", ", backupFiles));

            return;
        }

        Logger.Information("📤 Preparing to upload database backup: {FilePath}", dbFilePath);

        // ✅ Upload to Google Drive
        await _googleDriveService.UploadFileToCorrectFolder(dbFilePath, WebistecsConstants.DatabaseBackupFolderId, "text/plain");

        Logger.Information("✅ Successfully uploaded database backup: {FileName}", Path.GetFileName(dbFilePath));
    }

    private async Task CreateDatabaseBackup(string filePath)
    {
        Logger.Information("🔄 Creating database backup: {FilePath}", filePath);

        try
        {
            // ✅ Ensure backup folder exists
            Directory.CreateDirectory(_config.BackupPath);

            // ✅ Execute SQLite backup command (or your DB engine's method)
            File.Copy(_config.DbPath, filePath, true); // Overwrites if exists

            Logger.Information("✅ Database backup successfully created: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            Logger.Error("❌ Error during database backup: {Message}", ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _timer?.Dispose();
}
