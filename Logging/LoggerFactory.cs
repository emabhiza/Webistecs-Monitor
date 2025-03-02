using System.Diagnostics;
using Serilog;
using ILogger = Serilog.ILogger;


namespace Webistecs_Monitor.Logging;

public static class LoggerFactory
{
    public static ILogger Create()
    {
        var method = new StackTrace().GetFrame(1)?.GetMethod();
        var type = method?.ReflectedType;

        return type != null 
            ? Log.ForContext(type) 
            : Log.ForContext("Unknown Context", null);
    }
}