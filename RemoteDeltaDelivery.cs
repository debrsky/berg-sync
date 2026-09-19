using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

internal static class RemoteDeltaDelivery
{
    private static readonly string[] Tables =
        ["Bosses", "Cargos", "Cars", "Customers", "Tariffs", "Applications", "XInvoices", "XInvoicePays", "XInvoiceDatas"];
    private const string OperationsFileName = "operations.copy.gz";
    private const string OperationsColumns = """
        op_seq_num,id_seller,id_payer,op_date,op_date_ts,op_type,id_invoice,inv_amount,op_amount,
        balance_before,charge_amount,payment_amount,balance_after,prepayment_before,prepayment_added,
        prepayment_applied,prepayment_after,inv_debt_before,inv_debt_after,debt_invoices_before,debt_invoices_after
        """;
    private const string OperationsSelectColumns = """
        o.op_seq_num,o.id_seller,o.id_payer,o.op_date,o.op_date_ts,o.op_type,o.id_invoice,o.inv_amount,o.op_amount,
        o.balance_before,o.charge_amount,o.payment_amount,o.balance_after,o.prepayment_before,o.prepayment_added,
        o.prepayment_applied,o.prepayment_after,o.inv_debt_before,o.inv_debt_after,o.debt_invoices_before,o.debt_invoices_after
        """;

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
        MigrationSchema schema,
        NpgsqlConnection local,
        NpgsqlTransaction transaction,
        bool includeOperations)
    {
        var totalTimer = Stopwatch.StartNew();
        var phaseTimer = Stopwatch.StartNew();
        var package = new DeltaPackage
        {
            FormatVersion = includeOperations ? 2 : 1,
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

        LogProfile(patchId, "Сериализация строк пакета", phaseTimer,
            $"строк: {package.Tables.Values.Sum(table => table.Count):N0}; пар: {affectedPairs.Count:N0}");

        var directory = Path.Combine(root, patchId);
        Directory.CreateDirectory(directory);
        if (includeOperations)
        {
            phaseTimer.Restart();
            var operationsPath = Path.Combine(directory, OperationsFileName);
            var temporaryOperationsPath = operationsPath + ".tmp";
            package.OperationsRowCount = await ScalarInt64Async(local, transaction, """
                SELECT count(*)
                FROM bergapp.operations o
                JOIN delta_affected_pairs p USING (id_payer,id_seller)
                """);
            await ExportOperationsAsync(local, temporaryOperationsPath);
            File.Move(temporaryOperationsPath, operationsPath, overwrite: true);
            package.OperationsFile = OperationsFileName;
            package.OperationsSha256 = await CalculateSha256Async(operationsPath);
            LogProfile(patchId, "Выгрузка operations в пакет", phaseTimer,
                $"строк: {package.OperationsRowCount:N0}; размер: {new FileInfo(operationsPath).Length:N0} байт");
        }

        var packagePath = Path.Combine(directory, "package.json");
        var temporaryPath = packagePath + ".tmp";
        phaseTimer.Restart();
        var json = JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, packagePath, overwrite: true);
        LogProfile(patchId, "Запись package.json", phaseTimer,
            $"размер: {new FileInfo(packagePath).Length:N0} байт");

        phaseTimer.Restart();
        var checksum = await CalculateSha256Async(packagePath);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "package.sha256"), checksum + "\n", new UTF8Encoding(false));
        LogProfile(patchId, "Расчёт и запись SHA-256", phaseTimer);
        LogProfile(patchId, "Формирование пакета — всего", totalTimer);
        return directory;
    }

    public static async Task DeliverPendingAsync(
        NpgsqlConnection local,
        string hostingConnectionString,
        string packageRoot,
        MigrationSchema schema,
        bool recalculate,
        bool refreshViews,
        Action<string>? stageChanged = null)
    {
        var timer = Stopwatch.StartNew();
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
            LogProfile("pending", "Чтение очереди доставки", timer, "таблица состояния отсутствует");
            return;
        }

        LogProfile("pending", "Чтение очереди доставки", timer, $"пакетов: {pending.Count:N0}");
        foreach (var patchId in pending)
        {
            Console.WriteLine($"Повторная доставка pending-пакета: {patchId}");
            var package = await LoadPackageAsync(packageRoot, patchId);
            await DeliverAndRecordAsync(
                local, hostingConnectionString, package, schema, recalculate, refreshViews, stageChanged);
        }
    }

    public static async Task DeliverAndRecordAsync(
        NpgsqlConnection local,
        string hostingConnectionString,
        DeltaPackage package,
        MigrationSchema schema,
        bool recalculate,
        bool refreshViews,
        Action<string>? stageChanged = null)
    {
        var totalTimer = Stopwatch.StartNew();
        var phaseTimer = Stopwatch.StartNew();
        try
        {
            Console.WriteLine($"[PROFILE] [{package.PatchId}] Начало публикации на хостинг; " +
                              $"строк: {package.Tables.Values.Sum(table => table.Count):N0}; " +
                              $"пар: {package.AffectedPairs.Count:N0}");
            await MarkDeliveryAttemptAsync(local, package.PatchId, "applying", null);
            LogProfile(package.PatchId, "Локальная отметка applying", phaseTimer);

            await ApplyAsync(hostingConnectionString, package, schema, recalculate, refreshViews, stageChanged);

            phaseTimer.Restart();
            await MarkDeliveryAttemptAsync(local, package.PatchId, "applied", null, applied: true);
            LogProfile(package.PatchId, "Локальная отметка applied", phaseTimer);
            LogProfile(package.PatchId, "Публикация на хостинг — всего", totalTimer);
        }
        catch (Exception exception)
        {
            phaseTimer.Restart();
            await MarkDeliveryAttemptAsync(local, package.PatchId, "failed", exception.Message);
            LogProfile(package.PatchId, "Локальная отметка failed", phaseTimer);
            totalTimer.Stop();
            Console.WriteLine($"[PROFILE] [{package.PatchId}] Публикация завершилась ошибкой через " +
                              $"{totalTimer.Elapsed.TotalMilliseconds:N0} мс: {exception.Message}");
            throw;
        }
    }

    public static async Task<DeltaPackage> LoadPackageAsync(string root, string patchId)
    {
        var totalTimer = Stopwatch.StartNew();
        var phaseTimer = Stopwatch.StartNew();
        var directory = Path.Combine(root, patchId);
        var packagePath = Path.Combine(directory, "package.json");
        var checksumPath = Path.Combine(directory, "package.sha256");
        if (!File.Exists(packagePath) || !File.Exists(checksumPath))
            throw new FileNotFoundException($"Не найден пакет дельты {patchId} в {directory}.");

        var expected = (await File.ReadAllTextAsync(checksumPath)).Trim().ToLowerInvariant();
        var actual = await CalculateSha256Async(packagePath);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"Не совпадает SHA-256 пакета {patchId}.");
        LogProfile(patchId, "Проверка SHA-256 пакета", phaseTimer,
            $"размер: {new FileInfo(packagePath).Length:N0} байт");

        phaseTimer.Restart();
        var package = JsonSerializer.Deserialize<DeltaPackage>(await File.ReadAllTextAsync(packagePath))
                      ?? throw new InvalidOperationException($"Не удалось прочитать пакет {patchId}.");
        package.PackageDirectory = directory;
        if (package.FormatVersion >= 2)
        {
            if (!string.Equals(package.OperationsFile, OperationsFileName, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(package.OperationsSha256))
                throw new InvalidOperationException($"В пакете {patchId} отсутствует корректный снимок operations.");
            var operationsPath = Path.Combine(directory, package.OperationsFile!);
            if (!File.Exists(operationsPath))
                throw new FileNotFoundException($"Не найден снимок operations пакета {patchId}.", operationsPath);
            var operationsChecksum = await CalculateSha256Async(operationsPath);
            if (!string.Equals(operationsChecksum, package.OperationsSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Не совпадает SHA-256 снимка operations пакета {patchId}.");
        }
        LogProfile(patchId, "Чтение и десериализация пакета", phaseTimer,
            $"строк: {package.Tables.Values.Sum(table => table.Count):N0}; operations: {package.OperationsRowCount:N0}");
        LogProfile(patchId, "Загрузка пакета — всего", totalTimer);
        return package;
    }

    private static async Task ApplyAsync(
        string connectionString,
        DeltaPackage package,
        MigrationSchema schema,
        bool recalculate,
        bool refreshViews,
        Action<string>? stageChanged)
    {
        var phaseTimer = Stopwatch.StartNew();
        stageChanged?.Invoke("Подключение к PostgreSQL хостинга");
        await using var hosting = new NpgsqlConnection(connectionString);
        await hosting.OpenAsync();
        LogProfile(package.PatchId, "Подключение к PostgreSQL хостинга", phaseTimer,
            $"сервер: {hosting.Host}/{hosting.Database}");

        phaseTimer.Restart();
        var current = await ReadRemoteStateAsync(hosting);
        LogProfile(package.PatchId, "Чтение состояния хостинга", phaseTimer,
            $"состояние: {current.BaselineId}/{current.DeltaSeq}");

        phaseTimer.Restart();
        var alreadyApplied = current.BaselineId == package.BaselineId &&
                             current.DeltaSeq == package.ToDeltaSeq &&
                             await PatchExistsAsync(hosting, package.PatchId);
        LogProfile(package.PatchId, "Проверка идемпотентности", phaseTimer,
            $"уже применён: {(alreadyApplied ? "да" : "нет")}");
        if (alreadyApplied)
        {
            Console.WriteLine($"Пакет {package.PatchId} уже применён на хостинге.");
            return;
        }
        if (current.BaselineId != package.BaselineId || current.DeltaSeq != package.FromDeltaSeq)
            throw new InvalidOperationException(
                $"Состояние хостинга не подходит для {package.PatchId}: " +
                $"ожидалось {package.BaselineId}/{package.FromDeltaSeq}, " +
                $"получено {current.BaselineId}/{current.DeltaSeq}.");

        phaseTimer.Restart();
        await using var transaction = await hosting.BeginTransactionAsync();
        await ExecuteAsync(hosting, transaction, "SELECT pg_advisory_xact_lock(1896472001)");
        LogProfile(package.PatchId, "Открытие транзакции и ожидание advisory lock", phaseTimer);

        // Повторяем проверку после получения блокировки.
        phaseTimer.Restart();
        current = await ReadRemoteStateAsync(hosting, transaction);
        LogProfile(package.PatchId, "Повторная проверка состояния под блокировкой", phaseTimer);
        if (current.BaselineId != package.BaselineId || current.DeltaSeq != package.FromDeltaSeq)
            throw new InvalidOperationException("Состояние хостинга изменилось во время ожидания блокировки.");

        await EnsureOperationsPrimaryKeyAsync(hosting, transaction);

        stageChanged?.Invoke("Загрузка данных на хостинг");
        foreach (var table in Tables)
        {
            phaseTimer.Restart();
            await ExecuteAsync(hosting, transaction,
                $"CREATE TEMP TABLE {Stage(table)} (LIKE bergauto.{Quote(table)} INCLUDING DEFAULTS) ON COMMIT DROP");
            var rows = package.Tables.GetValueOrDefault(table) ?? [];
            await CopyRowsAsync(
                hosting, table, schema.Tables[table], rows, package.DateOffsetHours);
            LogProfile(package.PatchId, $"Создание staging и COPY: {table}", phaseTimer,
                $"строк: {rows.Count:N0}");
        }

        phaseTimer.Restart();
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
        LogProfile(package.PatchId, "COPY затронутых финансовых пар", phaseTimer,
            $"пар: {package.AffectedPairs.Distinct().Count():N0}");

        foreach (var table in Tables)
        {
            phaseTimer.Restart();
            await UpsertAsync(hosting, transaction, table, schema.Tables[table]);
            var count = package.Counts.GetValueOrDefault(table);
            LogProfile(package.PatchId, $"UPSERT на хостинге: {table}", phaseTimer,
                $"изменений: {count?.Inserted + count?.Updated ?? 0:N0}");
        }

        if (recalculate && package.AffectedPairs.Count > 0)
        {
            stageChanged?.Invoke("Замена финансовых операций на хостинге");
            phaseTimer.Restart();
            if (package.FormatVersion >= 2)
            {
                await ReplaceOperationsAsync(hosting, transaction, package);
                LogProfile(package.PatchId, "Замена operations на хостинге", phaseTimer,
                    $"пар: {package.AffectedPairs.Count:N0}; строк: {package.OperationsRowCount:N0}");
            }
            else
            {
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
                LogProfile(package.PatchId, "Пересчёт operations на хостинге (legacy-пакет)", phaseTimer,
                    $"пар: {package.AffectedPairs.Count:N0}");
            }
        }

        var totalChanges = package.Counts.Values.Sum(value => value.Inserted + value.Updated);
        if (refreshViews && totalChanges > 0)
        {
            stageChanged?.Invoke("Обновление представлений на хостинге");
            foreach (var view in new[] { "payers", "sellers", "invoices", "counterparties", "balances", "debt_invoices" })
            {
                phaseTimer.Restart();
                await ExecuteAsync(hosting, transaction, $"REFRESH MATERIALIZED VIEW bergapp.{Quote(view)}");
                LogProfile(package.PatchId, $"REFRESH на хостинге: {view}", phaseTimer);
            }
        }

        stageChanged?.Invoke("Фиксация и проверка публикации");
        phaseTimer.Restart();

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

        LogProfile(package.PatchId, "Запись метаданных патча на хостинге", phaseTimer);
        phaseTimer.Restart();
        await transaction.CommitAsync();
        LogProfile(package.PatchId, "COMMIT на хостинге", phaseTimer);

        phaseTimer.Restart();
        var appliedState = await ReadRemoteStateAsync(hosting);
        if (appliedState.BaselineId != package.BaselineId ||
            appliedState.DeltaSeq != package.ToDeltaSeq ||
            !await PatchExistsAsync(hosting, package.PatchId))
            throw new InvalidOperationException(
                $"Проверка применённого пакета {package.PatchId} на хостинге не пройдена.");

        LogProfile(package.PatchId, "Проверка результата публикации", phaseTimer,
            $"состояние: {appliedState.BaselineId}/{appliedState.DeltaSeq}");
        Console.WriteLine($"Пакет {package.PatchId} применён на хостинге.");
    }

    private static async Task ExportOperationsAsync(NpgsqlConnection connection, string path)
    {
        await using var source = await connection.BeginRawBinaryCopyAsync($"""
            COPY (
              SELECT {OperationsSelectColumns}
              FROM bergapp.operations o
              JOIN delta_affected_pairs p USING (id_payer,id_seller)
              ORDER BY o.id_seller,o.id_payer,o.op_seq_num
            ) TO STDOUT (FORMAT BINARY)
            """);
        await using var file = File.Create(path);
        await using var compressed = new GZipStream(file, CompressionLevel.SmallestSize);
        await source.CopyToAsync(compressed);
    }

    private static async Task ReplaceOperationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DeltaPackage package)
    {
        await ExecuteAsync(connection, transaction,
            "CREATE TEMP TABLE delta_operations (LIKE bergapp.operations INCLUDING DEFAULTS) ON COMMIT DROP");

        var path = Path.Combine(package.PackageDirectory, package.OperationsFile!);
        await using (var file = File.OpenRead(path))
        await using (var decompressed = new GZipStream(file, CompressionMode.Decompress))
        await using (var target = await connection.BeginRawBinaryCopyAsync(
                         $"COPY delta_operations ({OperationsColumns}) FROM STDIN (FORMAT BINARY)"))
            await decompressed.CopyToAsync(target);

        var actualRows = await ScalarInt64Async(connection, transaction, "SELECT count(*) FROM delta_operations");
        if (actualRows != package.OperationsRowCount)
            throw new InvalidOperationException(
                $"Снимок operations содержит {actualRows:N0} строк вместо {package.OperationsRowCount:N0}.");

        await ExecuteAsync(connection, transaction, """
            DELETE FROM bergapp.operations o
            USING delta_affected_pairs p
            WHERE o.id_payer=p.id_payer AND o.id_seller=p.id_seller;
            INSERT INTO bergapp.operations (
              op_seq_num,id_seller,id_payer,op_date,op_date_ts,op_type,id_invoice,inv_amount,op_amount,
              balance_before,charge_amount,payment_amount,balance_after,prepayment_before,prepayment_added,
              prepayment_applied,prepayment_after,inv_debt_before,inv_debt_after,debt_invoices_before,debt_invoices_after
            )
            SELECT
              op_seq_num,id_seller,id_payer,op_date,op_date_ts,op_type,id_invoice,inv_amount,op_amount,
              balance_before,charge_amount,payment_amount,balance_after,prepayment_before,prepayment_added,
              prepayment_applied,prepayment_after,inv_debt_before,inv_debt_after,debt_invoices_before,debt_invoices_after
            FROM delta_operations;
            """);
    }

    internal static async Task EnsureOperationsPrimaryKeyAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 0;
        command.CommandText = """
            DO $body$
            BEGIN
              IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid='bergapp.operations'::regclass AND contype='p'
              ) THEN
                ALTER TABLE bergapp.operations
                  ALTER COLUMN id_seller SET NOT NULL,
                  ALTER COLUMN id_payer SET NOT NULL,
                  ALTER COLUMN op_seq_num SET NOT NULL;
                ALTER TABLE bergapp.operations
                  ADD CONSTRAINT operations_pkey PRIMARY KEY (id_seller,id_payer,op_seq_num);
              END IF;
            END $body$;
            DROP INDEX IF EXISTS bergapp.operations_payer_seller_idx;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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

    private static void LogProfile(
        string patchId, string operation, Stopwatch stopwatch, string? details = null)
    {
        stopwatch.Stop();
        Console.WriteLine($"[PROFILE] [{patchId}] {operation}: {stopwatch.Elapsed.TotalMilliseconds:N0} мс" +
                          (string.IsNullOrWhiteSpace(details) ? string.Empty : $"; {details}"));
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
    private static string Stage(string table) => Quote("delta_" + table.ToLowerInvariant());
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record RemoteState(string BaselineId, long DeltaSeq);
}

internal sealed class DeltaPackage
{
    public int FormatVersion { get; init; } = 1;
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
    public string? OperationsFile { get; set; }
    public string? OperationsSha256 { get; set; }
    public long OperationsRowCount { get; set; }
    [JsonIgnore]
    public string PackageDirectory { get; set; } = string.Empty;
}

internal sealed class SerializedDeltaRow
{
    public List<string?> Values { get; init; } = [];
}

internal sealed record DeltaTableCount(long Inserted, long Updated);
internal sealed record AffectedPair(int IdPayer, int IdSeller);
