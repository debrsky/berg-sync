using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

internal static class IncrementalSync
{
    private static readonly string[] SmallTables = ["Bosses", "Cargos", "Cars", "Tariffs"];
    private static readonly string[] DataTables =
        ["Bosses", "Cargos", "Cars", "Customers", "Tariffs", "Applications", "XInvoices", "XInvoicePays", "XInvoiceDatas"];
    private static readonly string[] MaxIdTables =
        ["Customers", "Applications", "XInvoices", "XInvoicePays", "XInvoiceDatas"];

    public static async Task<IncrementalSyncResult> RunAsync(
        OleDbConnection access,
        string baseConnectionString,
        string databaseName,
        int periodDays,
        int idOverlap,
        int paymentIdOverlap,
        int invoiceDataIdOverlap,
        bool fullCustomers,
        double dateOffsetHours,
        int maxChanges,
        bool recalculate,
        bool refreshViews,
        string sourcePath,
        bool syncHosting,
        string? packageDirectory,
        Action<string>? stageChanged = null)
    {
        void Stage(string name) => stageChanged?.Invoke(name);

        var timer = Stopwatch.StartNew();
        Stage("Загрузка конфигурации");
        var schema = LoadSchema();
        ValidateSchema(schema);

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = databaseName };
        await using var postgres = new NpgsqlConnection(builder.ConnectionString);
        Stage("Подключение к локальной PostgreSQL");
        await postgres.OpenAsync();
        Console.WriteLine("\nИНКРЕМЕНТАЛЬНАЯ ВЫГРУЗКА MDB → локальный PostgreSQL");
        Console.WriteLine($"  назначение: {postgres.Host}/{postgres.Database}");
        Console.WriteLine($"  период: {periodDays} дней; overlap: {idOverlap:N0}");
        Console.WriteLine($"  XInvoicePays overlap: {paymentIdOverlap:N0}; XInvoiceDatas overlap: {invoiceDataIdOverlap:N0}");
        Console.WriteLine($"  Customers: {(fullCustomers ? "полный проход" : "новые и связанные")}");
        Console.WriteLine("  удаления: не применяются");

        var phaseTimer = Stopwatch.StartNew();
        Stage("Подготовка локальной базы");
        await EnsureTargetAsync(postgres, schema);
        LogProfile("Подготовка локальной базы", phaseTimer);

