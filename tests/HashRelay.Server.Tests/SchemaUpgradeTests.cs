using HashRelay.Server.Data;
using HashRelay.Server.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>A database file written by the previous release gains the attribution columns on start-up, without migrations.</summary>
public sealed class SchemaUpgradeTests
{
    private static readonly (string Table, string Column)[] Added =
    [
        ("Notifications", "PublisherId"), ("Notifications", "PublisherName"), ("Notifications", "PublisherType"), ("Notifications", "Channel"),
        ("Credentials", "PublisherType"), ("ServiceAccounts", "PublisherType"),
    ];

    [Fact]
    public async Task Attribution_columns_are_added_to_an_older_database_and_legacy_rows_survive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hashrelay-upgrade-{Guid.NewGuid():n}.db");
        var options = new DbContextOptionsBuilder<RelayDbContext>().UseSqlite($"Data Source={path}").Options;
        try
        {
            await using (var db = new RelayDbContext(options))
            {
                // Shape the file like the previous release: full schema, then the new columns removed again.
                await db.Database.EnsureCreatedAsync();
                foreach (var (table, column) in Added) await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\"");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Notifications\" (\"Id\",\"TenantId\",\"TagsCsv\",\"Title\",\"Priority\",\"PayloadHash\",\"PublishedAt\",\"MatchedEndpoints\") VALUES ('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222','old','Legacy','normal','h',0,0)");
                foreach (var (table, column) in Added)
                    Assert.DoesNotContain(column, await db.Database.SqlQueryRaw<string>($"SELECT name AS Value FROM pragma_table_info('{table}')").ToListAsync());
            }

            await using (var db = new RelayDbContext(options))
            {
                await SchemaUpgrades.ApplyAsync(db);
                foreach (var (table, column) in Added)
                    Assert.Contains(column, await db.Database.SqlQueryRaw<string>($"SELECT name AS Value FROM pragma_table_info('{table}')").ToListAsync());

                var legacy = await db.Notifications.AsNoTracking().SingleAsync();
                Assert.Equal("Legacy", legacy.Title);
                Assert.Null(legacy.PublisherName);
                Assert.Null(legacy.PublisherType);
                Assert.Null(legacy.Channel);
                Assert.False(RoutingService.PublisherOf(legacy).IsKnown);
                Assert.Equal(Contracts.PublisherTypes.Unknown, RoutingService.PublisherOf(legacy).Type);

                // Idempotent: running the upgrade again on an up-to-date file is a no-op.
                await SchemaUpgrades.ApplyAsync(db);
            }
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }
}
