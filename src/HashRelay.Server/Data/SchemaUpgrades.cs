using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Data;

/// <summary>
/// The schema is created with EnsureCreated (no migrations). Columns added after a database was first created are
/// added here as nullable ALTER TABLEs so existing SQLite files keep working after an upgrade.
/// </summary>
public static class SchemaUpgrades
{
    private static readonly (string Table, string Column, string Sql)[] Columns =
    [
        ("Notifications", "Markdown", "TEXT NULL"),
        ("Notifications", "ImageUrl", "TEXT NULL"),
        ("Notifications", "Diagram", "TEXT NULL"),
        ("Notifications", "DiagramImageUrl", "TEXT NULL"),
    ];

    public static async Task ApplyAsync(RelayDbContext db, CancellationToken ct = default)
    {
        foreach (var group in Columns.GroupBy(c => c.Table))
        {
            var existing = await db.Database.SqlQueryRaw<string>($"SELECT name AS Value FROM pragma_table_info('{group.Key}')").ToListAsync(ct);
            foreach (var (table, column, sql) in group)
            {
                if (existing.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {sql}", ct);
            }
        }
    }
}