        var packageRoot = RemoteDeltaDelivery.ResolvePackageRoot(packageDirectory);
        string? hostingConnectionString = null;
        if (syncHosting)
        {
            hostingConnectionString = Environment.GetEnvironmentVariable("PG_HOSTING_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(hostingConnectionString))
                throw new ArgumentException("Для --sync-hosting задайте PG_HOSTING_CONNECTION_STRING.");
            phaseTimer.Restart();
            Stage("Проверка отложенных публикаций");
            await RemoteDeltaDelivery.DeliverPendingAsync(
                postgres, hostingConnectionString, packageRoot, schema, recalculate, refreshViews, stageChanged);
            LogProfile("Проверка и доставка отложенных пакетов", phaseTimer);
        }

        phaseTimer.Restart();
        Stage("Поиск изменений в MDB");
        var targetMax = await ReadTargetMaxIdsAsync(postgres);
        var sourceMax = ReadSourceMaxIds(access);
        ValidateFreshness(sourceMax, targetMax);
        LogProfile("Чтение и проверка максимальных ID", phaseTimer);

        var periodStart = DateTime.Today.AddDays(-periodDays);
        phaseTimer.Restart();
        var candidates = ReadCandidates(
            access, schema, targetMax, periodStart, idOverlap,
            paymentIdOverlap, invoiceDataIdOverlap, fullCustomers);
        LogProfile("Чтение кандидатов из MDB", phaseTimer,
            $"строк: {candidates.Values.Sum(rows => rows.Count):N0}");

        Console.WriteLine("\nКандидаты:");
        foreach (var table in DataTables)
            Console.WriteLine($"  {table,-20} {candidates[table].Count,10:N0}");

        phaseTimer.Restart();
        await using var transaction = await postgres.BeginTransactionAsync();
        await ExecuteAsync(postgres, "SELECT pg_advisory_xact_lock(1896472001)", transaction);
        var state = await EnsureStateAsync(
            postgres, transaction, sourcePath, dateOffsetHours, targetMax);
        LogProfile("Открытие локальной транзакции и получение блокировки", phaseTimer);

        if (Math.Abs(state.DateOffsetHours - dateOffsetHours) > 0.000001)
            throw new InvalidOperationException(
                $"Коррекция дат не совпадает с baseline: в состоянии {state.DateOffsetHours}, задано {dateOffsetHours}.");

        phaseTimer.Restart();
        await CreateStagingTablesAsync(postgres, transaction, schema);
        LogProfile("Создание локальных staging-таблиц", phaseTimer);
        foreach (var table in DataTables)
        {
            phaseTimer.Restart();
            await CopyRowsAsync(postgres, transaction, table, schema.Tables[table], candidates[table].Values, dateOffsetHours);
            LogProfile($"COPY в локальный staging: {table}", phaseTimer,
                $"строк: {candidates[table].Count:N0}");
        }

        phaseTimer.Restart();
        var changes = await ReadChangesAsync(postgres, transaction, schema);
        LogProfile("Сравнение staging с локальными таблицами", phaseTimer);

        phaseTimer.Restart();
        var unstable = await RemoveUnstableRowsAsync(
            access, postgres, transaction, schema, candidates, changes);
        LogProfile("Повторная проверка изменившихся строк MDB", phaseTimer,
            $"отложено нестабильных строк: {unstable:N0}");
        if (unstable > 0)
        {
            Console.WriteLine($"Нестабильных строк отложено: {unstable:N0}");
            changes = await ReadChangesAsync(postgres, transaction, schema);
        }

        var totalChanges = changes.Values.Sum(value => value.Inserted + value.Updated);
        if (totalChanges > maxChanges)
            throw new InvalidOperationException(
                $"Защитное ограничение: найдено {totalChanges:N0} изменений, максимум --delta-max-changes={maxChanges:N0}.");

        Console.WriteLine("\nФактические изменения:");
        foreach (var table in DataTables)
        {
            var value = changes[table];
            Console.WriteLine($"  {table,-20} +{value.Inserted,7:N0}  ~{value.Updated,7:N0}");
        }

        phaseTimer.Restart();
        await BuildAffectedPairsAsync(postgres, transaction);
        var affectedPairList = await ReadAffectedPairsAsync(postgres, transaction);
        var affectedPairs = affectedPairList.Count;
        LogProfile("Определение затронутых финансовых пар", phaseTimer,
            $"пар: {affectedPairs:N0}");

        phaseTimer.Restart();
        var changedRows = new Dictionary<string, IReadOnlyList<object?[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in DataTables)
        {
            var changedIds = await ReadChangedIdsAsync(postgres, transaction, table, schema.Tables[table]);
            changedRows[table] = changedIds.Select(id => candidates[table][id].Values).ToArray();
        }
        LogProfile("Формирование набора строк дельты", phaseTimer,
            $"строк: {changedRows.Values.Sum(rows => rows.Count):N0}");

        var nextSeq = state.DeltaSeq + 1;
        var patchId = $"{state.BaselineId}-delta-{nextSeq:0000}";
        var packageCounts = changes.ToDictionary(
            pair => pair.Key,
            pair => new DeltaTableCount(pair.Value.Inserted, pair.Value.Updated),
            StringComparer.OrdinalIgnoreCase);
        string? packagePath = null;

        Stage("Применение изменений к локальной базе");
        foreach (var table in DataTables)
        {
            phaseTimer.Restart();
            await UpsertAsync(postgres, transaction, table, schema.Tables[table]);
            var tableChanges = changes[table];
            LogProfile($"Локальный UPSERT: {table}", phaseTimer,
                $"изменений: {tableChanges.Inserted + tableChanges.Updated:N0}");
        }

        if (recalculate && affectedPairs > 0)
        {
            Stage("Пересчёт финансовых операций");
            Console.WriteLine($"Пересчёт финансовых пар: {affectedPairs:N0}");
            phaseTimer.Restart();
            if (affectedPairs <= 500)
            {
                await ExecuteAsync(postgres, """
                    DO $body$
                    DECLARE pair record;
                    BEGIN
                      FOR pair IN SELECT id_payer, id_seller FROM delta_affected_pairs LOOP
                        CALL bergapp.calculate_and_save_operations(pair.id_payer, pair.id_seller);
                      END LOOP;
                    END $body$
                    """, transaction);
            }
            else
            {
                Console.WriteLine("Затронуто более 500 пар — полный пересчёт operations.");
                await ExecuteAsync(postgres, "CALL bergapp.calculate_and_save_operations()", transaction);
            }
            LogProfile("Локальный пересчёт operations", phaseTimer,
                $"пар: {affectedPairs:N0}");
        }

        if (syncHosting)
        {
            Stage("Создание delta-пакета");
            packagePath = await RemoteDeltaDelivery.WritePackageAsync(
                packageRoot, patchId, state.BaselineId, state.DeltaSeq, nextSeq,
                sourcePath, periodStart, dateOffsetHours, changedRows,
                affectedPairList, packageCounts, sourceMax, schema,
                postgres, transaction, recalculate && affectedPairs > 0);
            Console.WriteLine($"Пакет дельты: {packagePath}");
        }

        if (refreshViews && totalChanges > 0)
        {
            Stage("Обновление локальных представлений");
            Console.WriteLine("Обновление materialized views...");
            foreach (var view in new[] { "payers", "sellers", "invoices", "counterparties", "balances", "debt_invoices" })
            {
                phaseTimer.Restart();
                await ExecuteAsync(postgres, $"REFRESH MATERIALIZED VIEW bergapp.{Quote(view)}", transaction);
                LogProfile($"Локальный REFRESH: {view}", phaseTimer);
            }
        }

        var countsJson = JsonSerializer.Serialize(packageCounts);
        var maxJson = JsonSerializer.Serialize(sourceMax);

        phaseTimer.Restart();
        await using (var command = postgres.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO berg_sync.patches
                  (patch_id, baseline_id, delta_seq, source_mdb, period_start, counts, affected_pairs, created_at)
                VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, current_timestamp)
                """;
            command.Parameters.AddWithValue(patchId);
            command.Parameters.AddWithValue(state.BaselineId);
            command.Parameters.AddWithValue(nextSeq);
            command.Parameters.AddWithValue(sourcePath);
            command.Parameters.AddWithValue(periodStart);
            command.Parameters.AddWithValue(countsJson);
            command.Parameters.AddWithValue(affectedPairs);
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = postgres.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO berg_sync.patch_delivery
                  (patch_id,target,status,attempts,applied_at)
                VALUES ($1,'local','applied',1,current_timestamp),
                       ($1,'hosting',$2,0,NULL)
                ON CONFLICT (patch_id,target) DO UPDATE SET
                  status=excluded.status,
                  applied_at=excluded.applied_at
                """;
            command.Parameters.AddWithValue(patchId);
            command.Parameters.AddWithValue(syncHosting ? "pending" : "not_requested");
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = postgres.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE berg_sync.state
                SET delta_seq=$1, source_mdb=$2, source_max_ids=$3::jsonb, updated_at=current_timestamp
                WHERE id=1
                """;
            command.Parameters.AddWithValue(nextSeq);
            command.Parameters.AddWithValue(sourcePath);
            command.Parameters.AddWithValue(maxJson);
            await command.ExecuteNonQueryAsync();
        }

        LogProfile("Запись метаданных локального патча", phaseTimer);
        phaseTimer.Restart();
        Stage("Фиксация локального патча");
        await transaction.CommitAsync();
        LogProfile("COMMIT локального патча", phaseTimer);

        if (syncHosting)
        {
            Stage("Публикация на хостинг");
            var package = await RemoteDeltaDelivery.LoadPackageAsync(packageRoot, patchId);
            await RemoteDeltaDelivery.DeliverAndRecordAsync(
                postgres, hostingConnectionString!, package, schema, recalculate, refreshViews, stageChanged);
        }

        timer.Stop();
        Console.WriteLine($"\nПрименён локальный патч: {patchId}");
        Console.WriteLine($"Изменено строк: {totalChanges:N0}; затронуто пар: {affectedPairs:N0}; время: {timer.Elapsed}");
        Console.WriteLine(syncHosting
            ? "Тот же пакет применён на хостинге."
            : "На хостинг данные не отправлялись.");
        Stage("Завершено");
        return new IncrementalSyncResult(
            patchId, state.BaselineId, nextSeq, totalChanges, affectedPairs, timer.Elapsed, syncHosting);
    }

    private static void LogProfile(string operation, Stopwatch stopwatch, string? details = null)
    {
        stopwatch.Stop();
        Console.WriteLine($"[PROFILE] {operation}: {stopwatch.Elapsed.TotalMilliseconds:N0} мс" +
                          (string.IsNullOrWhiteSpace(details) ? string.Empty : $"; {details}"));
    }

    private static Dictionary<string, Dictionary<int, SourceRow>> ReadCandidates(
        OleDbConnection access,
        MigrationSchema schema,
        IReadOnlyDictionary<string, long> targetMax,
        DateTime periodStart,
        int overlap,
        int paymentOverlap,
        int dataOverlap,
        bool fullCustomers)
    {
        var result = DataTables.ToDictionary(
            table => table, _ => new Dictionary<int, SourceRow>(), StringComparer.OrdinalIgnoreCase);

        foreach (var table in SmallTables)
        {
            Console.WriteLine($"  чтение кандидатов {table}...");
            AddRows(result[table], Query(access, table, schema.Tables[table]));
        }

        Console.WriteLine("  чтение кандидатов XInvoices...");
        AddRows(result["XInvoices"], Query(access, "XInvoices", schema.Tables["XInvoices"],
            "[ID] > ? OR [Date] >= ?", Threshold(targetMax, "XInvoices", overlap), periodStart));
        Console.WriteLine("  чтение кандидатов XInvoicePays...");
        AddRows(result["XInvoicePays"], Query(access, "XInvoicePays", schema.Tables["XInvoicePays"],
            "[ID] > ? OR [Date] >= ?", Threshold(targetMax, "XInvoicePays", paymentOverlap), periodStart));
        Console.WriteLine("  чтение кандидатов XInvoiceDatas...");
        AddRows(result["XInvoiceDatas"], Query(access, "XInvoiceDatas", schema.Tables["XInvoiceDatas"],
            "[ID] > ?", Threshold(targetMax, "XInvoiceDatas", dataOverlap)));
        Console.WriteLine("  чтение кандидатов Applications...");
        AddRows(result["Applications"], Query(access, "Applications", schema.Tables["Applications"],
            "[ID] > ? OR [DateReg] >= ? OR [DateWorkIn] >= ? OR [DateWorkOut] >= ?",
            Threshold(targetMax, "Applications", overlap), periodStart, periodStart, periodStart));

        var invoiceIds = new HashSet<int>(result["XInvoices"].Keys);
        invoiceIds.UnionWith(GetIntValues(result["XInvoicePays"].Values, schema.Tables["XInvoicePays"], "ID_XInvoice"));
        invoiceIds.UnionWith(GetIntValues(result["XInvoiceDatas"].Values, schema.Tables["XInvoiceDatas"], "ID_XInvoice"));
        AddRows(result["XInvoices"], QueryByIds(access, "XInvoices", schema.Tables["XInvoices"], "ID", invoiceIds));

        var applicationIds = new HashSet<int>(result["Applications"].Keys);
        applicationIds.UnionWith(GetIntValues(result["XInvoices"].Values, schema.Tables["XInvoices"], "ID_Application"));
        AddRows(result["Applications"], QueryByIds(access, "Applications", schema.Tables["Applications"], "ID", applicationIds));

        // Обратное замыкание: всегда перечитываем полный состав выбранных счетов.
        AddRows(result["XInvoicePays"], QueryByIds(
            access, "XInvoicePays", schema.Tables["XInvoicePays"], "ID_XInvoice", result["XInvoices"].Keys));
        AddRows(result["XInvoiceDatas"], QueryByIds(
            access, "XInvoiceDatas", schema.Tables["XInvoiceDatas"], "ID_XInvoice", result["XInvoices"].Keys));

        if (fullCustomers)
            AddRows(result["Customers"], Query(access, "Customers", schema.Tables["Customers"]));
        else
        {
            AddRows(result["Customers"], Query(access, "Customers", schema.Tables["Customers"],
                "[ID] > ?", Threshold(targetMax, "Customers", overlap)));
            var customerIds = new HashSet<int>();
            foreach (var name in new[] { "ID_Customer", "ID_CustomerOut", "ID_CustomerPay" })
                customerIds.UnionWith(GetIntValues(result["Applications"].Values, schema.Tables["Applications"], name));
            AddRows(result["Customers"], QueryByIds(
                access, "Customers", schema.Tables["Customers"], "ID", customerIds));
        }

        return result;
    }

    private static IEnumerable<SourceRow> QueryByIds(
        OleDbConnection access, string table, IReadOnlyList<MigrationColumn> columns,
        string filterColumn, IEnumerable<int> ids)
    {
        var distinct = ids.Distinct().ToArray();
        foreach (var chunk in distinct.Chunk(150))
        {
            var placeholders = string.Join(",", chunk.Select(_ => "?"));
            foreach (var row in Query(access, table, columns,
                         $"{QuoteAccess(filterColumn)} IN ({placeholders})", chunk.Cast<object>().ToArray()))
                yield return row;
        }
    }

    private static List<SourceRow> Query(
        OleDbConnection access, string table, IReadOnlyList<MigrationColumn> columns,
        string? where = null, params object[] parameters)
    {
        using var command = access.CreateCommand();
        command.CommandText = $"SELECT {string.Join(",", columns.Select(c => QuoteAccess(c.Name)))} " +
                              $"FROM {QuoteAccess(table)}" +
                              (where is null ? string.Empty : $" WHERE {where}");
        foreach (var value in parameters)
        {
            var parameter = value switch
            {
                DateTime date => new OleDbParameter { OleDbType = OleDbType.DBTimeStamp, Value = date },
                int number => new OleDbParameter { OleDbType = OleDbType.Integer, Value = number },
                long number when number is >= int.MinValue and <= int.MaxValue =>
                    new OleDbParameter { OleDbType = OleDbType.Integer, Value = (int)number },
                long number => new OleDbParameter { OleDbType = OleDbType.BigInt, Value = number },
                _ => new OleDbParameter { Value = value }
            };
            command.Parameters.Add(parameter);
        }

        OleDbDataReader reader;
        try
        {
            reader = command.ExecuteReader(CommandBehavior.SequentialAccess)
                ?? throw new InvalidOperationException($"Не удалось прочитать {table}.");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Ошибка чтения {table}. SQL: {command.CommandText}", exception);
        }
        using (reader)
        {
            var rows = new List<SourceRow>();
            while (reader.Read())
            {
                var values = new object?[columns.Count];
                for (var index = 0; index < values.Length; index++)
                    values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                rows.Add(new SourceRow(Convert.ToInt32(values[0], CultureInfo.InvariantCulture), values));
            }
            return rows;
        }
    }

    private static void AddRows(Dictionary<int, SourceRow> destination, IEnumerable<SourceRow> rows)
    {
        foreach (var row in rows)
            destination[row.Id] = row;
    }

    private static IEnumerable<int> GetIntValues(
        IEnumerable<SourceRow> rows, IReadOnlyList<MigrationColumn> columns, string column)
    {
        var index = ColumnIndex(columns, column);
        foreach (var row in rows)
            if (row.Values[index] is not null)
                yield return Convert.ToInt32(row.Values[index], CultureInfo.InvariantCulture);
    }

    private static async Task<int> RemoveUnstableRowsAsync(
        OleDbConnection access,
        NpgsqlConnection postgres,
        NpgsqlTransaction transaction,
        MigrationSchema schema,
        Dictionary<string, Dictionary<int, SourceRow>> candidates,
        Dictionary<string, ChangeCount> changes)
    {
        var unstableCount = 0;
        foreach (var table in DataTables)
        {
            if (changes[table].Inserted + changes[table].Updated == 0)
                continue;

            var ids = await ReadChangedIdsAsync(postgres, transaction, table, schema.Tables[table]);
            var reread = QueryByIds(access, table, schema.Tables[table], "ID", ids)
                .ToDictionary(row => row.Id);
            var unstable = ids.Where(id =>
                    !reread.TryGetValue(id, out var second) ||
                    !RowsEqual(candidates[table][id], second))
                .ToArray();
            if (unstable.Length == 0)
                continue;

            unstableCount += unstable.Length;
            await using var command = postgres.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {Stage(table)} WHERE \"ID\" = ANY($1)";
            command.Parameters.AddWithValue(unstable);
            await command.ExecuteNonQueryAsync();
        }
        return unstableCount;
    }

    private static bool RowsEqual(SourceRow first, SourceRow second)
    {
        if (first.Values.Length != second.Values.Length) return false;
        for (var index = 0; index < first.Values.Length; index++)
        {
            var a = first.Values[index];
            var b = second.Values[index];
            if (a is byte[] aa && b is byte[] bb)
            {
                if (!aa.AsSpan().SequenceEqual(bb)) return false;
            }
            else if (!Equals(a, b)) return false;
        }
        return true;
    }

    private static async Task CreateStagingTablesAsync(
        NpgsqlConnection postgres, NpgsqlTransaction transaction, MigrationSchema schema)
    {
        foreach (var table in DataTables)
            await ExecuteAsync(postgres,
                $"CREATE TEMP TABLE {Stage(table)} (LIKE bergauto.{Quote(table)} INCLUDING DEFAULTS) ON COMMIT DROP",
                transaction);
    }

    private static async Task CopyRowsAsync(
        NpgsqlConnection postgres,
        NpgsqlTransaction transaction,
        string table,
        IReadOnlyList<MigrationColumn> columns,
        IEnumerable<SourceRow> rows,
        double dateOffsetHours)
    {
        var columnList = string.Join(",", columns.Select(c => Quote(c.Name)));
        await using var importer = await postgres.BeginBinaryImportAsync(
            $"COPY {Stage(table)} ({columnList}) FROM STDIN (FORMAT BINARY)");
        foreach (var row in rows)
        {
            await importer.StartRowAsync();
            for (var index = 0; index < columns.Count; index++)
            {
                if (row.Values[index] is null)
                {
                    await importer.WriteNullAsync();
                    continue;
                }
                var converted = ConvertValue(row.Values[index]!, columns[index].Type, dateOffsetHours);
                await importer.WriteAsync(converted.Value, converted.Type);
            }
        }
        await importer.CompleteAsync();
    }

    private static async Task<Dictionary<string, ChangeCount>> ReadChangesAsync(
        NpgsqlConnection postgres, NpgsqlTransaction transaction, MigrationSchema schema)
    {
        var result = new Dictionary<string, ChangeCount>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in DataTables)
        {
            var distinct = DistinctSql("target", "incoming", schema.Tables[table]);
            await using var command = postgres.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT count(*) FILTER (WHERE target."ID" IS NULL),
                       count(*) FILTER (WHERE target."ID" IS NOT NULL AND ({distinct}))
                FROM {Stage(table)} incoming
                LEFT JOIN bergauto.{Quote(table)} target USING ("ID")
                """;
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            result[table] = new ChangeCount(reader.GetInt64(0), reader.GetInt64(1));
        }
        return result;
    }

    private static async Task<int[]> ReadChangedIdsAsync(
        NpgsqlConnection postgres, NpgsqlTransaction transaction,
        string table, IReadOnlyList<MigrationColumn> columns)
    {
        await using var command = postgres.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT incoming."ID"
            FROM {Stage(table)} incoming
            LEFT JOIN bergauto.{Quote(table)} target USING ("ID")
            WHERE target."ID" IS NULL OR ({DistinctSql("target", "incoming", columns)})
            """;
        var result = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetInt32(0));
        return result.ToArray();
    }

    private static async Task UpsertAsync(
        NpgsqlConnection postgres, NpgsqlTransaction transaction,
        string table, IReadOnlyList<MigrationColumn> columns)
    {
        var names = string.Join(",", columns.Select(c => Quote(c.Name)));
        var assignments = string.Join(",", columns.Skip(1).Select(c => $"{Quote(c.Name)}=excluded.{Quote(c.Name)}"));
        var distinct = DistinctSql("target", "excluded", columns);
        await ExecuteAsync(postgres, $"""
            INSERT INTO bergauto.{Quote(table)} AS target ({names})
            SELECT {names} FROM {Stage(table)}
            ON CONFLICT ("ID") DO UPDATE SET {assignments}
            WHERE {distinct}
            """, transaction);
    }

    private static async Task BuildAffectedPairsAsync(NpgsqlConnection postgres, NpgsqlTransaction transaction)
    {
        await ExecuteAsync(postgres, """
            CREATE TEMP TABLE delta_affected_pairs (
              id_payer integer NOT NULL, id_seller integer NOT NULL,
              PRIMARY KEY (id_payer, id_seller)
            ) ON COMMIT DROP;

            WITH changed_invoices AS (
              SELECT s."ID" FROM delta_XInvoices s
              LEFT JOIN bergauto."XInvoices" t USING ("ID")
              WHERE t."ID" IS NULL OR t IS DISTINCT FROM s
            ), changed_pays AS (
              SELECT s."ID_XInvoice" AS id FROM delta_XInvoicePays s
              LEFT JOIN bergauto."XInvoicePays" t USING ("ID")
              WHERE t."ID" IS NULL OR t IS DISTINCT FROM s
            ), changed_datas AS (
              SELECT s."ID_XInvoice" AS id FROM delta_XInvoiceDatas s
              LEFT JOIN bergauto."XInvoiceDatas" t USING ("ID")
              WHERE t."ID" IS NULL OR t IS DISTINCT FROM s
            ), changed_apps AS (
              SELECT s."ID" FROM delta_Applications s
              LEFT JOIN bergauto."Applications" t USING ("ID")
              WHERE t."ID" IS NULL OR t IS DISTINCT FROM s
            ), invoice_ids AS (
              SELECT "ID" AS id FROM changed_invoices
              UNION SELECT id FROM changed_pays
              UNION SELECT id FROM changed_datas
              UNION SELECT i."ID" FROM bergauto."XInvoices" i JOIN changed_apps a ON a."ID"=i."ID_Application"
              UNION SELECT i."ID" FROM delta_XInvoices i JOIN changed_apps a ON a."ID"=i."ID_Application"
            ), versions AS (
              SELECT i."ID_Boss" seller, a."ID_CustomerPay" payer
              FROM invoice_ids x JOIN bergauto."XInvoices" i ON i."ID"=x.id
              JOIN bergauto."Applications" a ON a."ID"=i."ID_Application"
              UNION
              SELECT i."ID_Boss", COALESCE(da."ID_CustomerPay", a."ID_CustomerPay")
              FROM invoice_ids x JOIN delta_XInvoices i ON i."ID"=x.id
              LEFT JOIN delta_Applications da ON da."ID"=i."ID_Application"
              LEFT JOIN bergauto."Applications" a ON a."ID"=i."ID_Application"
              UNION
              SELECT i."ID_Boss", da."ID_CustomerPay"
              FROM invoice_ids x JOIN bergauto."XInvoices" i ON i."ID"=x.id
              JOIN delta_Applications da ON da."ID"=i."ID_Application"
            )
            INSERT INTO delta_affected_pairs
            SELECT payer, seller FROM versions WHERE payer IS NOT NULL AND seller IS NOT NULL
            ON CONFLICT DO NOTHING;
            """, transaction);
    }

    private static async Task<List<AffectedPair>> ReadAffectedPairsAsync(
        NpgsqlConnection postgres, NpgsqlTransaction transaction)
    {
        var result = new List<AffectedPair>();
        await using var command = postgres.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id_payer,id_seller FROM delta_affected_pairs ORDER BY id_payer,id_seller";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new AffectedPair(reader.GetInt32(0), reader.GetInt32(1)));
        return result;
    }

    private static async Task EnsureTargetAsync(NpgsqlConnection postgres, MigrationSchema schema)
    {
        foreach (var table in DataTables)
        {
            await using var command = postgres.CreateCommand();
            command.CommandText = "SELECT to_regclass($1) IS NOT NULL";
            command.Parameters.AddWithValue($"bergauto.\"{table}\"");
            if (!Convert.ToBoolean(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture))
                throw new InvalidOperationException(
                    $"Нет целевой таблицы bergauto.{table}. Сначала выполните полную миграцию.");

            if (schema.Tables[table].All(column => !column.PK))
                throw new InvalidOperationException($"У таблицы {table} не задан первичный ключ.");
        }

        await using (var command = postgres.CreateCommand())
        {
            command.CommandText = "SELECT to_regclass('bergapp.operations') IS NOT NULL";
            if (!Convert.ToBoolean(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture))
                throw new InvalidOperationException(
                    "Нет целевой таблицы bergapp.operations. Сначала выполните полную миграцию.");
        }
        await RemoteDeltaDelivery.EnsureOperationsPrimaryKeyAsync(postgres);
    }

    private static Dictionary<string, long> ReadSourceMaxIds(OleDbConnection access)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in MaxIdTables)
        {
            using var command = access.CreateCommand();
            command.CommandText = $"SELECT MAX([ID]) FROM {QuoteAccess(table)}";
            var value = command.ExecuteScalar();
            result[table] = value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static async Task<Dictionary<string, long>> ReadTargetMaxIdsAsync(NpgsqlConnection postgres)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in MaxIdTables)
            result[table] = await ScalarInt64Async(postgres,
                $"SELECT COALESCE(MAX(\"ID\"),0) FROM bergauto.{Quote(table)}");
        return result;
    }

    private static void ValidateFreshness(
        IReadOnlyDictionary<string, long> source, IReadOnlyDictionary<string, long> target)
    {
        var stale = MaxIdTables.Where(table => source[table] < target[table])
            .Select(table => $"{table}: MDB={source[table]}, PostgreSQL={target[table]}")
            .ToArray();
        if (stale.Length > 0)
            throw new InvalidOperationException(
                "MDB логически старее локальной базы; выгрузка отклонена:\n  " + string.Join("\n  ", stale));
    }

    private static async Task<SyncState> EnsureStateAsync(
        NpgsqlConnection postgres, NpgsqlTransaction transaction,
        string sourcePath, double dateOffsetHours, IReadOnlyDictionary<string, long> targetMax)
    {
        await ExecuteAsync(postgres, """
            CREATE SCHEMA IF NOT EXISTS berg_sync;
            CREATE TABLE IF NOT EXISTS berg_sync.state (
              id integer PRIMARY KEY CHECK (id=1),
              baseline_id text NOT NULL,
              delta_seq bigint NOT NULL,
              source_mdb text,
              date_offset_hours double precision NOT NULL,
              source_max_ids jsonb NOT NULL DEFAULT '{}'::jsonb,
              updated_at timestamp with time zone NOT NULL DEFAULT current_timestamp
            );
            CREATE TABLE IF NOT EXISTS berg_sync.patches (
              patch_id text PRIMARY KEY,
              baseline_id text NOT NULL,
              delta_seq bigint NOT NULL,
              source_mdb text,
              period_start timestamp without time zone NOT NULL,
              counts jsonb NOT NULL,
              affected_pairs bigint NOT NULL,
              created_at timestamp with time zone NOT NULL DEFAULT current_timestamp
            );
            CREATE TABLE IF NOT EXISTS berg_sync.patch_delivery (
              patch_id text NOT NULL,
              target text NOT NULL,
              status text NOT NULL,
              attempts integer NOT NULL DEFAULT 0,
              last_error text,
              applied_at timestamp with time zone,
              PRIMARY KEY (patch_id, target)
            );
            """, transaction);

        await using var read = postgres.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT baseline_id,delta_seq,date_offset_hours,source_max_ids::text FROM berg_sync.state WHERE id=1";
        await using var reader = await read.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var state = new SyncState(reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2));
            var storedJson = reader.GetString(3);
            await reader.DisposeAsync();
            var stored = JsonSerializer.Deserialize<Dictionary<string, long>>(storedJson)
                         ?? new Dictionary<string, long>();
            if (stored.Count > 0 && MaxIdTables.Any(t => !stored.TryGetValue(t, out var value) || value != targetMax[t]))
            {
                var baseline = $"{DateTime.Now:yyyy-MM-dd-HHmmss}-local-full";
                await ResetStateAsync(postgres, transaction, baseline, sourcePath, dateOffsetHours, targetMax);
                Console.WriteLine($"Обнаружена новая полная локальная база. Новый baseline: {baseline}");
                return new SyncState(baseline, 0, dateOffsetHours);
            }
            return state;
        }
        await reader.DisposeAsync();

        var newBaseline = $"{DateTime.Now:yyyy-MM-dd-HHmmss}-local-full";
        await ResetStateAsync(postgres, transaction, newBaseline, sourcePath, dateOffsetHours, targetMax);
        Console.WriteLine($"Создано состояние синхронизации. Baseline: {newBaseline}");
        return new SyncState(newBaseline, 0, dateOffsetHours);
    }

    private static async Task ResetStateAsync(
        NpgsqlConnection postgres, NpgsqlTransaction transaction, string baseline,
        string sourcePath, double dateOffsetHours, IReadOnlyDictionary<string, long> targetMax)
    {
        await using var command = postgres.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO berg_sync.state
              (id,baseline_id,delta_seq,source_mdb,date_offset_hours,source_max_ids,updated_at)
            VALUES (1,$1,0,$2,$3,$4::jsonb,current_timestamp)
            ON CONFLICT (id) DO UPDATE SET
              baseline_id=excluded.baseline_id, delta_seq=0, source_mdb=excluded.source_mdb,
              date_offset_hours=excluded.date_offset_hours, source_max_ids=excluded.source_max_ids,
              updated_at=current_timestamp
            """;
        command.Parameters.AddWithValue(baseline);
        command.Parameters.AddWithValue(sourcePath);
        command.Parameters.AddWithValue(dateOffsetHours);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(targetMax));
        await command.ExecuteNonQueryAsync();
    }

    private static MigrationSchema LoadSchema()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "migration-schema.json");
        return JsonSerializer.Deserialize<MigrationSchema>(
                   File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Не удалось прочитать migration-schema.json.");
    }

    private static void ValidateSchema(MigrationSchema schema)
    {
        foreach (var table in DataTables)
            if (!schema.Tables.ContainsKey(table))
                throw new InvalidOperationException($"В migration-schema.json отсутствует {table}.");
    }

    private static (object Value, NpgsqlDbType Type) ConvertValue(object value, string postgresType, double offset)
    {
        var type = postgresType.ToUpperInvariant();
        if (type.StartsWith("VARCHAR", StringComparison.Ordinal) || type == "TEXT") return (value.ToString() ?? "", NpgsqlDbType.Text);
        if (type == "INTEGER") return (Convert.ToInt32(value, CultureInfo.InvariantCulture), NpgsqlDbType.Integer);
        if (type == "SMALLINT") return (Convert.ToInt16(value, CultureInfo.InvariantCulture), NpgsqlDbType.Smallint);
        if (type == "BIGINT") return (Convert.ToInt64(value, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint);
        if (type == "DOUBLE PRECISION") return (Convert.ToDouble(value, CultureInfo.InvariantCulture), NpgsqlDbType.Double);
        if (type.StartsWith("NUMERIC", StringComparison.Ordinal) || type.StartsWith("DECIMAL", StringComparison.Ordinal))
            return (Convert.ToDecimal(value, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric);
        if (type == "BOOLEAN") return (Convert.ToBoolean(value, CultureInfo.InvariantCulture), NpgsqlDbType.Boolean);
        if (type.StartsWith("TIMESTAMP", StringComparison.Ordinal))
            return (DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture).AddHours(-offset), DateTimeKind.Unspecified), NpgsqlDbType.Timestamp);
        throw new NotSupportedException($"Тип PostgreSQL не поддержан: {postgresType}");
    }

    private static string DistinctSql(string left, string right, IReadOnlyList<MigrationColumn> columns)
    {
        var business = columns.Skip(1).ToArray();
        return $"ROW({string.Join(",", business.Select(c => $"{left}.{Quote(c.Name)}"))}) IS DISTINCT FROM " +
               $"ROW({string.Join(",", business.Select(c => $"{right}.{Quote(c.Name)}"))})";
    }

    private static int ColumnIndex(IReadOnlyList<MigrationColumn> columns, string name)
    {
        for (var index = 0; index < columns.Count; index++)
            if (string.Equals(columns[index].Name, name, StringComparison.OrdinalIgnoreCase)) return index;
        throw new InvalidOperationException($"Колонка {name} не найдена.");
    }

    private static long Threshold(IReadOnlyDictionary<string, long> maxima, string table, int overlap) =>
        Math.Max(0, maxima[table] - overlap);

    private static string Stage(string table) => Quote("delta_" + table.ToLowerInvariant());
    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string QuoteAccess(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(
        NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 0;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private sealed record SourceRow(int Id, object?[] Values);
    private sealed record ChangeCount(long Inserted, long Updated);
    private sealed record SyncState(string BaselineId, long DeltaSeq, double DateOffsetHours);
}

internal sealed record IncrementalSyncResult(
    string PatchId,
    string BaselineId,
    long DeltaSeq,
    long TotalChanges,
    int AffectedPairs,
    TimeSpan Elapsed,
    bool PublishedToHosting);
