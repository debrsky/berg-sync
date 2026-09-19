using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

internal static class RemoteDeltaDelivery
{
    private static readonly string[] Tables =
        ["Bosses", "Cargos", "Cars", "Customers", "Tariffs", "Applications", "XInvoices", "XInvoicePays", "XInvoiceDatas"];

    public static string ResolvePackageRoot(string? configured)
    {
        var value = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Environment.GetEnvironmentVariable("DELTA_PACKAGE_DIR");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? "delta-packages" : value);
    }

    public static async Task<string> WritePackageAsync(
        string root,
        string patchId,
        string baselineId,
        long fromSeq,
        long toSeq,
        string sourceMdb,
        DateTime periodStart,
        double dateOffsetHours,
        IReadOnlyDictionary<string, IReadOnlyList<object?[]>> rows,
        IReadOnlyList<AffectedPair> affectedPairs,
        IReadOnlyDictionary<string, DeltaTableCount> counts,
        IReadOnlyDictionary<string, long> sourceMaxIds,
        MigrationSchema schema)
    {
        var package = new DeltaPackage
        {
            PatchId = patchId,
            BaselineId = baselineId,
            FromDeltaSeq = fromSeq,
            ToDeltaSeq = toSeq,
            SourceMdb = sourceMdb,
            PeriodStart = periodStart,
            CreatedAt = DateTimeOffset.Now,
            DateOffsetHours = dateOffsetHours,
            DeletePolicy = "ignore",
            Counts = new Dictionary<string, DeltaTableCount>(counts, StringComparer.OrdinalIgnoreCase),
            SourceMaxIds = new Dictionary<string, long>(sourceMaxIds, StringComparer.OrdinalIgnoreCase),
            AffectedPairs = affectedPairs.ToList()
        };

        foreach (var table in Tables)
        {
            var columns = schema.Tables[table];
            package.Tables[table] = rows.TryGetValue(table, out var tableRows)
                ? tableRows.Select(row => SerializeRow(row, columns)).ToList()
                : [];
        }

        var directory = Path.Combine(root, patchId);
        Directory.CreateDirectory(directory);
        var packagePath = Path.Combine(directory, "package.json");
        var temporaryPath = packagePath + ".tmp";
        var json = JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, packagePath, overwrite: true);

        var checksum = await CalculateSha256Async(packagePath);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "package.sha256"), checksum + "\n", new UTF8Encoding(false));
        return directory;
    }

    public static async Task DeliverPendingAsync(
        NpgsqlConnection local,
        string hostingConnectionString,
        string packageRoot,
        MigrationSchema schema,
        bool recalculate,
        bool refreshViews)
    {
        var pending = new List<string>();
        try
        {
            await using var command = local.CreateCommand();
            command.CommandText = """
                SELECT patch_id
                FROM berg_sync.patch_delivery
                WHERE target='hosting' AND status IN ('pending','failed')
                ORDER BY patch_id
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) pending.Add(reader.GetString(0));
        }
        catch (PostgresException exception) when (exception.SqlState is "42P01" or "3F000")
        {
            return;
        }

        foreach (var patchId in pending)
        {
            Console.WriteLine($"Повторная доставка pending-пакета: {patchId}");
            var package = await LoadPackageAsync(packageRoot, patchId);
            await DeliverAndRecordAsync(
                local, hostingConnectionString, package, schema, recalculate, refreshViews);
        }
    }

    public static async Task DeliverAndRecordAsync(
        NpgsqlConnection local,
        string hostingConnectionString,
        DeltaPackage package,
        MigrationSchema schema,
        bool recalculate,
        bool refreshViews)
    {
        try
        {
            await MarkDeliveryAttemptAsync(local, package.PatchId, "applying", null);
            await ApplyAsync(hostingConnectionString, package, schema, recalculate, refreshViews);
            await MarkDeliveryAttemptAsync(local, package.PatchId, "applied", null, applied: true);
        }
        catch (Exception exception)
        {
            await MarkDeliveryAttemptAsync(local, package.PatchId, "failed", exception.Message);
            throw;
        }
    }

    public static async Task<DeltaPackage> LoadPackageAsync(string root, string patchId)
    {
        var directory = Path.Combine(root, patchId);
        var packagePath = Path.Combine(directory, "package.json");
        var checksumPath = Path.Combine(directory, "package.sha256");
        if (!File.Exists(packagePath) || !File.Exists(checksumPath))
            throw new FileNotFoundException($"Не найден пакет дельты {patchId} в {directory}.");

        var expected = (await File.ReadAllTextAsync(checksumPath)).Trim().ToLowerInvariant();
        var actual = await CalculateSha256Async(packagePath);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"Не совпадает SHA-256 пакета {patchId}.");

        return JsonSerializer.Deserialize<DeltaPackage>(await File.ReadAllTextAsync(packagePath))
               ?? throw new InvalidOperationException($"Не удалось прочитать пакет {patchId}.");
    }

    private static async Task ApplyAsync(
        string connectionString,
        DeltaPackage package,
        MigrationSchema schema,
        bool recalculate,
        bool refreshViews)
    {
        await using var hosting = new NpgsqlConnection(connectionString);
        await hosting.OpenAsync();

        var current = await ReadRemoteStateAsync(hosting);
        if (current.BaselineId == package.BaselineId && current.DeltaSeq == package.ToDeltaSeq &&
            await PatchExistsAsync(hosting, package.PatchId))
        {
            Console.WriteLine($"Пакет {package.PatchId} уже применён на хостинге.");
            return;
        }
        if (current.BaselineId != package.BaselineId || current.DeltaSeq != package.FromDeltaSeq)
            throw new InvalidOperationException(
                $"Состояние хостинга не подходит для {package.PatchId}: " +
                $"ожидалось {package.BaselineId}/{package.FromDeltaSeq}, " +
                $"получено {current.BaselineId}/{current.DeltaSeq}.");

        await using var transaction = await hosting.BeginTransactionAsync();
        await ExecuteAsync(hosting, transaction, "SELECT pg_advisory_xact_lock(1896472001)");

        // Повторяем проверку после получения блокировки.
        current = await ReadRemoteStateAsync(hosting, transaction);
        if (current.BaselineId != package.BaselineId || current.DeltaSeq != package.FromDeltaSeq)
            throw new InvalidOperationException("Состояние хостинга изменилось во время ожидания блокировки.");

        foreach (var table in Tables)
        {
            await ExecuteAsync(hosting, transaction,
                $"CREATE TEMP TABLE {Stage(table)} (LIKE bergauto.{Quote(table)} INCLUDING DEFAULTS) ON COMMIT DROP");
            var rows = package.Tables.GetValueOrDefault(table) ?? [];
            await CopyRowsAsync(
                hosting, table, schema.Tables[table], rows, package.DateOffsetHours);
        }

        await ExecuteAsync(hosting, transaction, """
            CREATE TEMP TABLE delta_affected_pairs (
              id_payer integer NOT NULL,
              id_seller integer NOT NULL,
              PRIMARY KEY (id_payer,id_seller)
            ) ON COMMIT DROP
            """);
        await using (var importer = await hosting.BeginBinaryImportAsync(
                         "COPY delta_affected_pairs (id_payer,id_seller) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var pair in package.AffectedPairs.Distinct())
            {
                await importer.StartRowAsync();
                await importer.WriteAsync(pair.IdPayer, NpgsqlDbType.Integer);
                await importer.WriteAsync(pair.IdSeller, NpgsqlDbType.Integer);
            }
            await importer.CompleteAsync();
        }

        foreach (var table in Tables)
            await UpsertAsync(hosting, transaction, table, schema.Tables[table]);

        if (recalculate && package.AffectedPairs.Count > 0)
        {
            await ExecuteAsync(hosting, transaction, """
                CREATE INDEX IF NOT EXISTS operations_payer_seller_idx
                ON bergapp.operations (id_payer,id_seller)
                """);
            if (package.AffectedPairs.Count <= 500)
                await ExecuteAsync(hosting, transaction, """
                    DO $body$
                    DECLARE pair record;
                    BEGIN
                      FOR pair IN SELECT id_payer,id_seller FROM delta_affected_pairs LOOP
                        CALL bergapp.calculate_and_save_operations(pair.id_payer,pair.id_seller);
                      END LOOP;
                    END $body$
                    """);
            else
                await ExecuteAsync(hosting, transaction, "CALL bergapp.calculate_and_save_operations()");
        }

        var totalChanges = package.Counts.Values.Sum(value => value.Inserted + value.Updated);
        if (refreshViews && totalChanges > 0)
            foreach (var view in new[] { "payers", "sellers", "invoices", "counterparties", "balances", "debt_invoices" })
                await ExecuteAsync(hosting, transaction, $"REFRESH MATERIALIZED VIEW bergapp.{Quote(view)}");

        await using (var command = hosting.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO berg_sync.patches
                  (patch_id,baseline_id,delta_seq,source_mdb,period_start,counts,affected_pairs,created_at)
                VALUES ($1,$2,$3,$4,$5,$6::jsonb,$7,$8)
                """;
            command.Parameters.AddWithValue(package.PatchId);
            command.Parameters.AddWithValue(package.BaselineId);
            command.Parameters.AddWithValue(package.ToDeltaSeq);
            command.Parameters.AddWithValue(package.SourceMdb);
            command.Parameters.AddWithValue(package.PeriodStart);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(package.Counts));
            command.Parameters.AddWithValue(package.AffectedPairs.Count);
            command.Parameters.AddWithValue(package.CreatedAt.ToUniversalTime());
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = hosting.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE berg_sync.state
                SET delta_seq=$1,source_mdb=$2,source_max_ids=$3::jsonb,updated_at=current_timestamp
                WHERE id=1
                """;
            command.Parameters.AddWithValue(package.ToDeltaSeq);
            command.Parameters.AddWithValue(package.SourceMdb);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(package.SourceMaxIds));
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = hosting.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO berg_sync.patch_delivery
                  (patch_id,target,status,attempts,applied_at)
                VALUES ($1,'hosting','applied',1,current_timestamp)
                ON CONFLICT (patch_id,target) DO UPDATE SET
                  status='applied',attempts=berg_sync.patch_delivery.attempts+1,
                  last_error=NULL,applied_at=current_timestamp
                """;
            command.Parameters.AddWithValue(package.PatchId);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        var appliedState = await ReadRemoteStateAsync(hosting);
        if (appliedState.BaselineId != package.BaselineId ||
            appliedState.DeltaSeq != package.ToDeltaSeq ||
            !await PatchExistsAsync(hosting, package.PatchId))
            throw new InvalidOperationException(
                $"Проверка применённого пакета {package.PatchId} на хостинге не пройдена.");

        Console.WriteLine($"Пакет {package.PatchId} применён на хостинге.");
    }

    private static async Task CopyRowsAsync(
        NpgsqlConnection connection,
        string table,
        IReadOnlyList<MigrationColumn> columns,
        IReadOnlyList<SerializedDeltaRow> rows,
        double dateOffsetHours)
    {
        if (rows.Count == 0) return;
        var names = string.Join(",", columns.Select(column => Quote(column.Name)));
        await using var importer = await connection.BeginBinaryImportAsync(
            $"COPY {Stage(table)} ({names}) FROM STDIN (FORMAT BINARY)");
        foreach (var row in rows)
        {
            if (row.Values.Count != columns.Count)
                throw new InvalidOperationException($"Неверное число колонок в пакете для {table}.");
            await importer.StartRowAsync();
            for (var index = 0; index < columns.Count; index++)
            {
                var raw = row.Values[index];
                if (raw is null)
                {
                    await importer.WriteNullAsync();
                    continue;
                }
                var converted = ParseValue(raw, columns[index].Type, dateOffsetHours);
                await importer.WriteAsync(converted.Value, converted.Type);
            }
        }
        await importer.CompleteAsync();
    }

    private static async Task UpsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        IReadOnlyList<MigrationColumn> columns)
    {
        var names = string.Join(",", columns.Select(column => Quote(column.Name)));
        var assignments = string.Join(",", columns.Skip(1).Select(column =>
            $"{Quote(column.Name)}=excluded.{Quote(column.Name)}"));
        var business = columns.Skip(1).ToArray();
        var distinct = $"ROW({string.Join(",", business.Select(c => $"target.{Quote(c.Name)}"))}) IS DISTINCT FROM " +
                       $"ROW({string.Join(",", business.Select(c => $"excluded.{Quote(c.Name)}"))})";
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO bergauto.{Quote(table)} AS target ({names})
            SELECT {names} FROM {Stage(table)}
            ON CONFLICT ("ID") DO UPDATE SET {assignments}
            WHERE {distinct}
            """);
    }

    private static SerializedDeltaRow SerializeRow(object?[] values, IReadOnlyList<MigrationColumn> columns)
    {
        var serialized = new List<string?>(values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            serialized.Add(value switch
            {
                null => null,
                DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                bool boolean => boolean ? "true" : "false",
                byte[] bytes => Convert.ToBase64String(bytes),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            });
        }
        return new SerializedDeltaRow { Values = serialized };
    }

    private static (object Value, NpgsqlDbType Type) ParseValue(
        string value, string postgresType, double dateOffsetHours)
    {
        var type = postgresType.ToUpperInvariant();
        if (type.StartsWith("VARCHAR", StringComparison.Ordinal) || type == "TEXT") return (value, NpgsqlDbType.Text);
        if (type == "INTEGER") return (int.Parse(value, CultureInfo.InvariantCulture), NpgsqlDbType.Integer);
        if (type == "SMALLINT") return (short.Parse(value, CultureInfo.InvariantCulture), NpgsqlDbType.Smallint);
        if (type == "BIGINT") return (long.Parse(value, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint);
        if (type == "DOUBLE PRECISION") return (double.Parse(value, CultureInfo.InvariantCulture), NpgsqlDbType.Double);
        if (type.StartsWith("NUMERIC", StringComparison.Ordinal) || type.StartsWith("DECIMAL", StringComparison.Ordinal))
            return (decimal.Parse(value, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric);
        if (type == "BOOLEAN") return (bool.Parse(value), NpgsqlDbType.Boolean);
        if (type.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            var date = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .AddHours(-dateOffsetHours);
            return (DateTime.SpecifyKind(date, DateTimeKind.Unspecified), NpgsqlDbType.Timestamp);
        }
        throw new NotSupportedException($"Тип PostgreSQL не поддержан: {postgresType}");
    }

    private static async Task MarkDeliveryAttemptAsync(
        NpgsqlConnection local, string patchId, string status, string? error, bool applied = false)
    {
        await using var command = local.CreateCommand();
        command.CommandText = """
            INSERT INTO berg_sync.patch_delivery
              (patch_id,target,status,attempts,last_error,applied_at)
            VALUES ($1,'hosting',$2,1,$3,CASE WHEN $4 THEN current_timestamp ELSE NULL END)
            ON CONFLICT (patch_id,target) DO UPDATE SET
              status=excluded.status,
              attempts=berg_sync.patch_delivery.attempts+1,
              last_error=excluded.last_error,
              applied_at=CASE WHEN $4 THEN current_timestamp ELSE berg_sync.patch_delivery.applied_at END
            """;
        command.Parameters.AddWithValue(patchId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue((object?)Truncate(error, 4000) ?? DBNull.Value);
        command.Parameters.AddWithValue(applied);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<RemoteState> ReadRemoteStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT baseline_id,delta_seq FROM berg_sync.state WHERE id=1";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("На хостинге отсутствует berg_sync.state.");
        return new RemoteState(reader.GetString(0), reader.GetInt64(1));
    }

    private static async Task<bool> PatchExistsAsync(NpgsqlConnection connection, string patchId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM berg_sync.patches WHERE patch_id=$1)";
        command.Parameters.AddWithValue(patchId);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> CalculateSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
    private static string Stage(string table) => Quote("delta_" + table.ToLowerInvariant());
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record RemoteState(string BaselineId, long DeltaSeq);
}

internal sealed class DeltaPackage
{
    public string PatchId { get; init; } = string.Empty;
    public string BaselineId { get; init; } = string.Empty;
    public long FromDeltaSeq { get; init; }
    public long ToDeltaSeq { get; init; }
    public string SourceMdb { get; init; } = string.Empty;
    public DateTime PeriodStart { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public double DateOffsetHours { get; init; }
    public string DeletePolicy { get; init; } = "ignore";
    public Dictionary<string, DeltaTableCount> Counts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> SourceMaxIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<SerializedDeltaRow>> Tables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AffectedPair> AffectedPairs { get; init; } = [];
}

internal sealed class SerializedDeltaRow
{
    public List<string?> Values { get; init; } = [];
}

internal sealed record DeltaTableCount(long Inserted, long Updated);
internal sealed record AffectedPair(int IdPayer, int IdSeller);
