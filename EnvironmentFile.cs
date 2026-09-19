using System.Text;

internal static class EnvironmentFile
{
    private const string ExplicitPathVariable = "BERG_SYNC_ENV_FILE";

    public static string? Load()
    {
        var path = ResolvePath();
        if (path is null)
            return null;

        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();

            var separator = line.IndexOf('=');
            if (separator <= 0)
                throw new FormatException($"Некорректная строка в {path}: ожидается NAME=VALUE.");

            var name = line[..separator].Trim();
            if (!IsValidName(name))
                throw new FormatException($"Некорректное имя переменной '{name}' в {path}.");

            // Явно заданное окружение имеет приоритет над локальным .env.
            if (Environment.GetEnvironmentVariable(name) is not null)
                continue;

            var value = ParseValue(line[(separator + 1)..].Trim(), path);
            Environment.SetEnvironmentVariable(name, value);
        }

        return path;
    }

    private static string? ResolvePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(ExplicitPathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException(
                    $"Файл окружения из {ExplicitPathVariable} не найден.", fullPath);
            return fullPath;
        }

        var currentPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (File.Exists(currentPath))
            return Path.GetFullPath(currentPath);

        var applicationPath = Path.Combine(AppContext.BaseDirectory, ".env");
        if (File.Exists(applicationPath))
            return Path.GetFullPath(applicationPath);

        // При `dotnet run --project ...` исполняемый файл находится в bin/...,
        // поэтому ищем корень проекта среди его родителей.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "berg-sync.csproj")))
            {
                var projectPath = Path.Combine(directory.FullName, ".env");
                return File.Exists(projectPath) ? projectPath : null;
            }
            directory = directory.Parent;
        }

        return null;
    }

    private static string ParseValue(string value, string path)
    {
        if (value.Length == 0)
            return string.Empty;

        if (value[0] is '\'' or '"')
        {
            var quote = value[0];
            if (value.Length < 2 || value[^1] != quote)
                throw new FormatException($"Незакрытая кавычка в {path}.");

            var unquoted = value[1..^1];
            return quote == '"'
                ? unquoted.Replace("\\n", "\n", StringComparison.Ordinal)
                          .Replace("\\r", "\r", StringComparison.Ordinal)
                          .Replace("\\\"", "\"", StringComparison.Ordinal)
                          .Replace("\\\\", "\\", StringComparison.Ordinal)
                : unquoted;
        }

        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? value[..comment].TrimEnd() : value;
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
            return false;

        return name.All(character => char.IsLetterOrDigit(character) || character == '_');
    }
}
