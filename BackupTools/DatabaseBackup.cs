using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Logging;
using ILogger = Serilog.ILogger;

namespace Webistecs_Monitor.BackupTools;

public class DatabaseBackup
{
    private static readonly ILogger Logger = LoggerFactory.Create();
    private readonly ApplicationConfiguration _config;
    private readonly GoogleDriveService _googleDriveService;

    public DatabaseBackup(ApplicationConfiguration config, GoogleDriveService googleDriveService)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config), "Configuration is null. Ensure it is loaded properly.");
        _googleDriveService = googleDriveService ?? throw new ArgumentNullException(nameof(googleDriveService), "Google Drive Service is null. Ensure it is registered in DI.");
    }

    public async Task RunBackupProcess(CancellationToken cancellationToken)
    {
        Logger.Information("📤 Starting database backup process...");

        try
        {
            // Define the backup file name and path
            var dbBackupFileName = $"webistecs-db-{DateTime.UtcNow:yyyy-MM-dd}.db";
            var dbFilePath = Path.Combine(_config.BackupPath, dbBackupFileName);

            Logger.Information("🛠 Database backup will be saved at: {FilePath}", dbFilePath);

            // Create the database backup
            await CreateDatabaseBackup(dbFilePath, cancellationToken);

            // Verify the backup file exists
            if (!File.Exists(dbFilePath))
            {
                Logger.Error("❌ ERROR: Backup file `{FilePath}` does NOT exist. Skipping upload.", dbFilePath);
                LogAvailableBackupFiles();
                return;
            }

            // Upload the backup to Google Drive
            Logger.Information("📤 Preparing to upload database backup: {FilePath}", dbFilePath);
            await _googleDriveService.UploadFileToCorrectFolder(dbFilePath, WebistecsConstants.DatabaseBackupFolderId, "text/plain");

            Logger.Information("✅ Successfully uploaded database backup: {FileName}", Path.GetFileName(dbFilePath));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "❌ An error occurred during the backup process: {Message}", ex.Message);
        }
    }

    private async Task CreateDatabaseBackup(string filePath, CancellationToken cancellationToken)
    {
        Logger.Information("🔄 Creating database backup: {FilePath}", filePath);

        try
        {
            // Ensure the backup directory exists
            Directory.CreateDirectory(_config.BackupPath);

            // Copy the database file to the backup location
            File.Copy(_config.DbPath, filePath, overwrite: true);

            Logger.Information("✅ Database backup successfully created: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "❌ Error during database backup: {Message}", ex.Message);
            throw; // Propagate the exception to handle it upstream
        }
    }

    private void LogAvailableBackupFiles()
    {
        try
        {
            var backupFiles = Directory.GetFiles(_config.BackupPath);
            Logger.Warning("📂 Available backup files in `{BackupPath}`: {BackupFiles}",
                _config.BackupPath, string.Join(", ", backupFiles));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "❌ Failed to list backup files in directory: {BackupPath}", _config.BackupPath);
        }
    }
}