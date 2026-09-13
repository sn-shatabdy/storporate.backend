namespace Storporate.Api.Configuration;

/// <summary>
/// Loads the repo-root ".env" file (gitignored, real local connection values) into process
/// environment variables before configuration is built, so <c>ConnectionStrings__WriteDb</c>,
/// <c>ArtifactStorage__*</c>, and <c>Llm__Bionic__*</c> can be bound via the standard ASP.NET
/// Core environment-variable configuration provider (double-underscore section separator)
/// without ever hardcoding secrets in appsettings.json. No-op if no ".env" file is found —
/// production deployments won't ship one and are expected to set real environment variables.
/// Existing environment variables always win, so a real deployment env is never overridden.
/// </summary>
internal static class DotEnvLoader
{
    public static void LoadIfPresent()
    {
        var envFilePath = FindDotEnvFile(Directory.GetCurrentDirectory());
        if (envFilePath is null)
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(envFilePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string? FindDotEnvFile(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
