using System.Data.OleDb;
using System.Diagnostics;
using Npgsql;
using Spectre.Console;

internal static class SyncTui
{
    private const string IncrementalMode = "Инкрементальная публикация";
    private const string FullMode = "Полная миграция и публикация";

    public static async Task<int> RunAsync(
        OleDbConnection access,
        string mdbPath,
        string localConnectionString,
        Options options)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
            throw new InvalidOperationException("TUI требует интерактивный терминал.");

        var hostingConnectionString = Environment.GetEnvironmentVariable("PG_HOSTING_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(hostingConnectionString))
            throw new ArgumentException("Для TUI задайте PG_HOSTING_CONNECTION_STRING.");

        var local = new NpgsqlConnectionStringBuilder(localConnectionString)
        {
            Database = options.PgDatabase
        };
        var hosting = new NpgsqlConnectionStringBuilder(hostingConnectionString);

        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("Berg Sync").Color(Color.Blue));
        AnsiConsole.Write(new Panel(BuildTargetGrid(mdbPath, access.Provider, local, hosting))
        {
            Header = new PanelHeader(" Параметры публикации "),
            Border = BoxBorder.Rounded
        });

        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Выберите режим:")
                .HighlightStyle(new Style(Color.Blue, decoration: Decoration.Bold))
                .AddChoices(IncrementalMode, FullMode));

        if (mode == FullMode)
            ValidateFullPublishingConfiguration();

        PrintWarning(mode);
        AnsiConsole.WriteLine();
        if (!ConfirmRun(mode, mdbPath))
        {
            AnsiConsole.MarkupLine("[yellow]Запуск отменён.[/]");
            return 0;
        }

        AnsiConsole.Write(new Rule("[blue]Ход выполнения[/]").LeftJustified());
        var currentStage = "Подготовка";
        void ReportStage(string stage)
        {
            currentStage = stage;
            AnsiConsole.MarkupLine($"[blue]▶[/] [bold]{Markup.Escape(stage)}[/]");
        }

        try
        {
            if (mode == FullMode)
                await RunFullAsync(access, mdbPath, localConnectionString, options, ReportStage);
            else
                await RunIncrementalAsync(access, mdbPath, localConnectionString, options, ReportStage);
            return 0;
        }
        catch (Exception exception)
        {
            var message = $"[bold]Этап:[/] {Markup.Escape(currentStage)}\n" +
                          $"[bold]Ошибка:[/] {Markup.Escape(exception.Message)}";
            AnsiConsole.Write(new Panel(message)
            {
                Header = new PanelHeader(" Ошибка публикации "),
                Border = BoxBorder.Double,
                BorderStyle = new Style(Color.Red)
            });
            return 1;
        }
    }

    private static async Task RunIncrementalAsync(
        OleDbConnection access,
        string mdbPath,
        string localConnectionString,
        Options options,
        Action<string> reportStage)
    {
        var result = await IncrementalSync.RunAsync(
            access,
            localConnectionString,
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
            syncHosting: true,
            options.DeltaPackageDirectory,
            reportStage);

        var summary = CreateSummaryGrid();
        summary.AddRow("Режим", IncrementalMode);
        summary.AddRow("Патч", Markup.Escape(result.PatchId));
        summary.AddRow("Baseline", Markup.Escape(result.BaselineId));
        summary.AddRow("delta_seq", result.DeltaSeq.ToString("N0"));
        summary.AddRow("Изменено строк", result.TotalChanges.ToString("N0"));
        summary.AddRow("Финансовых пар", result.AffectedPairs.ToString("N0"));
        summary.AddRow("Время", result.Elapsed.ToString(@"hh\:mm\:ss"));
        summary.AddRow("Хостинг", result.PublishedToHosting ? "[green]опубликовано[/]" : "[yellow]не опубликовано[/]");
        WriteSuccess(summary);
    }

    private static async Task RunFullAsync(
        OleDbConnection access,
        string mdbPath,
        string localConnectionString,
        Options options,
        Action<string> reportStage)
    {
        var timer = Stopwatch.StartNew();
        var baselineId = $"{DateTime.Now:yyyy-MM-dd-HHmmss}-full";

        reportStage("Полная миграция в локальную PostgreSQL");
        await FullMigration.RunAsync(
            access,
            localConnectionString,
            options.PgDatabase,
            options.PgAdminDatabase,
            options.SqlDirectory,
            options.MigrationLimit,
            options.MdbDateOffsetHours,
            confirmDrop: true,
            mdbPath,
            baselineId);

        reportStage("Полная публикация на хостинг");
        await HostingPublisher.RunAsync(
            localConnectionString,
            options.PgDatabase,
            confirmRestore: true,
            options.TransferWorkDirectory,
            options.TransferRetries,
            options.ZstdLevel);

        timer.Stop();
        reportStage("Завершено");
        var summary = CreateSummaryGrid();
        summary.AddRow("Режим", FullMode);
        summary.AddRow("Baseline", Markup.Escape(baselineId));
        summary.AddRow("delta_seq", "0");
        summary.AddRow("Время", timer.Elapsed.ToString(@"hh\:mm\:ss"));
        summary.AddRow("Хостинг", "[green]опубликовано и проверено[/]");
        WriteSuccess(summary);
    }

    private static void PrintWarning(string mode)
    {
        if (mode == FullMode)
        {
            AnsiConsole.MarkupLine(
                "[red bold]Внимание:[/] схемы [bold]bergauto[/], [bold]bergapp[/] и [bold]berg_sync[/] " +
                "будут пересозданы локально и заменены на хостинге.");
            return;
        }

        AnsiConsole.MarkupLine(
            "[grey]Будут применены INSERT/UPDATE локально и на хостинге. Удаления из MDB не применяются.[/]");
    }

    private static bool ConfirmRun(string mode, string mdbPath)
    {
        var escapedPath = Markup.Escape(mdbPath);
        return mode == FullMode
            ? AnsiConsole.Confirm(
                $"Полностью заменить локальную базу и хостинг данными из [yellow]{escapedPath}[/]?",
                defaultValue: false)
            : AnsiConsole.Confirm("Запустить инкрементальную публикацию?", defaultValue: false);
    }

    private static void ValidateFullPublishingConfiguration()
    {
        foreach (var name in new[] { "HOSTING_SSH_TARGET", "HOSTING_TRANSFER_DIR", "HOSTING_RESTORE_SCRIPT" })
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                throw new ArgumentException($"Для полной публикации задайте {name}.");
    }

    private static Grid CreateSummaryGrid() => new Grid()
        .AddColumn(new GridColumn().NoWrap())
        .AddColumn();

    private static void WriteSuccess(Grid summary) =>
        AnsiConsole.Write(new Panel(summary)
        {
            Header = new PanelHeader(" Успешно "),
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Green)
        });

    private static Grid BuildTargetGrid(
        string mdbPath,
        string provider,
        NpgsqlConnectionStringBuilder local,
        NpgsqlConnectionStringBuilder hosting)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().RightAligned())
            .AddColumn();
        grid.AddRow("MDB", Markup.Escape(mdbPath));
        grid.AddRow("ACE", Markup.Escape(provider));
        grid.AddRow("Локальная БД", Markup.Escape(Endpoint(local)));
        grid.AddRow("Хостинг", Markup.Escape(Endpoint(hosting)));
        return grid;
    }

    private static string Endpoint(NpgsqlConnectionStringBuilder builder)
    {
        var port = builder.Port > 0 ? $":{builder.Port}" : string.Empty;
        return $"{builder.Host}{port}/{builder.Database}";
    }
}
