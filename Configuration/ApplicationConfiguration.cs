using System.Collections;
using Newtonsoft.Json;
using Webistecs_Monitor.Logging;
using ILogger = Serilog.ILogger;

namespace Webistecs_Monitor.Configuration
{
    public class ApplicationConfiguration
    {
        public string CredentialsPath { get; set; }
        public string BackupPath { get; set; }
        public string DbPath { get; set; }
        public string LocalBackUpPath { get; set; }
        public string HealthCheckUrl { get; set; }
        private static readonly ILogger Logger = LoggerFactory.Create();

  /// <summary>
        /// Loads the application configuration, prioritizing environment variables in production.
        /// </summary>
        public static ApplicationConfiguration LoadConfiguration()
        {
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
            var config = new ApplicationConfiguration();

            // ✅ Log all available environment variables for debugging
            Logger.Information("🔍 [DEBUG] Dumping ALL environment variables...");
            foreach (DictionaryEntry env in Environment.GetEnvironmentVariables())
            {
                Logger.Information("🔹 {Key} = {Value}", env.Key, env.Value);
            }

            if (environment == "Development")
            {
                var configPath = "appsettings.json";
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    config = JsonConvert.DeserializeObject<ApplicationConfiguration>(json) 
                             ?? throw new InvalidDataException("Invalid config file.");
                }
            }
            else
            {
                // ✅ Explicitly log each variable before reading it
                Logger.Information("🔍 [DEBUG] Reading required environment variables...");

                config.CredentialsPath = GetEnvOrLog("GOOGLE_APPLICATION_CREDENTIALS");
                config.BackupPath = GetEnvOrLog("BACKUP_PATH");
                config.DbPath = GetEnvOrLog("DB_PATH");
                config.LocalBackUpPath = GetEnvOrLog("LOCAL_BACKUP_PATH");
                config.HealthCheckUrl = GetEnvOrLog("HEALTH_CHECK_URL");
            }

            Logger.Information("✅ [DEBUG] Final Config Loaded: CredentialsPath={CredentialsPath}, BackupPath={BackupPath}, DbPath={DbPath}", 
                config.CredentialsPath, config.BackupPath, config.DbPath);

            return config;
        }

        /// <summary>
        /// Fetches an environment variable and logs if it is missing.
        /// </summary>
        private static string GetEnvOrLog(string envVariable)
        {
            var value = Environment.GetEnvironmentVariable(envVariable);

            if (string.IsNullOrEmpty(value))
            {
                Logger.Error("❌ MISSING ENV VARIABLE: {EnvVariable}. Ensure it's set in the Kubernetes deployment!", envVariable);
            }
            else
            {
                Logger.Information("✅ {EnvVariable} = {Value}", envVariable, value);
            }

            return value ?? string.Empty; // Prevents crashes by returning an empty string if null
        }
    }
}