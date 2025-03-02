namespace Webistecs_Monitor.Domain;

public class ScheduledTask
{
    public string Name { get; set; } = "";
    public TimeSpan Interval { get; set; }
    public Func<Task> ExecuteAsync { get; set; } = async () => { };
}