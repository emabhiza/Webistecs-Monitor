using Newtonsoft.Json;

namespace Webistecs_Monitor.Domain;

public class TaskMetadata
{
    [JsonProperty("lastUpdate")] public string LastUpdate { get; set; }

    [JsonProperty("Schedule")] public string Schedule { get; set; }

    [JsonProperty("OverrideAppHealthStatus")]
    public bool OverrideAppHealthStatus { get; set; }

    [JsonProperty("disableUpdates")] public bool DisableUpdates { get; set; } // ✅ Added DisableUpdates flag

    public DateTime GetLastUpdateDateTime()
    {
        return DateTime.Parse(LastUpdate);
    }
}