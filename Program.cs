using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;

Console.OutputEncoding = Encoding.UTF8;
using var failureLog = new FailureLog();

try
{
    EnvironmentFile.Load();
    var options = Options.Parse(args);
    failureLog.AddConnectionString(options.PgConnectionString);
    if (options.ShowHelp)
    {
        Options.PrintHelp();
        return 0;
    }

    // Keep the OS file handle open across every await, including TUI and hosting publication.
    using var runLock = RunLock.Acquire();

    var configuredMdbPath = options.Tui
        ? Environment.GetEnvironmentVariable("MDB_PATH")
        : !string.IsNullOrWhiteSpace(options.MdbPath)
            ? options.MdbPath
            : Environment.GetEnvironmentVariable("MDB_PATH");
    if (string.IsNullOrWhiteSpace(configuredMdbPath))
        throw new ArgumentException(
            "Укажите путь к MDB через --mdb <путь> или переменную MDB_PATH.");

    var mdbPath = Path.GetFullPath(configuredMdbPath);
    if (!File.Exists(mdbPath))
        throw new FileNotFoundException("MDB-файл не найден.", mdbPath);

    using var connection = OpenAccessDatabase(mdbPath, options.Provider);

    if (options.Tui)
    {
        var result = await SyncTui.RunAsync(
            connection, mdbPath, ResolvePostgresConnectionString(options), options, failureLog.Record);
        if (result != 0) failureLog.Save();
        return result;
    }

    Console.WriteLine($"MDB: {mdbPath}");
    Console.WriteLine($"Размер: {new FileInfo(mdbPath).Length:N0} байт");
    Console.WriteLine($"Провайдер: {connection.Provider}");
    Console.WriteLine("Подключение: OK\n");

    if (options.FullMigration)
    {
        var baselineId = $"{DateTime.Now:yyyy-MM-dd-HHmmss}-full";
        await FullMigration.RunAsync(
            connection,
            ResolvePostgresConnectionString(options),
            options.PgDatabase,
            options.PgAdminDatabase,
            options.SqlDirectory,
            options.MigrationLimit,
            options.MdbDateOffsetHours,
            options.ConfirmDrop,
            mdbPath,
            baselineId);

        if (options.PublishHosting)
            await HostingPublisher.RunAsync(
                ResolvePostgresConnectionString(options),
                options.PgDatabase,
                options.ConfirmHostingRestore,
                options.TransferWorkDirectory,
                options.TransferRetries,
                options.ZstdLevel);
        return 0;
    }

    if (options.PublishHosting)
    {
        await HostingPublisher.RunAsync(
            ResolvePostgresConnectionString(options),
            options.PgDatabase,
            options.ConfirmHostingRestore,
            options.TransferWorkDirectory,
            options.TransferRetries,
            options.ZstdLevel);
        return 0;
    }

    if (options.Incremental)
    {
        await IncrementalSync.RunAsync(
            connection,
            ResolvePostgresConnectionString(options),
            options.PgDatabase,
            options.PeriodDays,
            options.IdOverlap,
            options.PaymentIdOverlap,
            options.InvoiceDataIdOverlap,
            options.FullCustomers,
            options.MdbDateOffsetHours,
            options.DeltaMaxChanges,
            !options.SkipRecalculate,
            !options.SkipRefreshViews,
            mdbPath,
            options.SyncHosting,
            options.DeltaPackageDirectory);
        return 0;
    }

    var tables = GetUserTables(connection);
    Console.WriteLine($"Пользовательских таблиц: {tables.Count}");

    foreach (var table in tables)
        Console.WriteLine($"  {table}");

    if (options.Table is null)
    {
        if (options.CopyToPostgres)
            throw new ArgumentException("Для переноса обязательно укажите --table <имя>.");

        Console.WriteLine("\nДля проверки структуры и данных добавьте --table <имя>.");
        return 0;
    }

    var actualTable = tables.FirstOrDefault(
        name => string.Equals(name, options.Table, StringComparison.OrdinalIgnoreCase));

    if (actualTable is null)
        throw new ArgumentException($"Таблица '{options.Table}' не найдена в MDB.");

    var columns = GetColumns(connection, actualTable);
    Console.WriteLine($"\nТаблица: {actualTable}");
    PrintStructure(columns);

    if (options.CountRows)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM {QuoteAccessIdentifier(actualTable)}";
        var count = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        Console.WriteLine($"\nСтрок в MDB: {count:N0}");
    }

    PrintSample(connection, actualTable, options.Limit);

    if (options.CopyToPostgres)
    {
        var pgConnectionString = ResolvePostgresConnectionString(options);
        await CopyToPostgres(
            connection,
            actualTable,
            columns,
            pgConnectionString,
            options.PgSchema,
            options.PgTable ?? actualTable,
            options.CopyLimit,
            options.Replace);
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Ошибка: {exception.Message}");
    failureLog.Save(exception);

    if (exception.Message.Contains("provider is not registered", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("поставщик не зарегистрирован", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            "Проверьте установку Microsoft Access Database Engine и совпадение " +
            "разрядности ACE с PlatformTarget проекта.");
    }

    return 1;
}

static OleDbConnection OpenAccessDatabase(string path, string? requestedProvider)
{
    var providers = requestedProvider is null
        ? new[] { "Microsoft.ACE.OLEDB.16.0", "Microsoft.ACE.OLEDB.12.0" }
        : new[] { requestedProvider };

    var errors = new List<string>();

    foreach (var provider in providers)
    {
        var connection = new OleDbConnection(
            $"Provider={provider};Data Source={path};Persist Security Info=False;");

        try
        {
            connection.Open();
            return connection;
        }
        catch (Exception exception) when (exception is OleDbException or InvalidOperationException)
        {
            errors.Add($"{provider}: {exception.Message}");
            connection.Dispose();
        }
    }

    throw new InvalidOperationException(
        "Не удалось открыть MDB ни одним из провайдеров:\n  " +
        string.Join("\n  ", errors));
}

static List<string> GetUserTables(OleDbConnection connection)
{
    var schema = connection.GetSchema("Tables");

    return schema.Rows.Cast<DataRow>()
        .Where(row => string.Equals(row["TABLE_TYPE"]?.ToString(), "TABLE", StringComparison.OrdinalIgnoreCase))
        .Select(row => row["TABLE_NAME"]?.ToString())
        .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
        .Select(name => name!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<ColumnDefinition> GetColumns(OleDbConnection connection, string table)
{
    using var command = connection.CreateCommand();
    command.CommandText = $"SELECT * FROM {QuoteAccessIdentifier(table)}";

    using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly)
        ?? throw new InvalidOperationException("Не удалось прочитать структуру таблицы.");

    var schema = reader.GetSchemaTable()
        ?? throw new InvalidOperationException("Провайдер не вернул структуру таблицы.");

    return schema.Rows.Cast<DataRow>()
        .Select(row => new ColumnDefinition(
            row["ColumnName"].ToString()!,
            row["DataType"] as Type ?? typeof(string),
            row.Table.Columns.Contains("AllowDBNull") && row["AllowDBNull"] is true,
            row.Table.Columns.Contains("ColumnSize") && row["ColumnSize"] is int size ? size : null))
        .ToList();
}

static void PrintStructure(IReadOnlyList<ColumnDefinition> columns)
{
    Console.WriteLine("Структура:");
    foreach (var column in columns)
        Console.WriteLine(
            $"  {column.Name,-28} {column.ClrType.Name,-14} " +
            $"size={column.Size,-8} nullable={column.Nullable} → {column.PostgresType}");
}

static void PrintSample(OleDbConnection connection, string table, int limit)
{
    if (limit == 0)
        return;

    using var command = connection.CreateCommand();
    command.CommandText = $"SELECT TOP {limit} * FROM {QuoteAccessIdentifier(table)}";

    using var reader = command.ExecuteReader()
        ?? throw new InvalidOperationException("Не удалось прочитать данные таблицы.");

    Console.WriteLine($"\nПервые строки (не более {limit}):");
    var rowNumber = 0;

    while (reader.Read())
    {
        rowNumber++;
        Console.WriteLine($"--- строка {rowNumber} ---");

        for (var index = 0; index < reader.FieldCount; index++)
        {
            var value = reader.IsDBNull(index) ? "NULL" : FormatValue(reader.GetValue(index));
            Console.WriteLine($"  {reader.GetName(index)} = {value}");
        }
    }

    if (rowNumber == 0)
        Console.WriteLine("  <таблица пуста>");
}

static async Task CopyToPostgres(
    OleDbConnection accessConnection,
    string sourceTable,
    IReadOnlyList<ColumnDefinition> columns,
    string connectionString,
    string targetSchema,
    string targetTable,
    int copyLimit,
    bool replace)
{
    Console.WriteLine("\nПеренос в PostgreSQL:");
    Console.WriteLine($"  назначение: {targetSchema}.{targetTable}");
    Console.WriteLine($"  лимит: {(copyLimit == 0 ? "вся таблица" : copyLimit.ToString("N0", CultureInfo.CurrentCulture))}");

    await using var postgres = new NpgsqlConnection(connectionString);
    await postgres.OpenAsync();
    Console.WriteLine($"  сервер: {postgres.Host}/{postgres.Database}");

    await using var transaction = await postgres.BeginTransactionAsync();

    var quotedSchema = QuotePostgresIdentifier(targetSchema);
    var quotedTable = QuotePostgresIdentifier(targetTable);
    var qualifiedTable = $"{quotedSchema}.{quotedTable}";

    await using (var command = postgres.CreateCommand())
    {
        command.Transaction = transaction;
        command.CommandText = $"CREATE SCHEMA IF NOT EXISTS {quotedSchema}";
        await command.ExecuteNonQueryAsync();

        if (replace)
        {
            command.CommandText = $"DROP TABLE IF EXISTS {qualifiedTable}";
            await command.ExecuteNonQueryAsync();
        }

        var definitions = string.Join(", ", columns.Select(column =>
            $"{QuotePostgresIdentifier(column.Name)} {column.PostgresType}"));
        command.CommandText = $"CREATE TABLE {qualifiedTable} ({definitions})";
        await command.ExecuteNonQueryAsync();
    }

    using var sourceCommand = accessConnection.CreateCommand();
    sourceCommand.CommandText = copyLimit == 0
        ? $"SELECT * FROM {QuoteAccessIdentifier(sourceTable)}"
        : $"SELECT TOP {copyLimit} * FROM {QuoteAccessIdentifier(sourceTable)}";

    using var reader = sourceCommand.ExecuteReader(CommandBehavior.SequentialAccess)
        ?? throw new InvalidOperationException("Не удалось прочитать исходную таблицу.");

    var columnList = string.Join(", ", columns.Select(column => QuotePostgresIdentifier(column.Name)));
    long copied = 0;

    await using (var importer = await postgres.BeginBinaryImportAsync(
        $"COPY {qualifiedTable} ({columnList}) FROM STDIN (FORMAT BINARY)"))
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

                var value = NormalizeValue(reader.GetValue(index), columns[index]);
                await importer.WriteAsync(value, columns[index].NpgsqlType);
            }

            copied++;
            if (copied % 10_000 == 0)
                Console.Write($"\r  перенесено: {copied:N0}");
        }

        await importer.CompleteAsync();
    }

    long destinationCount;
    await using (var verifyCommand = postgres.CreateCommand())
    {
        verifyCommand.Transaction = transaction;
        verifyCommand.CommandText = $"SELECT COUNT(*) FROM {qualifiedTable}";
        destinationCount = Convert.ToInt64(await verifyCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    if (destinationCount != copied)
        throw new InvalidOperationException(
            $"Проверка не пройдена: прочитано {copied}, в PostgreSQL {destinationCount}.");

    await transaction.CommitAsync();
    Console.WriteLine($"\r  перенесено: {copied:N0}");
    Console.WriteLine($"  проверка COUNT(*): {destinationCount:N0} — OK");
    Console.WriteLine("Перенос завершён.");
}

static object NormalizeValue(object value, ColumnDefinition column) => column.NpgsqlType switch
{
    NpgsqlDbType.Smallint => Convert.ToInt16(value, CultureInfo.InvariantCulture),
    NpgsqlDbType.Integer => Convert.ToInt32(value, CultureInfo.InvariantCulture),
    NpgsqlDbType.Bigint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    NpgsqlDbType.Real => Convert.ToSingle(value, CultureInfo.InvariantCulture),
    NpgsqlDbType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    NpgsqlDbType.Numeric => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    NpgsqlDbType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
    NpgsqlDbType.Timestamp => DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Unspecified),
    NpgsqlDbType.Uuid => value is Guid guid ? guid : Guid.Parse(value.ToString()!),
    NpgsqlDbType.Bytea => (byte[])value,
    _ => value.ToString() ?? string.Empty
};

