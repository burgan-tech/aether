using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

namespace BBT.Aether.AspNetCore.Threads;

public static class ThreadPoolHelper
{
    public static void ConfigureThreadPool(string configFilePath = "runtimeconfig.json")
    {
        try
        {
            if (!File.Exists(configFilePath))
                return;
            var jsonNode = JsonNode.Parse(File.ReadAllText(configFilePath))?["runtimeOptions"]?["configProperties"]?["System.Threading.ThreadPool.MinThreads"];
            if (jsonNode == null || !int.TryParse(jsonNode.ToString(), out var result))
                return;
            ThreadPool.SetMinThreads(result, result);
        }
        catch
        {
            // ignored
        }
    }
}