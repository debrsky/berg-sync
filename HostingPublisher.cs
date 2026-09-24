using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;

internal static class HostingPublisher
{
    private static readonly string[] IdTables =
        ["Bosses", "Cargos", "Cars", "Customers", "Tariffs", "Applications", "XInvoices", "XInvoicePays", "XInvoiceDatas"];
    private static readonly string[] CountOnlyRelations =
        ["bergauto.\"xSaldo\"", "bergapp.operations", "bergapp.payers", "bergapp.sellers", "bergapp.invoices", "bergapp.counterparties", "bergapp.balances", "bergapp.debt_invoices"];

    public static async Task RunAsync(
        string localConnectionString,
        string localDatabase,
        bool confirmRestore,
        string? configuredWorkDirectory,
        int transferRetries,
        int zstdLevel)
    {
        if (!confirmRestore)
            throw new ArgumentException(
                "Публикация заменяет bergauto, bergapp и berg_sync на хостинге. " +
                "Добавьте --confirm-hosting-restore.");

        var totalTimer = Stopwatch.StartNew();
        var phaseTimer = Stopwatch.StartNew();
        var hostingConnectionString = RequireEnvironment("PG_HOSTING_CONNECTION_STRING");
        var sshTarget = RequireEnvironment("HOSTING_SSH_TARGET");
        var remoteDirectory = NormalizeRemotePath(RequireEnvironment("HOSTING_TRANSFER_DIR"));
        var remoteScript = NormalizeRemotePath(RequireEnvironment("HOSTING_RESTORE_SCRIPT"));

        var localBuilder = new NpgsqlConnectionStringBuilder(localConnectionString)
        {
            Database = localDatabase
        };
        var hostingBuilder = new NpgsqlConnectionStringBuilder(hostingConnectionString);

        Console.WriteLine("\nПОЛНАЯ ПУБЛИКАЦИЯ НА ХОСТИНГ");
        Console.WriteLine($"  локальная база: {localBuilder.Host}/{localBuilder.Database}");
        Console.WriteLine($"  хостинг: {hostingBuilder.Host}:{hostingBuilder.Port}/{hostingBuilder.Database}");
        Console.WriteLine($"  SFTP/SSH: {sshTarget}");
        Console.WriteLine($"  каталог: {remoteDirectory}");

        await using var local = new NpgsqlConnection(localBuilder.ConnectionString);
        await local.OpenAsync();
        var state = await ReadStateAsync(local);
        LogProfile("full", "Подключение и чтение состояния локальной базы", phaseTimer,
            $"baseline: {state.BaselineId}; delta_seq: {state.DeltaSeq}");
        if (state.DeltaSeq != 0)
            throw new InvalidOperationException(
                $"Полная публикация требует delta_seq=0, получено {state.DeltaSeq}. " +
                "Сначала выполните полную миграцию.");

        phaseTimer.Restart();
        var localSnapshot = await ReadSnapshotAsync(local, state, "локально");
        LogProfile(state.BaselineId, "Контрольный снимок локальной базы — всего", phaseTimer,
            $"объектов: {localSnapshot.Count:N0}");

        phaseTimer.Restart();
        await ValidateHostingConnectionAsync(hostingBuilder.ConnectionString);
        LogProfile(state.BaselineId, "Проверка подключения к хостингу", phaseTimer);

        var baseWorkDirectory = string.IsNullOrWhiteSpace(configuredWorkDirectory)
            ? Path.Combine(Path.GetTempPath(), "berg-sync-transfer")
            : Path.GetFullPath(configuredWorkDirectory);
        var workDirectory = Path.Combine(baseWorkDirectory, state.BaselineId);
        var dumpDirectory = Path.Combine(workDirectory, "bergdb-dump");
        var manifestPath = Path.Combine(workDirectory, "manifest.json");
        var tarPath = Path.Combine(workDirectory, "berg-transfer.tar");
        var archiveName = $"berg-transfer-{state.BaselineId}.tar.zst";
        var archivePath = Path.Combine(workDirectory, archiveName);
        var checksumPath = archivePath + ".sha256";

        phaseTimer.Restart();
        if (Directory.Exists(workDirectory))
            Directory.Delete(workDirectory, recursive: true);
        Directory.CreateDirectory(workDirectory);
        LogProfile(state.BaselineId, "Подготовка рабочего каталога", phaseTimer);

        var success = false;
        try
        {
            Console.WriteLine("Создание directory-format dump...");
            phaseTimer.Restart();
            var pgEnvironment = BuildPostgresEnvironment(localBuilder);
            await RunProcessAsync("pg_dump.exe",
                ["--format=directory", "--compress=none", "--jobs=4", "--no-owner", "--no-acl",
                 "--schema=bergauto", "--schema=bergapp", "--schema=berg_sync",
                 $"--file={dumpDirectory}", localBuilder.Database!],
                pgEnvironment);
            LogProfile(state.BaselineId, "Создание directory-format dump", phaseTimer);

            phaseTimer.Restart();
            var manifest = new FullTransferManifest
            {
                BaselineId = state.BaselineId,
                DeltaSeq = state.DeltaSeq,
                CreatedAt = DateTimeOffset.Now,
                SourceDatabase = localBuilder.Database ?? string.Empty,
                Relations = localSnapshot
            };
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            LogProfile(state.BaselineId, "Формирование manifest", phaseTimer,
                $"объектов: {manifest.Relations.Count:N0}");

            Console.WriteLine("Создание TAR...");
            phaseTimer.Restart();
            await RunProcessAsync("tar.exe",
                ["-cf", tarPath, "-C", workDirectory, "bergdb-dump", "manifest.json"]);
            var tarSize = new FileInfo(tarPath).Length;
            LogProfile(state.BaselineId, "Создание TAR", phaseTimer,
                $"размер: {tarSize:N0} байт");

            Console.WriteLine($"Сжатие Zstandard, уровень {zstdLevel}...");
            phaseTimer.Restart();
            var zstdArguments = new List<string>();
            if (zstdLevel > 19) zstdArguments.Add("--ultra");
            zstdArguments.Add($"-{zstdLevel}");
            zstdArguments.Add("-T0");
            zstdArguments.Add("--force");
            zstdArguments.Add(tarPath);
            zstdArguments.Add("-o");
            zstdArguments.Add(archivePath);
            await RunProcessAsync("zstd.exe", zstdArguments);
            var archiveSize = new FileInfo(archivePath).Length;
            File.Delete(tarPath);
            LogProfile(state.BaselineId, "Сжатие Zstandard", phaseTimer,
                $"{tarSize:N0} → {archiveSize:N0} байт; коэффициент: {(double)archiveSize / tarSize:P1}");

            phaseTimer.Restart();
            await RunProcessAsync("zstd.exe", ["--test", archivePath]);
            var checksum = await CalculateSha256Async(archivePath);
            await File.WriteAllTextAsync(
                checksumPath, $"{checksum}  {archiveName}\n", new UTF8Encoding(false));
            Console.WriteLine($"Архив: {archiveSize:N0} байт");
            LogProfile(state.BaselineId, "Проверка архива и расчёт SHA-256", phaseTimer);

            phaseTimer.Restart();
            await EnsureRemoteDirectoryAsync(sshTarget, remoteDirectory);
            LogProfile(state.BaselineId, "Подготовка удалённого каталога", phaseTimer);

            phaseTimer.Restart();
            await UploadWithResumeAsync(
                sshTarget, archivePath, $"{remoteDirectory}/{archiveName}.uploading", transferRetries);
            var uploadSeconds = Math.Max(phaseTimer.Elapsed.TotalSeconds, 0.001);
            LogProfile(state.BaselineId, "Загрузка архива по SFTP", phaseTimer,
                $"размер: {archiveSize:N0} байт; скорость: {archiveSize / uploadSeconds / 1024 / 1024:N2} МиБ/с");

            phaseTimer.Restart();
            await ActivateArchiveAsync(
                sshTarget, remoteDirectory, remoteScript, archiveName, checksum);
            LogProfile(state.BaselineId, "Удалённая проверка и восстановление", phaseTimer);

            Console.WriteLine("Проверка базы на хостинге...");
            phaseTimer.Restart();
            await using var hosting = new NpgsqlConnection(hostingBuilder.ConnectionString);
            await hosting.OpenAsync();
            var remoteState = await ReadStateAsync(hosting);
            LogProfile(state.BaselineId, "Подключение и чтение состояния хостинга", phaseTimer,
                $"baseline: {remoteState.BaselineId}; delta_seq: {remoteState.DeltaSeq}");

            phaseTimer.Restart();
            var remoteSnapshot = await ReadSnapshotAsync(hosting, remoteState, "хостинг");
            CompareSnapshots(localSnapshot, remoteSnapshot, state, remoteState);
            LogProfile(state.BaselineId, "Контрольный снимок и сравнение хостинга — всего", phaseTimer,
                $"объектов: {remoteSnapshot.Count:N0}");

            phaseTimer.Restart();
            await ExecuteAsync(local, "CALL berg_persistent.archive_invoices()");
            LogProfile(state.BaselineId, "Архивация локальных счетов в berg_persistent", phaseTimer);

            success = true;
            Console.WriteLine("Полная публикация и проверка завершены успешно.");
            Console.WriteLine($"Baseline: {state.BaselineId}");
        }
        finally
        {
            phaseTimer.Restart();
            if (success)
                Directory.Delete(workDirectory, recursive: true);
            else
                Console.Error.WriteLine($"Файлы неуспешной публикации сохранены: {workDirectory}");
            LogProfile(state.BaselineId, "Очистка рабочего каталога", phaseTimer,
                success ? "успешная публикация" : "файлы сохранены для диагностики");
            LogProfile(state.BaselineId, "Полная публикация — всего", totalTimer,
                success ? "успешно" : "ошибка");
        }
    }

