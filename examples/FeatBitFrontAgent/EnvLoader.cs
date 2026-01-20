namespace Kode.Agent.WebApiAssistant;

public static class EnvLoader
{
    private static bool _loaded;

    public static void Load()
    {
        if (_loaded) return;

        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(directory))
        {
            var envPath = Path.Combine(directory, ".env");
            if (File.Exists(envPath))
            {
                DotNetEnv.Env.Load(envPath);
                _loaded = true;
                return;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        _loaded = true;
    }

    public static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    public static string Get(string key, string defaultValue) =>
        Environment.GetEnvironmentVariable(key) ?? defaultValue;
}

