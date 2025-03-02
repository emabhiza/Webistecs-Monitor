using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Webistecs_Monitor.BackupTools;
using Webistecs_Monitor.Configuration;
using Webistecs_Monitor.Domain;
using Webistecs_Monitor.Google;
using Webistecs_Monitor.Grafana;

namespace Webistecs_Monitor
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Setup Serilog for console logging (useful for kubectl logs)
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .MinimumLevel.Debug()
                .CreateLogger();

            Log.Information("🚀 Webistecs Monitor Starting...");

            // Load configuration before anything else
            var config = ApplicationConfiguration.LoadConfiguration();
            Log.Information("✅ Loaded Configuration: GOOGLE_CREDENTIALS={CredentialsPath}, DB_PATH={DbPath}, BACKUP_PATH={BackupPath}",
                config.CredentialsPath, config.DbPath, config.BackupPath);

            // Build the host and register dependencies
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton(config);
                    services.AddSingleton<GoogleDriveService>();
                    services.AddSingleton<DatabaseBackup>();
                    services.AddSingleton<MonitoringToolsBackup>();
                    services.AddSingleton<GrafanaExportService>();
                    services.AddSingleton<LogsBackup>();
                })
                .UseSerilog()
                .Build();

            using (var scope = host.Services.CreateScope())
            {
                var serviceProvider = scope.ServiceProvider;

                var googleDriveService = serviceProvider.GetRequiredService<GoogleDriveService>();
                var databaseBackup = serviceProvider.GetRequiredService<DatabaseBackup>();
                var monitoringBackup = serviceProvider.GetRequiredService<MonitoringToolsBackup>();
                var grafanaBackup = serviceProvider.GetRequiredService<GrafanaExportService>();
                var logsBackup = serviceProvider.GetRequiredService<LogsBackup>();

                try
                {
                    Log.Information("📡 Fetching job schedule from Google Drive...");
                    // Try to read schedule from Google Drive; if empty, use defaults.
                    var backupSchedule = await googleDriveService.ReadBackupStatusFromGoogleDrive();
                    if (backupSchedule == null || backupSchedule.Count == 0)
                    {
                        Log.Warning("⚠️ No backup schedule found! Using default schedule.");
                        backupSchedule = InitializeDefaultSchedule();
                    }

                    var now = DateTime.UtcNow;

                    foreach (var (taskName, metadata) in backupSchedule)
                    {
                        if (metadata.DisableUpdates)
                        {
                            Log.Information("⏩ Skipping {TaskName}, updates disabled.", taskName);
                            continue;
                        }

                        // If OverrideAppHealthStatus is false, perform a health check before running the task.
                        if (!metadata.OverrideAppHealthStatus)
                        {
                            bool healthy = await IsAppHealthyAsync();
                            if (!healthy)
                            {
                                Log.Warning("⚠️ Health check failed. Skipping {TaskName} update.", taskName);
                                continue;
                            }
                        }

                        var lastRunTime = DateTime.Parse(metadata.LastUpdate);
                        var interval = GetIntervalFromSchedule(metadata.Schedule);

                        if (lastRunTime.Add(interval) <= now)
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

                    await googleDriveService.SaveBackupStatusToGoogleDrive(backupSchedule);
                    Log.Information("✅ Backup status updated.");
                }
                catch (Exception ex)
                {
                    Log.Error("❌ Error executing tasks: {Message}", ex.Message);
                }
            }

            Log.Information("✅ Tasks completed. Exiting application.");
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
                    await dbBackup.RunBackupProcess(default);
                    break;
                case "MonitoringBackup":
                    await monitoringBackup.RunBackupProcess(default);
                    break;
                case "GrafanaBackup":
                    await grafanaBackup.RunBackupProcess(default);
                    break;
                case "LogBackup":
                    await logsBackup.RunBackupProcess(default);
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
                { "GrafanaBackup", new TaskMetadata { LastUpdate = DateTime.UtcNow.ToString("o"), Schedule = "HOURLY", OverrideAppHealthStatus = true, DisableUpdates = false } }
            };
        }

        private static TimeSpan GetIntervalFromSchedule(string schedule)
        {
            return schedule.ToUpperInvariant() switch
            {
                "DAILY" => TimeSpan.FromDays(1),
                "HOURLY" => TimeSpan.FromHours(1),
                "WEEKLY" => TimeSpan.FromDays(7),
                _ => throw new ArgumentException($"❌ Unsupported schedule: {schedule}")
            };
        }

        /// <summary>
        /// Checks the health of the application by calling the HEALTH_CHECK_URL.
        /// If no URL is provided, it assumes the app is healthy.
        /// </summary>
        /// <returns>true if healthy; false otherwise.</returns>
        private static async Task<bool> IsAppHealthyAsync()
        {
            var healthUrl = Environment.GetEnvironmentVariable("HEALTH_CHECK_URL");
            if (string.IsNullOrEmpty(healthUrl))
            {
                Log.Debug("No HEALTH_CHECK_URL specified, assuming healthy.");
                return true;
            }

            try
            {
                using var client = new HttpClient();
                var response = await client.GetAsync(healthUrl);
                if (response.IsSuccessStatusCode)
                {
                    Log.Debug("Health check succeeded.");
                    return true;
                }
                else
                {
                    Log.Warning("Health check returned status {StatusCode}.", response.StatusCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Health check failed: {Message}", ex.Message);
                return false;
            }
        }
    }
}
