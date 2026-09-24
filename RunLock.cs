internal static class RunLock
{
    // Shared by all invocations on this Windows machine, regardless of working directory or mode.
    // The file is intentionally kept: removing it on exit would allow a race with another process.
    public static FileStream Acquire()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "..", "state");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "run.lock");

        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Другой экземпляр berg-sync уже выполняется (файл блокировки: {path}). " +
                "Дождитесь его завершения и повторите запуск.", exception);
        }
    }
}
