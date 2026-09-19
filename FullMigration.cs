using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

internal static class FullMigration
{
    private static readonly string[] OperationScripts =
    [
        "operations/calculate-and-save-operations.sql"
    ];

    private static readonly string[] ViewScripts =
    [
        "views/payers.sql",
        "views/sellers.sql",
        "views/invoices.sql",
        "views/counterparties.sql",
        "views/balances.sql",
        "views/debt_invoices.sql"
    ];

    public static async Task RunAsync(
        OleDbConnection access,
        string baseConnectionString,
        string databaseName,
        string adminDatabase,
        string? sqlDirectory,
        int rowLimit,
        double dateOffsetHours,
        bool confirmDrop,
        string sourcePath,
        string baselineId)
    {
        if (!confirmDrop)
            throw new ArgumentException(
                "Полная миграция удаляет схемы bergauto и bergapp. Добавьте --confirm-drop.");

        var totalTimer = Stopwatch.StartNew();
        var schema = LoadSchema();
        var sqlRoot = ResolveSqlDirectory(sqlDirectory);

        Console.WriteLine("\nПОЛНАЯ МИГРАЦИЯ MDB → PostgreSQL");
        Console.WriteLine($"  база: {databaseName}");
        Console.WriteLine($"  SQL: {sqlRoot}");
        Console.WriteLine($"  лимит таблицы: {(rowLimit == 0 ? "нет" : rowLimit.ToString("N0"))}");
        Console.WriteLine($"  коррекция MDB DateTime: -{dateOffsetHours.ToString(CultureInfo.InvariantCulture)} ч");

        var baseBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = adminDatabase
        };

        await EnsureDatabaseAsync(adminBuilder.ConnectionString, databaseName);

