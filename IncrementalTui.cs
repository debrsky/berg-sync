using System.Data.OleDb;
using Npgsql;
using Spectre.Console;

internal static class IncrementalTui
{
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
            Header = new PanelHeader(" Инкрементальная публикация "),
            Border = BoxBorder.Rounded
        });
        AnsiConsole.MarkupLine("[grey]Будут изменены локальная база и база на хостинге. Удаления из MDB не применяются.[/]");
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Запустить инкрементальную публикацию?", defaultValue: false))
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
                ReportStage);

            var summary = new Grid()
                .AddColumn(new GridColumn().NoWrap())
                .AddColumn();
            summary.AddRow("Патч", Markup.Escape(result.PatchId));
            summary.AddRow("Baseline", Markup.Escape(result.BaselineId));
            summary.AddRow("delta_seq", result.DeltaSeq.ToString("N0"));
            summary.AddRow("Изменено строк", result.TotalChanges.ToString("N0"));
            summary.AddRow("Финансовых пар", result.AffectedPairs.ToString("N0"));
            summary.AddRow("Время", result.Elapsed.ToString(@"hh\:mm\:ss"));
            summary.AddRow("Хостинг", result.PublishedToHosting ? "[green]опубликовано[/]" : "[yellow]не опубликовано[/]");

            AnsiConsole.Write(new Panel(summary)
            {
                Header = new PanelHeader(" Успешно "),
                Border = BoxBorder.Double,
                BorderStyle = new Style(Color.Green)
            });
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
