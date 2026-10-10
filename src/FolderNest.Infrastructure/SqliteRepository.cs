using System.Text.Json;
using FolderNest.Core;
using Microsoft.Data.Sqlite;

namespace FolderNest.Infrastructure;

public sealed class SqliteRepository : IMonitorRepository
{
    private readonly string connectionString;
    public SqliteRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath,
            ForeignKeys = true, DefaultTimeout = 30 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS monitors(id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS observations(
              monitor_id TEXT NOT NULL REFERENCES monitors(id) ON DELETE CASCADE,
              path TEXT NOT NULL COLLATE NOCASE, payload TEXT NOT NULL,
              PRIMARY KEY(monitor_id,path));
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public IReadOnlyList<MonitorDefinition> GetMonitors()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload FROM monitors ORDER BY rowid";
        using var reader = cmd.ExecuteReader(); var result = new List<MonitorDefinition>();
        while(reader.Read()) result.Add(JsonSerializer.Deserialize<MonitorDefinition>(reader.GetString(0))!);
        return result;
    }

    public void SaveMonitor(MonitorDefinition monitor)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var read = c.CreateCommand(); read.Transaction = tx;
        read.CommandText = "SELECT payload FROM monitors WHERE id=$id";
        read.Parameters.AddWithValue("$id", monitor.Id.ToString());
        if (read.ExecuteScalar() is string json &&
            !string.Equals(JsonSerializer.Deserialize<MonitorDefinition>(json)!.Path, monitor.Path,
                StringComparison.OrdinalIgnoreCase))
        {
            using var reset = c.CreateCommand(); reset.Transaction = tx;
            reset.CommandText = "DELETE FROM observations WHERE monitor_id=$id";
            reset.Parameters.AddWithValue("$id", monitor.Id.ToString()); reset.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO monitors(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        cmd.Parameters.AddWithValue("$id", monitor.Id.ToString());
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(monitor)); cmd.ExecuteNonQuery(); tx.Commit();
    }

    public void RemoveMonitor(Guid id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM monitors WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Observation> GetObservations(Guid monitorId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload FROM observations WHERE monitor_id=$id";
        cmd.Parameters.AddWithValue("$id", monitorId.ToString());
        using var reader = cmd.ExecuteReader(); var result = new List<Observation>();
        while(reader.Read()) result.Add(JsonSerializer.Deserialize<Observation>(reader.GetString(0))!);
        return result;
    }

    public void ReplaceObservations(Guid monitorId, IReadOnlyList<Observation> observations)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var clear = c.CreateCommand(); clear.Transaction = tx;
        clear.CommandText = "DELETE FROM observations WHERE monitor_id=$id";
        clear.Parameters.AddWithValue("$id", monitorId.ToString()); clear.ExecuteNonQuery();
        foreach(var o in observations)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO observations VALUES($id,$path,$payload)";
            cmd.Parameters.AddWithValue("$id", monitorId.ToString()); cmd.Parameters.AddWithValue("$path", o.Path);
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(o)); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