        var targetBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName
        };

        await using var postgres = new NpgsqlConnection(targetBuilder.ConnectionString);
        await postgres.OpenAsync();
        Console.WriteLine($"Подключено: {postgres.Host}/{postgres.Database}");

        await ExecuteAsync(postgres, "CREATE EXTENSION IF NOT EXISTS hstore");
        // Полная миграция создаёт новый baseline; старые номера локальных дельт недействительны.
        await ExecuteAsync(postgres, "DROP SCHEMA IF EXISTS berg_sync CASCADE");
        await ExecuteAsync(postgres, "DROP SCHEMA IF EXISTS bergauto CASCADE");
        await ExecuteAsync(postgres, "CREATE SCHEMA bergauto");
        await ExecuteAsync(postgres, "DROP SCHEMA IF EXISTS bergapp CASCADE");
        await ExecuteAsync(postgres, "CREATE SCHEMA bergapp");
        Console.WriteLine("Схемы bergauto и bergapp пересозданы.");

        await CreateRawTablesAsync(postgres, schema);

        long totalRows = 0;
        foreach (var tableName in schema.TablesOrder)
        {
            if (!schema.Tables.TryGetValue(tableName, out var columns))
                throw new InvalidOperationException($"Нет конфигурации таблицы {tableName}.");

            totalRows += await CopyTableAsync(
                access, postgres, tableName, columns, rowLimit, dateOffsetHours);
        }

        Console.WriteLine("\nСоздание индексов...");
        await ExecuteAsync(postgres, "SET search_path TO bergauto");
        var indexCount = 0;
        foreach (var tableName in schema.TablesOrder)
        {
            if (!schema.Indexes.TryGetValue(tableName, out var indexes))
                continue;

            foreach (var indexSql in indexes)
            {
                await ExecuteAsync(postgres, indexSql);
                indexCount++;
            }
        }
        Console.WriteLine($"Индексов создано: {indexCount}");

        Console.WriteLine("ANALYZE...");
        foreach (var tableName in schema.TablesOrder)
            await ExecuteAsync(postgres, $"ANALYZE bergauto.{QuoteIdentifier(tableName)}");

        // hstore установлен в public; возвращаем стандартный search_path после
        // создания индексов, которые используют короткие имена таблиц bergauto.
        await ExecuteAsync(postgres, "RESET search_path");

        Console.WriteLine("\nСоздание расчётных функций и таблицы операций...");
        await ExecuteBundledScriptAsync(postgres, "sql/get-operations.sql");
        foreach (var script in OperationScripts)
            await ExecuteScriptAsync(postgres, sqlRoot, script);

        Console.WriteLine("Расчёт операций...");
        await ExecuteAsync(postgres, "CALL bergapp.calculate_and_save_operations()");
        var operationCount = await ExecuteScalarInt64Async(
            postgres, "SELECT COUNT(*) FROM bergapp.operations");
        Console.WriteLine($"Операций: {operationCount:N0}");

        Console.WriteLine("\nСоздание materialized views...");
        foreach (var script in ViewScripts)
            await ExecuteScriptAsync(postgres, sqlRoot, script);

        await InitializeSyncStateAsync(
            postgres, schema, baselineId, sourcePath, dateOffsetHours);

        totalTimer.Stop();
        Console.WriteLine("\n============================================================");
        Console.WriteLine("МИГРАЦИЯ ЗАВЕРШЕНА УСПЕШНО");
        Console.WriteLine($"Перенесено строк: {totalRows:N0}");
        Console.WriteLine($"Операций: {operationCount:N0}");
        Console.WriteLine($"Время: {totalTimer.Elapsed}");
        Console.WriteLine("============================================================");
    }

    private static async Task InitializeSyncStateAsync(
        NpgsqlConnection postgres,
        MigrationSchema schema,
        string baselineId,
        string sourcePath,
        double dateOffsetHours)
    {
        Console.WriteLine($"Создание baseline: {baselineId}");
        await ExecuteAsync(postgres, """
            CREATE SCHEMA berg_sync;
            CREATE TABLE berg_sync.state (
              id integer PRIMARY KEY CHECK (id=1),
              baseline_id text NOT NULL,
              delta_seq bigint NOT NULL,
              source_mdb text,
              date_offset_hours double precision NOT NULL,
              source_max_ids jsonb NOT NULL DEFAULT '{}'::jsonb,
              updated_at timestamp with time zone NOT NULL DEFAULT current_timestamp
            );
            CREATE TABLE berg_sync.patches (
              patch_id text PRIMARY KEY,
              baseline_id text NOT NULL,
              delta_seq bigint NOT NULL,
              source_mdb text,
              period_start timestamp without time zone NOT NULL,
              counts jsonb NOT NULL,
              affected_pairs bigint NOT NULL,
              created_at timestamp with time zone NOT NULL DEFAULT current_timestamp
            );
            CREATE TABLE berg_sync.patch_delivery (
              patch_id text NOT NULL,
              target text NOT NULL,
              status text NOT NULL,
              attempts integer NOT NULL DEFAULT 0,
              last_error text,
              applied_at timestamp with time zone,
              PRIMARY KEY (patch_id, target)
            );
            """);

        var maxima = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in new[] { "Customers", "Applications", "XInvoices", "XInvoicePays", "XInvoiceDatas" })
            maxima[table] = await ExecuteScalarInt64Async(
                postgres, $"SELECT COALESCE(MAX(\"ID\"),0) FROM bergauto.{QuoteIdentifier(table)}");

        await using var command = postgres.CreateCommand();
        command.CommandText = """
            INSERT INTO berg_sync.state
              (id,baseline_id,delta_seq,source_mdb,date_offset_hours,source_max_ids)
            VALUES (1,$1,0,$2,$3,$4::jsonb)
            """;
        command.Parameters.AddWithValue(baselineId);
        command.Parameters.AddWithValue(sourcePath);
        command.Parameters.AddWithValue(dateOffsetHours);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(maxima));
        await command.ExecuteNonQueryAsync();
    }

    private static MigrationSchema LoadSchema()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "migration-schema.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Не найден migration-schema.json.", path);

        return JsonSerializer.Deserialize<MigrationSchema>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Не удалось прочитать migration-schema.json.");
    }

    private static string ResolveSqlDirectory(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"Каталог SQL не найден: {fullPath}");
            return fullPath;
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "SQL");
            if (File.Exists(Path.Combine(
                    candidate, "operations", "calculate-and-save-operations.sql")))
                return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Не найден каталог SQL. Укажите его через --sql-dir.");
    }

    private static async Task EnsureDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();

        await using var check = admin.CreateCommand();
        check.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1)";
        check.Parameters.AddWithValue(databaseName);
        var exists = (bool)(await check.ExecuteScalarAsync() ?? false);

        if (!exists)
        {
            await ExecuteAsync(admin, $"CREATE DATABASE {QuoteIdentifier(databaseName)}");
            Console.WriteLine($"Создана база {databaseName}.");
        }

        await ExecuteAsync(
            admin,
            $"ALTER DATABASE {QuoteIdentifier(databaseName)} SET TIMEZONE TO 'Asia/Vladivostok'");
    }

    private static async Task CreateRawTablesAsync(NpgsqlConnection postgres, MigrationSchema schema)
    {
        Console.WriteLine("Создание таблиц bergauto...");
        foreach (var tableName in schema.TablesOrder)
        {
            var columns = schema.Tables[tableName];
            var definitions = columns.Select(column =>
            {
                var definition = $"{QuoteIdentifier(column.Name)} {column.Type}";
                if (column.PK) definition += " PRIMARY KEY";
                if (column.NotNull) definition += " NOT NULL";
                if (!string.IsNullOrWhiteSpace(column.Default))
                {
                    var rawDefault = column.Default == "NOW()" ||
                                     column.Default.Contains("INTERVAL", StringComparison.OrdinalIgnoreCase);
                    var value = rawDefault
                        ? column.Default
                        : $"'{column.Default.Replace("'", "''", StringComparison.Ordinal)}'";
                    definition += $" DEFAULT {value}";
                }
                return definition;
            });

            await ExecuteAsync(
                postgres,
                $"CREATE TABLE bergauto.{QuoteIdentifier(tableName)} ({string.Join(", ", definitions)})");
        }
    }

    private static async Task<long> CopyTableAsync(
        OleDbConnection access,
        NpgsqlConnection postgres,
        string tableName,
        IReadOnlyList<MigrationColumn> columns,
        int rowLimit,
        double dateOffsetHours)
    {
        var availableTables = access.GetSchema("Tables").Rows.Cast<DataRow>()
            .Where(row => string.Equals(row["TABLE_TYPE"]?.ToString(), "TABLE", StringComparison.OrdinalIgnoreCase))
            .Select(row => row["TABLE_NAME"]?.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!availableTables.Contains(tableName))
        {
            Console.WriteLine($"  {tableName,-20} отсутствует в MDB — пропуск");
            return 0;
        }

        var timer = Stopwatch.StartNew();
        using var command = access.CreateCommand();
        var sourceColumns = string.Join(", ", columns.Select(column => QuoteAccessIdentifier(column.Name)));
        command.CommandText = rowLimit == 0
            ? $"SELECT {sourceColumns} FROM {QuoteAccessIdentifier(tableName)}"
            : $"SELECT TOP {rowLimit} {sourceColumns} FROM {QuoteAccessIdentifier(tableName)}";

        using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess)
            ?? throw new InvalidOperationException($"Не удалось прочитать {tableName}.");

        await using var transaction = await postgres.BeginTransactionAsync();
        var targetColumns = string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)));
        long copied = 0;

        await using (var importer = await postgres.BeginBinaryImportAsync(
            $"COPY bergauto.{QuoteIdentifier(tableName)} ({targetColumns}) FROM STDIN (FORMAT BINARY)"))
        {
            while (reader.Read())
            {
                await importer.StartRowAsync();
                for (var index = 0; index < columns.Count; index++)
                {
                    if (reader.IsDBNull(index))
                    {
                        await importer.WriteNullAsync();
                        continue;
                    }

                    var (value, type) = ConvertValue(reader.GetValue(index), columns[index].Type, dateOffsetHours);
                    await importer.WriteAsync(value, type);
                }

                copied++;
                if (copied % 10_000 == 0)
                    Console.Write($"\r  {tableName,-20} {copied,12:N0}");
            }

            await importer.CompleteAsync();
        }

        var count = await ExecuteScalarInt64Async(
            postgres,
            $"SELECT COUNT(*) FROM bergauto.{QuoteIdentifier(tableName)}",
            transaction);

        if (count != copied)
            throw new InvalidOperationException(
                $"Ошибка проверки {tableName}: прочитано {copied}, записано {count}.");

        await transaction.CommitAsync();
        timer.Stop();
        Console.WriteLine($"\r  {tableName,-20} {copied,12:N0} строк  {timer.Elapsed}");
        return copied;
    }

    private static (object Value, NpgsqlDbType Type) ConvertValue(
        object value,
        string postgresType,
        double dateOffsetHours)
    {
        var type = postgresType.ToUpperInvariant();
        if (type.StartsWith("VARCHAR", StringComparison.Ordinal) || type is "TEXT")
            return (value.ToString() ?? string.Empty, NpgsqlDbType.Text);
        if (type is "INTEGER")
            return (Convert.ToInt32(value, CultureInfo.InvariantCulture), NpgsqlDbType.Integer);
        if (type is "SMALLINT")
            return (Convert.ToInt16(value, CultureInfo.InvariantCulture), NpgsqlDbType.Smallint);
        if (type is "BIGINT")
            return (Convert.ToInt64(value, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint);
        if (type is "DOUBLE PRECISION")
            return (Convert.ToDouble(value, CultureInfo.InvariantCulture), NpgsqlDbType.Double);
        if (type.StartsWith("NUMERIC", StringComparison.Ordinal) || type.StartsWith("DECIMAL", StringComparison.Ordinal))
            return (Convert.ToDecimal(value, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric);
        if (type is "BOOLEAN")
            return (Convert.ToBoolean(value, CultureInfo.InvariantCulture), NpgsqlDbType.Boolean);
        if (type.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            var date = Convert.ToDateTime(value, CultureInfo.InvariantCulture).AddHours(-dateOffsetHours);
            return (DateTime.SpecifyKind(date, DateTimeKind.Unspecified), NpgsqlDbType.Timestamp);
        }

        throw new NotSupportedException($"Тип PostgreSQL не поддержан: {postgresType}");
    }

    private static async Task ExecuteBundledScriptAsync(
        NpgsqlConnection connection,
        string relativePath)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException("Встроенный SQL-файл не найден.", path);

        Console.WriteLine($"  → {relativePath} (C# version)");
        await ExecuteAsync(connection, await File.ReadAllTextAsync(path));
    }

    private static async Task ExecuteScriptAsync(
        NpgsqlConnection connection,
        string sqlRoot,
        string relativePath)
    {
        var path = Path.Combine(sqlRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException("SQL-файл не найден.", path);

        Console.WriteLine($"  → {relativePath}");
        await ExecuteAsync(connection, await File.ReadAllTextAsync(path));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarInt64Async(
        NpgsqlConnection connection,
        string sql,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 0;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteAccessIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}

internal sealed class MigrationSchema
{
    public List<string> TablesOrder { get; init; } = [];
    public Dictionary<string, List<MigrationColumn>> Tables { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Indexes { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class MigrationColumn
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool PK { get; init; }
    public bool NotNull { get; init; }
    public string? Default { get; init; }
}