    private static async Task EnsureRemoteDirectoryAsync(string target, string directory)
    {
        await RunProcessAsync("ssh.exe",
            SshArguments(target, $"mkdir -p -- {ShellQuote(directory)}"));
    }

    private static async Task UploadWithResumeAsync(
        string target, string localPath, string remotePath, int attempts)
    {
        var batchPath = Path.Combine(Path.GetTempPath(), $"berg-sftp-{Guid.NewGuid():N}.txt");
        var normalizedLocal = localPath.Replace('\\', '/');

        try
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var exists = await RunProcessAsync(
                    "ssh.exe",
                    SshArguments(target, $"test -f {ShellQuote(remotePath)}"),
                    throwOnError: false) == 0;
                var operation = exists ? "reput" : "put";
                await File.WriteAllTextAsync(
                    batchPath,
                    $"{operation} \"{normalizedLocal.Replace("\"", "\\\"", StringComparison.Ordinal)}\" " +
                    $"\"{remotePath.Replace("\"", "\\\"", StringComparison.Ordinal)}\"\n",
                    new UTF8Encoding(false));

                Console.WriteLine($"SFTP: попытка {attempt} из {attempts} ({operation})...");
                var exitCode = await RunProcessAsync(
                    "sftp.exe", SshArguments("-b", batchPath, target), throwOnError: false);
                if (exitCode == 0)
                    return;
                if (attempt < attempts)
                    await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            File.Delete(batchPath);
        }

        throw new InvalidOperationException($"SFTP-загрузка не удалась после {attempts} попыток.");
    }

    private static async Task ActivateArchiveAsync(
        string target,
        string remoteDirectory,
        string remoteScript,
        string archiveName,
        string checksum)
    {
        var uploadingName = archiveName + ".uploading";
        var checksumName = archiveName + ".sha256";
        var command =
            $"set -Eeuo pipefail; cd -- {ShellQuote(remoteDirectory)}; " +
            $"actual=$(sha256sum -- {ShellQuote(uploadingName)} | awk '{{print $1}}'); " +
            $"test \"$actual\" = {ShellQuote(checksum)}; " +
            $"printf '%s  %s\\n' {ShellQuote(checksum)} {ShellQuote(archiveName)} > {ShellQuote(checksumName + ".tmp")}; " +
            $"mv -f -- {ShellQuote(uploadingName)} {ShellQuote(archiveName)}; " +
            $"mv -f -- {ShellQuote(checksumName + ".tmp")} {ShellQuote(checksumName)}; " +
            $"{ShellQuote(remoteScript)} {ShellQuote(archiveName)}";

        Console.WriteLine("Проверка SHA-256 и запуск удалённого восстановления...");
        await RunProcessAsync("ssh.exe",
            SshArguments(target, command));
    }

    private static string[] SshArguments(params string[] arguments)
    {
        var configPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ssh", "config"));
        if (!File.Exists(configPath))
            throw new FileNotFoundException("Не найден общий SSH-конфиг для публикации.", configPath);

        var variable = Environment.UserName.Equals("zhn", StringComparison.OrdinalIgnoreCase)
            ? "HOSTING_SSH_KEY_ZHN"
            : "HOSTING_SSH_KEY";
        var configuredKey = RequireEnvironment(variable);
        var keyPath = Path.GetFullPath(configuredKey);
        if (!File.Exists(keyPath))
            throw new FileNotFoundException($"SSH-ключ из {variable} не найден.", keyPath);

        return ["-F", configPath, "-i", keyPath, "-o", "IdentitiesOnly=yes",
            "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", .. arguments];
    }

    private static async Task ValidateHostingConnectionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await connection.OpenAsync(cancellation.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_is_in_recovery(), current_database(), current_user";
        await using var reader = await command.ExecuteReaderAsync(cancellation.Token);
        await reader.ReadAsync(cancellation.Token);
        if (reader.GetBoolean(0))
            throw new InvalidOperationException("PostgreSQL на хостинге находится в recovery mode.");
        Console.WriteLine($"Проверено подключение к хостингу: {reader.GetString(1)} / {reader.GetString(2)}");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SyncStateSnapshot> ReadStateAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT baseline_id, delta_seq
            FROM berg_sync.state
            WHERE id=1
            """;
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("berg_sync.state не содержит строку id=1.");
            return new SyncStateSnapshot(reader.GetString(0), reader.GetInt64(1));
        }
        catch (PostgresException exception) when (exception.SqlState is "42P01" or "3F000")
        {
            throw new InvalidOperationException(
                "Схема состояния berg_sync отсутствует. Сначала выполните полную миграцию.", exception);
        }
    }

    private static async Task<Dictionary<string, RelationSnapshot>> ReadSnapshotAsync(
        NpgsqlConnection connection, SyncStateSnapshot state, string target)
    {
        var result = new Dictionary<string, RelationSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in IdTables)
        {
            var timer = Stopwatch.StartNew();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*), COALESCE(MAX(\"ID\"),0) FROM bergauto.{Quote(table)}";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            var snapshot = new RelationSnapshot(reader.GetInt64(0), reader.GetInt64(1));
            result[$"bergauto.{table}"] = snapshot;
            LogProfile(state.BaselineId, $"Снимок {target}: bergauto.{table}", timer,
                $"строк: {snapshot.Count:N0}; max ID: {snapshot.MaxId:N0}");
        }
        foreach (var relation in CountOnlyRelations)
        {
            var timer = Stopwatch.StartNew();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {relation}";
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            var key = relation.Replace("\"", string.Empty, StringComparison.Ordinal);
            result[key] = new RelationSnapshot(count, null);
            LogProfile(state.BaselineId, $"Снимок {target}: {key}", timer,
                $"строк: {count:N0}");
        }
        return result;
    }

    private static void CompareSnapshots(
        IReadOnlyDictionary<string, RelationSnapshot> local,
        IReadOnlyDictionary<string, RelationSnapshot> remote,
        SyncStateSnapshot localState,
        SyncStateSnapshot remoteState)
    {
        if (localState != remoteState)
            throw new InvalidOperationException(
                $"Состояние хостинга не совпадает: local={localState}, remote={remoteState}.");

        var differences = local.Keys.Where(key =>
                !remote.TryGetValue(key, out var value) || value != local[key])
            .Select(key => $"{key}: local={local[key]}, remote={remote.GetValueOrDefault(key)}")
            .ToArray();
        if (differences.Length > 0)
            throw new InvalidOperationException(
                "Проверка хостинга не пройдена:\n  " + string.Join("\n  ", differences));
    }

    private static Dictionary<string, string?> BuildPostgresEnvironment(NpgsqlConnectionStringBuilder builder) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PGHOST"] = builder.Host,
            ["PGPORT"] = builder.Port.ToString(CultureInfo.InvariantCulture),
            ["PGUSER"] = builder.Username,
            ["PGPASSWORD"] = builder.Password,
            ["PGSSLMODE"] = builder.SslMode.ToString() switch
            {
                "Disable" => "disable",
                "Allow" => "allow",
                "Prefer" => "prefer",
                "Require" => "require",
                "VerifyCA" => "verify-ca",
                "VerifyFull" => "verify-full",
                _ => null
            }
        };

    private static async Task<int> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool throwOnError = true)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment)
                if (pair.Value is not null)
                    startInfo.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) Console.WriteLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) Console.Error.WriteLine(eventArgs.Data);
        };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Не удалось запустить {fileName}.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Не удалось запустить {fileName}: {exception.Message}", exception);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} завершился с кодом {process.ExitCode}.");
        return process.ExitCode;
    }

    private static async Task<string> CalculateSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void LogProfile(
        string operationId, string operation, Stopwatch stopwatch, string? details = null)
    {
        stopwatch.Stop();
        Console.WriteLine($"[PROFILE] [{operationId}] {operation}: {stopwatch.Elapsed.TotalMilliseconds:N0} мс" +
                          (string.IsNullOrWhiteSpace(details) ? string.Empty : $"; {details}"));
    }

    private static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Задайте {name} в .env или окружении.");
        return value;
    }

    private static string NormalizeRemotePath(string value)
    {
        if (value.ContainsAny(['\r', '\n', '\0']))
            throw new ArgumentException("Удалённый путь содержит недопустимые символы.");
        return value.TrimEnd('/');
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record SyncStateSnapshot(string BaselineId, long DeltaSeq);
    internal sealed record RelationSnapshot(long Count, long? MaxId);
    private sealed class FullTransferManifest
    {
        public string BaselineId { get; init; } = string.Empty;
        public long DeltaSeq { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string SourceDatabase { get; init; } = string.Empty;
        public Dictionary<string, RelationSnapshot> Relations { get; init; } = [];
    }
}
