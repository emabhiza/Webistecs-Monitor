namespace Webistecs_Monitor;


public static class WebistecsConstants
{
    public const string JsonApplicationType = "application/json";
    public const string ApplicationName = "webistecs";
    public const string IdField = "id";
    public const string DateFormat = "dd-MM-yyyy";
    public const string BackupStatusFileName = "scheduled_tasks_status.json";
    public static readonly string[] MonitoringTools = ["grafana", "prometheus"];
    
    public const string LogsBackupFolderId = "1TT5iSq9XuaD3u7w-4_25pWb6bSmZ6Vtg";      // ✅ Logs Folder
    public const string GrafanaBackupFolderId = "13RYYDMi6oSyvp-jCphvPJ3YteE7Njwvt";  // ✅ Grafana Folder
    public const string DatabaseBackupFolderId = "1e76cYkhEG3iwcEyPvbWgV8POcOibzkgN"; // ✅ Database Folder
}
