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
        if (state.DeltaSeq != 0)
            throw new InvalidOperationException(
                $"Полная публикация требует delta_seq=0, получено {state.DeltaSeq}. " +
                "Сначала выполните полную миграцию.");

        var localSnapshot = await ReadSnapshotAsync(local, state);
        await ValidateHostingConnectionAsync(hostingBuilder.ConnectionString);

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

        if (Directory.Exists(workDirectory))
            Directory.Delete(workDirectory, recursive: true);
        Directory.CreateDirectory(workDirectory);

        var success = false;
        try
        {
            Console.WriteLine("Создание directory-format dump...");
            var pgEnvironment = BuildPostgresEnvironment(localBuilder);
            await RunProcessAsync("pg_dump.exe",
                ["--format=directory", "--compress=none", "--jobs=4", "--no-owner", "--no-acl",
                 "--schema=bergauto", "--schema=bergapp", "--schema=berg_sync",
                 $"--file={dumpDirectory}", localBuilder.Database!],
                pgEnvironment);

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

            Console.WriteLine("Создание TAR...");
            await RunProcessAsync("tar.exe",
                ["-cf", tarPath, "-C", workDirectory, "bergdb-dump", "manifest.json"]);

            Console.WriteLine($"Сжатие Zstandard, уровень {zstdLevel}...");
            var zstdArguments = new List<string>();
            if (zstdLevel > 19) zstdArguments.Add("--ultra");
            zstdArguments.Add($"-{zstdLevel}");
            zstdArguments.Add("-T0");
            zstdArguments.Add("--force");
            zstdArguments.Add(tarPath);
            zstdArguments.Add("-o");
            zstdArguments.Add(archivePath);
            await RunProcessAsync("zstd.exe", zstdArguments);
            File.Delete(tarPath);

            await RunProcessAsync("zstd.exe", ["--test", archivePath]);
            var checksum = await CalculateSha256Async(archivePath);
            await File.WriteAllTextAsync(
                checksumPath, $"{checksum}  {archiveName}\n", new UTF8Encoding(false));
            Console.WriteLine($"Архив: {new FileInfo(archivePath).Length:N0} байт");

            await EnsureRemoteDirectoryAsync(sshTarget, remoteDirectory);
            await UploadWithResumeAsync(
                sshTarget, archivePath, $"{remoteDirectory}/{archiveName}.uploading", transferRetries);
            await ActivateArchiveAsync(
                sshTarget, remoteDirectory, remoteScript, archiveName, checksum);

            Console.WriteLine("Проверка базы на хостинге...");
            await using var hosting = new NpgsqlConnection(hostingBuilder.ConnectionString);
            await hosting.OpenAsync();
            var remoteState = await ReadStateAsync(hosting);
            var remoteSnapshot = await ReadSnapshotAsync(hosting, remoteState);
            CompareSnapshots(localSnapshot, remoteSnapshot, state, remoteState);

            success = true;
            Console.WriteLine("Полная публикация и проверка завершены успешно.");
            Console.WriteLine($"Baseline: {state.BaselineId}");
        }
        finally
        {
            if (success)
                Directory.Delete(workDirectory, recursive: true);
            else
                Console.Error.WriteLine($"Файлы неуспешной публикации сохранены: {workDirectory}");
        }
    }

    private static async Task EnsureRemoteDirectoryAsync(string target, string directory)
    {
        await RunProcessAsync("ssh.exe",
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", target,
             $"mkdir -p -- {ShellQuote(directory)}"]);
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
                    ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", target,
                     $"test -f {ShellQuote(remotePath)}"],
                    throwOnError: false) == 0;
                var operation = exists ? "reput" : "put";
                await File.WriteAllTextAsync(
                    batchPath,
                    $"{operation} \"{normalizedLocal.Replace("\"", "\\\"", StringComparison.Ordinal)}\" " +
                    $"\"{remotePath.Replace("\"", "\\\"", StringComparison.Ordinal)}\"\n",
                    new UTF8Encoding(false));

                Console.WriteLine($"SFTP: попытка {attempt} из {attempts} ({operation})...");
                var exitCode = await RunProcessAsync(
                    "sftp.exe", ["-b", batchPath, target], throwOnError: false);
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
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", target, command]);
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
        NpgsqlConnection connection, SyncStateSnapshot state)
    {
        var result = new Dictionary<string, RelationSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in IdTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*), COALESCE(MAX(\"ID\"),0) FROM bergauto.{Quote(table)}";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            result[$"bergauto.{table}"] = new RelationSnapshot(reader.GetInt64(0), reader.GetInt64(1));
        }
        foreach (var relation in CountOnlyRelations)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {relation}";
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            result[relation.Replace("\"", string.Empty, StringComparison.Ordinal)] = new RelationSnapshot(count, null);
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
