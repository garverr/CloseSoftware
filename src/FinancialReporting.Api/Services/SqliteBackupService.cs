using Microsoft.Data.Sqlite;

namespace FinancialReporting.Api.Services;

/// <summary>
/// Nightly SQLite hot-backup. Runs at the configured UTC hour, writes a date-stamped
/// .db file to the configured backup directory using sqlite3_backup() (so it works while
/// the application is up), and retains the last N copies. Cat 47.
///
/// Configuration keys (all optional):
///   Backup:Enabled         — bool, default true.
///   Backup:HourUtc         — int 0-23, hour of day (UTC) to run; default 3.
///   Backup:Directory       — string, output dir (relative or absolute); default "backups".
///   Backup:RetainCopies    — int, count of dated copies to keep; default 14.
///
/// Pair this with a backup of the DataProtection key ring directory; if that is lost,
/// every encrypted Xero token in the backed-up DB becomes unrecoverable.
/// </summary>
public sealed class SqliteBackupService(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<SqliteBackupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Backup:Enabled", true))
        {
            logger.LogInformation("SqliteBackupService disabled via Backup:Enabled=false.");
            return;
        }

        var connectionString = configuration.GetConnectionString("SqliteConnection")
                               ?? "Data Source=financial-reporting-dev.db";
        var sourcePath = ExtractSqliteDataSource(connectionString);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            logger.LogWarning("SqliteBackupService cannot determine the DB file path; backups disabled.");
            return;
        }

        var hourUtc = Math.Clamp(configuration.GetValue("Backup:HourUtc", 3), 0, 23);
        var directory = configuration.GetValue("Backup:Directory", "backups") ?? "backups";
        var retain = Math.Max(1, configuration.GetValue("Backup:RetainCopies", 14));

        // Resolve backup directory relative to the content root if a relative path is given.
        if (!Path.IsPathRooted(directory))
        {
            directory = Path.Combine(environment.ContentRootPath, directory);
        }
        Directory.CreateDirectory(directory);

        // Resolve source path relative to content root if needed.
        if (!Path.IsPathRooted(sourcePath))
        {
            sourcePath = Path.Combine(environment.ContentRootPath, sourcePath);
        }

        logger.LogInformation(
            "SqliteBackupService scheduled: hour={Hour}Z, source={Source}, target={Target}, retain={Retain}.",
            hourUtc, sourcePath, directory, retain);

        // Initial wait: sleep until the next configured hour.
        var initialDelay = ComputeDelayUntilHour(DateTime.UtcNow, hourUtc);
        try
        {
            await Task.Delay(initialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                BackupOnce(sourcePath, directory, retain);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SqliteBackupService failed while writing backup.");
            }

            try
            {
                // Run roughly every 24 hours.
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void BackupOnce(string sourcePath, string directory, int retain)
    {
        if (!File.Exists(sourcePath))
        {
            logger.LogWarning("SqliteBackupService source DB not found at {Path}; skipping.", sourcePath);
            return;
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var sourceFileName = Path.GetFileNameWithoutExtension(sourcePath);
        var targetPath = Path.Combine(directory, $"{sourceFileName}-{stamp}.db");

        using (var source = new SqliteConnection($"Data Source={sourcePath}"))
        using (var target = new SqliteConnection($"Data Source={targetPath}"))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        }

        logger.LogInformation("SqliteBackupService wrote {Target}.", targetPath);

        // Retention: keep the most recent N copies; remove older ones.
        var prefix = $"{sourceFileName}-";
        var existing = Directory.EnumerateFiles(directory, $"{prefix}*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();
        foreach (var stale in existing.Skip(retain))
        {
            try
            {
                stale.Delete();
                logger.LogInformation("SqliteBackupService pruned old backup {Path}.", stale.FullName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SqliteBackupService could not prune {Path}.", stale.FullName);
            }
        }
    }

    private static TimeSpan ComputeDelayUntilHour(DateTime nowUtc, int hourUtc)
    {
        var todayTarget = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, hourUtc, 0, 0, DateTimeKind.Utc);
        var target = todayTarget > nowUtc ? todayTarget : todayTarget.AddDays(1);
        return target - nowUtc;
    }

    private static string ExtractSqliteDataSource(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return builder.DataSource ?? "";
    }
}