static string ResolvePostgresConnectionString(Options options)
{
    if (!string.IsNullOrWhiteSpace(options.PgConnectionString))
        return options.PgConnectionString;

    var fromEnvironment = Environment.GetEnvironmentVariable("PG_LOCAL_CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(fromEnvironment))
        return fromEnvironment;

    throw new ArgumentException(
        "Для переноса задайте строку PostgreSQL через --pg или переменную PG_LOCAL_CONNECTION_STRING.");
}

static string QuoteAccessIdentifier(string identifier) =>
    $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

static string QuotePostgresIdentifier(string identifier) =>
    $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

static string FormatValue(object value)
{
    const int maxLength = 160;

    var text = value switch
    {
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        byte[] bytes => $"byte[{bytes.Length}] {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 24)))}",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    text = text.Replace("\r", "\\r", StringComparison.Ordinal)
               .Replace("\n", "\\n", StringComparison.Ordinal);

    return text.Length <= maxLength ? text : text[..maxLength] + "…";
}

sealed record ColumnDefinition(string Name, Type ClrType, bool Nullable, int? Size)
{
    public string PostgresType => ClrType switch
    {
        var type when type == typeof(byte) => "smallint",
        var type when type == typeof(short) => "smallint",
        var type when type == typeof(int) => "integer",
        var type when type == typeof(long) => "bigint",
        var type when type == typeof(float) => "real",
        var type when type == typeof(double) => "double precision",
        var type when type == typeof(decimal) => "numeric",
        var type when type == typeof(bool) => "boolean",
        var type when type == typeof(DateTime) => "timestamp without time zone",
        var type when type == typeof(Guid) => "uuid",
        var type when type == typeof(byte[]) => "bytea",
        _ => "text"
    };

    public NpgsqlDbType NpgsqlType => ClrType switch
    {
        var type when type == typeof(byte) => NpgsqlDbType.Smallint,
        var type when type == typeof(short) => NpgsqlDbType.Smallint,
        var type when type == typeof(int) => NpgsqlDbType.Integer,
        var type when type == typeof(long) => NpgsqlDbType.Bigint,
        var type when type == typeof(float) => NpgsqlDbType.Real,
        var type when type == typeof(double) => NpgsqlDbType.Double,
        var type when type == typeof(decimal) => NpgsqlDbType.Numeric,
        var type when type == typeof(bool) => NpgsqlDbType.Boolean,
        var type when type == typeof(DateTime) => NpgsqlDbType.Timestamp,
        var type when type == typeof(Guid) => NpgsqlDbType.Uuid,
        var type when type == typeof(byte[]) => NpgsqlDbType.Bytea,
        _ => NpgsqlDbType.Text
    };
}

sealed record Options(
    string? MdbPath,
    string? Table,
    int Limit,
    bool CountRows,
    string? Provider,
    bool CopyToPostgres,
    string? PgConnectionString,
    string PgSchema,
    string? PgTable,
    int CopyLimit,
    bool Replace,
    bool FullMigration,
    bool Incremental,
    bool SyncHosting,
    string? DeltaPackageDirectory,
    bool PublishHosting,
    bool ConfirmHostingRestore,
    string? TransferWorkDirectory,
    int TransferRetries,
    int ZstdLevel,
    string PgDatabase,
    string PgAdminDatabase,
    string? SqlDirectory,
    int MigrationLimit,
    double MdbDateOffsetHours,
    bool ConfirmDrop,
    int PeriodDays,
    int IdOverlap,
    int PaymentIdOverlap,
    int InvoiceDataIdOverlap,
    bool FullCustomers,
    int DeltaMaxChanges,
    bool SkipRecalculate,
    bool SkipRefreshViews,
    bool Tui,
    bool ShowHelp)
{
    public static Options Parse(string[] args)
    {
        string? mdbPath = null;
        string? table = null;
        string? provider = null;
        string? pgConnectionString = null;
        string? pgTable = null;
        var pgSchema = "mdb_poc";
        var limit = 3;
        var copyLimit = 1000;
        var countRows = false;
        var copyToPostgres = false;
        var replace = false;
        var fullMigration = false;
        var incremental = false;
        var syncHosting = false;
        string? deltaPackageDirectory = null;
        var publishHosting = false;
        var confirmHostingRestore = false;
        string? transferWorkDirectory = null;
        var transferRetries = 20;
        var zstdLevel = 17;
        var pgDatabase = "bergdb";
        var pgAdminDatabase = "postgres";
        string? sqlDirectory = null;
        var migrationLimit = 0;
        var mdbDateOffsetHours = 0d;
        var confirmDrop = false;
        var periodDays = 90;
        var idOverlap = 10_000;
        var paymentIdOverlap = 50_000;
        var invoiceDataIdOverlap = 20_000;
        var fullCustomers = false;
        var deltaMaxChanges = 100_000;
        var skipRecalculate = false;
        var skipRefreshViews = false;
        var tui = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--mdb": mdbPath = NextValue(args, ref index, "--mdb"); break;
                case "--table": table = NextValue(args, ref index, "--table"); break;
                case "--limit": limit = ParseLimit(NextValue(args, ref index, "--limit"), "--limit", 100); break;
                case "--count": countRows = true; break;
                case "--provider": provider = NextValue(args, ref index, "--provider"); break;
                case "--copy": copyToPostgres = true; break;
                case "--pg": pgConnectionString = NextValue(args, ref index, "--pg"); break;
                case "--pg-schema": pgSchema = NextValue(args, ref index, "--pg-schema"); break;
                case "--pg-table": pgTable = NextValue(args, ref index, "--pg-table"); break;
                case "--copy-limit": copyLimit = ParseLimit(NextValue(args, ref index, "--copy-limit"), "--copy-limit", int.MaxValue); break;
                case "--replace": replace = true; break;
                case "--migrate": fullMigration = true; break;
                case "--incremental" or "--delta": incremental = true; break;
                case "--sync-hosting": syncHosting = true; break;
                case "--delta-package-dir": deltaPackageDirectory = NextValue(args, ref index, "--delta-package-dir"); break;
                case "--publish-hosting": publishHosting = true; break;
                case "--confirm-hosting-restore": confirmHostingRestore = true; break;
                case "--transfer-work-dir": transferWorkDirectory = NextValue(args, ref index, "--transfer-work-dir"); break;
                case "--transfer-retries": transferRetries = ParsePositive(NextValue(args, ref index, "--transfer-retries"), "--transfer-retries"); break;
                case "--zstd-level": zstdLevel = ParseRange(NextValue(args, ref index, "--zstd-level"), "--zstd-level", 1, 22); break;
                case "--pg-database": pgDatabase = NextValue(args, ref index, "--pg-database"); break;
                case "--pg-admin-database": pgAdminDatabase = NextValue(args, ref index, "--pg-admin-database"); break;
                case "--sql-dir": sqlDirectory = NextValue(args, ref index, "--sql-dir"); break;
                case "--migration-limit": migrationLimit = ParseLimit(NextValue(args, ref index, "--migration-limit"), "--migration-limit", int.MaxValue); break;
                case "--mdb-date-offset-hours":
                    if (!double.TryParse(NextValue(args, ref index, "--mdb-date-offset-hours"), NumberStyles.Float, CultureInfo.InvariantCulture, out mdbDateOffsetHours))
                        throw new ArgumentException("--mdb-date-offset-hours должен быть числом.");
                    break;
                case "--confirm-drop": confirmDrop = true; break;
                case "--period-days": periodDays = ParsePositive(NextValue(args, ref index, "--period-days"), "--period-days"); break;
                case "--id-overlap": idOverlap = ParsePositive(NextValue(args, ref index, "--id-overlap"), "--id-overlap", true); break;
                case "--payment-id-overlap": paymentIdOverlap = ParsePositive(NextValue(args, ref index, "--payment-id-overlap"), "--payment-id-overlap", true); break;
                case "--invoice-data-id-overlap": invoiceDataIdOverlap = ParsePositive(NextValue(args, ref index, "--invoice-data-id-overlap"), "--invoice-data-id-overlap", true); break;
                case "--full-customers": fullCustomers = true; break;
                case "--delta-max-changes": deltaMaxChanges = ParsePositive(NextValue(args, ref index, "--delta-max-changes"), "--delta-max-changes"); break;
                case "--skip-recalculate": skipRecalculate = true; break;
                case "--skip-refresh-views": skipRefreshViews = true; break;
                case "--tui": tui = true; break;
                case "--help" or "-h": showHelp = true; break;
                default: throw new ArgumentException($"Неизвестный аргумент: {args[index]}");
            }
        }

        var selectedModes = (fullMigration ? 1 : 0) + (incremental ? 1 : 0) + (copyToPostgres ? 1 : 0);
        if (selectedModes > 1)
            throw new ArgumentException("Используйте только один режим: --migrate, --incremental или --copy.");
        if (publishHosting && (incremental || copyToPostgres))
            throw new ArgumentException("--publish-hosting совместим только с --migrate или отдельным запуском.");
        if (syncHosting && !incremental)
            throw new ArgumentException("--sync-hosting используется вместе с --incremental.");
        if (tui && (fullMigration || incremental || copyToPostgres || publishHosting || syncHosting))
            throw new ArgumentException("--tui запускается отдельно; режим публикации выбирается в интерфейсе.");
        if (tui && !string.IsNullOrWhiteSpace(mdbPath))
            throw new ArgumentException("--tui использует только MDB_PATH; не задавайте --mdb.");

        return new Options(mdbPath, table, limit, countRows, provider, copyToPostgres,
            pgConnectionString, pgSchema, pgTable, copyLimit, replace, fullMigration,
            incremental, syncHosting, deltaPackageDirectory,
            publishHosting, confirmHostingRestore, transferWorkDirectory,
            transferRetries, zstdLevel, pgDatabase, pgAdminDatabase, sqlDirectory, migrationLimit,
            mdbDateOffsetHours, confirmDrop, periodDays, idOverlap, paymentIdOverlap,
            invoiceDataIdOverlap, fullCustomers, deltaMaxChanges, skipRecalculate,
            skipRefreshViews, tui, showHelp);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            Berg Sync: чтение MDB и миграция в PostgreSQL.

            Использование:
              dotnet run --project . -- [--mdb <файл.mdb>] [параметры]

            Источник MDB:
              --mdb <путь>        Путь к MDB; имеет приоритет над MDB_PATH из окружения

            Чтение MDB:
              --table <имя>       Выбранная таблица
              --limit <0..100>    Число примеров строк, по умолчанию 3
              --count             Выполнить COUNT(*) в MDB
              --provider <имя>    Явно указать ACE-провайдер

            Перенос PostgreSQL:
              --copy              Включить перенос выбранной таблицы
              --pg <строка>       Строка подключения (или PG_LOCAL_CONNECTION_STRING)
              --pg-schema <имя>   Целевая схема, по умолчанию mdb_poc
              --pg-table <имя>    Целевая таблица, по умолчанию имя MDB-таблицы
              --copy-limit <N>    Число переносимых строк; 0 = все, по умолчанию 1000
              --replace           Удалить существующую целевую таблицу

            Полная миграция (аналог migrate.js):
              --migrate                  Запустить полную миграцию
              --pg-database <имя>        Целевая база, по умолчанию bergdb
              --pg-admin-database <имя>  Сервисная база, по умолчанию postgres
              --sql-dir <путь>           Каталог SQL; обычно находится автоматически
              --migration-limit <N>      Лимит строк каждой таблицы; 0 = все
              --mdb-date-offset-hours N  Вычесть часов из MDB-дат; по умолчанию 0
              --confirm-drop             Подтвердить удаление bergauto и bergapp

            Полная публикация на хостинг:
              --publish-hosting          Создать dump, загрузить по SFTP и восстановить
              --confirm-hosting-restore  Подтвердить замену схем на хостинге
              --transfer-work-dir <путь> Рабочий каталог; по умолчанию системный TEMP
              --transfer-retries <N>     Попытки SFTP-докачки, по умолчанию 20
              --zstd-level <1..22>       Уровень сжатия, по умолчанию 17

            Интерактивный режим:
              --tui                      Выбрать инкрементальную или полную публикацию
                                         (по умолчанию инкрементальная; MDB из MDB_PATH)

            Инкрементальная выгрузка в локальный PostgreSQL:
              --incremental, --delta     Применить безопасную INSERT/UPDATE-дельту
              --period-days <N>          Скользящий период, по умолчанию 90
              --id-overlap <N>           Общее перекрытие ID, по умолчанию 10000
              --payment-id-overlap <N>   Перекрытие платежей, по умолчанию 50000
              --invoice-data-id-overlap <N> Перекрытие позиций, по умолчанию 20000
              --full-customers           Полностью сравнить Customers
              --delta-max-changes <N>    Защитный лимит, по умолчанию 100000
              --skip-recalculate         Не пересчитывать bergapp.operations
              --skip-refresh-views       Не обновлять materialized views
              --sync-hosting             Доставить тот же пакет на хостинг
              --delta-package-dir <путь> Каталог неизменяемых delta-пакетов

              --help, -h          Показать справку
            """);
    }

    private static int ParseLimit(string value, string option, int maximum)
    {
        if (!int.TryParse(value, out var result) || result < 0 || result > maximum)
            throw new ArgumentException($"{option} должен быть числом от 0 до {maximum}.");
        return result;
    }

    private static int ParsePositive(string value, string option, bool allowZero = false)
    {
        if (!int.TryParse(value, out var result) || (allowZero ? result < 0 : result <= 0))
            throw new ArgumentException($"{option} должен быть {(allowZero ? "неотрицательным" : "положительным")} целым числом.");
        return result;
    }

    private static int ParseRange(string value, string option, int minimum, int maximum)
    {
        if (!int.TryParse(value, out var result) || result < minimum || result > maximum)
            throw new ArgumentException($"{option} должен быть целым числом от {minimum} до {maximum}.");
        return result;
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"Для {option} не указано значение.");
        return args[index];
    }
}
