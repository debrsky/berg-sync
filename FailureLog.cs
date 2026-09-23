using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

internal sealed class FailureLog : IDisposable
{
    private readonly TextWriter originalOut = Console.Out;
    private readonly TextWriter originalError = Console.Error;
    private readonly StringBuilder output = new();
    private readonly object gate = new();
    private readonly List<string> connectionStrings = new();

    public FailureLog()
    {
        Console.SetOut(new CaptureWriter(originalOut, this));
        Console.SetError(new CaptureWriter(originalError, this));
        Record($"Запуск: {DateTimeOffset.Now:O}");
    }

    public void Record(string message)
    {
        lock (gate) output.AppendLine(message);
    }

    public void AddConnectionString(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            connectionStrings.Add(connectionString);
    }

    public void Save(Exception? error = null)
    {
        try
        {
            string content;
            lock (gate) content = output.ToString();
            if (error is not null) content += Environment.NewLine + error + Environment.NewLine;
            content = Redact(content);

            var directory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                $"berg-sync-error-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.log");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                writer.Write(content);
            originalError.WriteLine($"Лог ошибки сохранён: {path}");
        }
        catch (Exception exception)
        {
            originalError.WriteLine($"Не удалось сохранить лог ошибки: {exception.GetType().Name}");
        }
    }

    private string Redact(string content)
    {
        var values = connectionStrings.Concat(
            new[] { "PG_LOCAL_CONNECTION_STRING", "PG_HOSTING_CONNECTION_STRING" }
                .Select(Environment.GetEnvironmentVariable));
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value)) continue;
            content = content.Replace(value, "[строка подключения скрыта]", StringComparison.OrdinalIgnoreCase);
            try
            {
                var password = new NpgsqlConnectionStringBuilder(value).Password;
                if (!string.IsNullOrEmpty(password))
                    content = content.Replace(password, "[пароль скрыт]", StringComparison.Ordinal);
            }
            catch (ArgumentException) { /* Дополнительно скрываем Password=... ниже. */ }
        }
        return Regex.Replace(content, @"(?i)(\b(?:password|pwd)\s*=\s*)(?:'[^']*'|""[^""]*""|[^;\s]+)",
            "$1[пароль скрыт]");
    }

    private void Append(string? value)
    {
        lock (gate) output.Append(value);
    }

    public void Dispose()
    {
        Console.SetOut(originalOut);
        Console.SetError(originalError);
    }

    private sealed class CaptureWriter(TextWriter destination, FailureLog owner) : TextWriter
    {
        public override Encoding Encoding => destination.Encoding;
        public override void Write(char value)
        {
            destination.Write(value);
            owner.Append(value.ToString());
        }
        public override void Write(string? value)
        {
            destination.Write(value);
            owner.Append(value);
        }
        public override void Write(char[] buffer, int index, int count)
        {
            destination.Write(buffer, index, count);
            owner.Append(new string(buffer, index, count));
        }
        public override void Write(ReadOnlySpan<char> buffer)
        {
            destination.Write(buffer);
            owner.Append(buffer.ToString());
        }
        public override void Flush() => destination.Flush();
    }
}
