using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Webistecs_Monitor.BackupTools;
using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Domain;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Grafana;

namespace Webistecs_Monitor;

public class Program
{
    public static async Task Main(string[] args)
    {
        // ✅ Set up logging to console (for `kubectl logs`)
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Debug()
            .CreateLogger();

        Log.Information("🚀 Webistecs Monitor Starting...");

        // ✅ Load Configuration BEFORE anything else
        var config = ApplicationConfiguration.LoadConfiguration();
        Log.Information("✅ Loaded Configuration: GOOGLE_CREDENTIALS={CredentialsPath}, DB_PATH={DbPath}, BACKUP_PATH={BackupPath}", 
            config.CredentialsPath, config.DbPath, config.BackupPath);

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(config); // ✅ Pass loaded config to DI
                services.AddSingleton<GoogleDriveService>();
                services.AddSingleton<DatabaseBackup>();
                services.AddSingleton<MonitoringToolsBackup>();
                services.AddSingleton<GrafanaExportService>();
                services.AddSingleton<LogsBackup>();
            })
            .Build();

        using var scope = host.Services.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        var googleDriveService = serviceProvider.GetRequiredService<GoogleDriveService>();
        var databaseBackup = serviceProvider.GetRequiredService<DatabaseBackup>();
        var monitoringBackup = serviceProvider.GetRequiredService<MonitoringToolsBackup>();
        var grafanaBackup = serviceProvider.GetRequiredService<GrafanaExportService>();
        var logsBackup = serviceProvider.GetRequiredService<LogsBackup>();

        try
        {
            Log.Information("📡 Fetching job schedule from Google Drive...");
            // var backupSchedule = await googleDriveService.ReadBackupStatusFromGoogleDrive();
            
            var backupSchedule = new Dictionary<string, TaskMetadata>
            {
                { "DatabaseBackup", new TaskMetadata { LastUpdate = DateTime.MinValue.ToString("o"), Schedule = "DAILY", OverrideAppHealthStatus = false, DisableUpdates = false } },
                { "MonitoringBackup", new TaskMetadata { LastUpdate = DateTime.MinValue.ToString("o"), Schedule = "HOURLY", OverrideAppHealthStatus = false, DisableUpdates = false } },
                { "LogBackup", new TaskMetadata { LastUpdate = DateTime.MinValue.ToString("o"), Schedule = "HOURLY", OverrideAppHealthStatus = false, DisableUpdates = false } },
                { "GrafanaBackup", new TaskMetadata { LastUpdate = DateTime.MinValue.ToString("o"), Schedule = "HOURLY", OverrideAppHealthStatus = false, DisableUpdates = false } }
            };
            
            var now = DateTime.UtcNow;

            if (backupSchedule == null)
            {
                Log.Warning("⚠️ No backup schedule found! Running all jobs as a fallback.");
                backupSchedule = InitializeDefaultSchedule();
            }

            foreach (var (taskName, metadata) in backupSchedule)
            {
                if (metadata.DisableUpdates)
                {
                    Log.Information("⏩ Skipping {TaskName}, updates disabled.", taskName);
                    continue;
                }

                var lastRunTime = DateTime.Parse(metadata.LastUpdate);
                var interval = GetIntervalFromSchedule(metadata.Schedule);

                if (lastRunTime + interval <= now)
                {
                    Log.Information("🔹 Running {TaskName}...", taskName);
                    await RunTask(taskName, databaseBackup, monitoringBackup, grafanaBackup, logsBackup);
                    metadata.LastUpdate = now.ToString("o");
                }
                else
                {
                    Log.Information("⏩ Skipping {TaskName}, last run at {LastRunTime}.", taskName, lastRunTime);
                }
            }

            // ✅ Update last run times
            await googleDriveService.SaveBackupStatusToGoogleDrive(backupSchedule);
            Log.Information("✅ Backup status updated.");
        }
        catch (Exception ex)
        {
            Log.Error("❌ Error executing tasks: {Message}", ex.Message);
        }

        // ✅ Keep the container running (for debugging)
        Log.Information("🟢 Webistecs Util running. Sleeping indefinitely...");
        await Task.Delay(-1);
    }

    private static async Task RunTask(
        string taskName, 
        DatabaseBackup dbBackup, 
        MonitoringToolsBackup monitoringBackup, 
        GrafanaExportService grafanaBackup, 
        LogsBackup logsBackup)
    {
        switch (taskName)
        {
            case "DatabaseBackup":
                await dbBackup.RunBackupProcess();
                break;
            case "MonitoringBackup":
                await monitoringBackup.StartAsync(default);
                break;
            case "GrafanaBackup":
                await grafanaBackup.StartAsync(default);
                break;
            case "LogBackup":
                await logsBackup.StartAsync(default);
                break;
            default:
                Log.Warning("⚠️ Unknown task: {TaskName}", taskName);
                break;
        }
    }

private static Dictionary<string, TaskMetadata> InitializeDefaultSchedule()
{
    return new Dictionary<string, TaskMetadata>
    {
        { "DatabaseBackup", new TaskMetadata { LastUpdate = DateTime.UtcNow.ToString("o"), Schedule = "DAILY", OverrideAppHealthStatus = false, DisableUpdates = false } },
        { "MonitoringBackup", new TaskMetadata { LastUpdate = DateTime.UtcNow.ToString("o"), Schedule = "HOURLY", OverrideAppHealthStatus = false, DisableUpdates = false } },
        { "LogBackup", new TaskMetadata { LastUpdate = DateTime.UtcNow.ToString("o"), Schedule = "HOURLY", OverrideAppHealthStatus = false, DisableUpdates = false } },
        { "GrafanaBackup", new TaskMetadata { LastUpdate = DateTime.UtcNow.ToString("o"), Schedule = "HOURLY", OverrideAppHealthStatus = false, DisableUpdates = false } }
    };
}


    private static TimeSpan GetIntervalFromSchedule(string schedule)
    {
        return schedule switch
        {
            "DAILY" => TimeSpan.FromDays(1),
            "HOURLY" => TimeSpan.FromHours(1),
            "WEEKLY" => TimeSpan.FromDays(7),
            _ => throw new ArgumentException($"❌ Unsupported schedule: {schedule}")
        };
    }
}