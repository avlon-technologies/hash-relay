using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace HashRelay.Server.Data;

/// <summary>
/// The schema is created with EnsureCreated (no migrations). For databases created by an earlier version, tables that
/// are missing are created from the model's own create script, and columns added later are appended as nullable
/// ALTER TABLEs, so existing SQLite files keep working after an upgrade.
/// </summary>
public static class SchemaUpgrades
{
    private static readonly (string Table, string Column, string Sql)[] Columns =
    [
        ("Notifications", "Markdown", "TEXT NULL"),
        ("Notifications", "ImageUrl", "TEXT NULL"),
        ("Notifications", "Diagram", "TEXT NULL"),
        ("Notifications", "DiagramImageUrl", "TEXT NULL"),
        ("Credentials", "ServiceAccountId", "TEXT NULL"),
    ];

    public static async Task ApplyAsync(RelayDbContext db, CancellationToken ct = default)
    {
        await CreateMissingTablesAsync(db, ct);
        foreach (var group in Columns.GroupBy(c => c.Table))
        {
            var existing = await db.Database.SqlQueryRaw<string>($"SELECT name AS Value FROM pragma_table_info('{group.Key}')").ToListAsync(ct);
            foreach (var (table, column, sql) in group)
            {
                if (existing.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {sql}", ct);
            }
        }
        // Indexes on columns added above (idempotent).
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Credentials_ServiceAccountId\" ON \"Credentials\" (\"ServiceAccountId\")", ct);
    }

    private static async Task CreateMissingTablesAsync(RelayDbContext db, CancellationToken ct)
    {
        var existing = (await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = db.Model.GetEntityTypes().Select(t => t.GetTableName()).OfType<string>().Where(t => !existing.Contains(t)).Distinct().ToList();
        if (missing.Count == 0) return;

        var script = db.GetService<IRelationalDatabaseCreator>().GenerateCreateScript();
        foreach (var statement in script.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var creates = missing.Any(t =>
                statement.StartsWith($"CREATE TABLE \"{t}\"", StringComparison.OrdinalIgnoreCase) ||
                (statement.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) && statement.Contains($" ON \"{t}\" ", StringComparison.OrdinalIgnoreCase)));
            if (creates) await db.Database.ExecuteSqlRawAsync(statement, ct);
        }
    }
}
