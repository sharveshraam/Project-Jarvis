using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jarvis.Infrastructure.Data;

/// <summary>
/// One serialised SQLite connection (WAL) shared by all stores. A desktop assistant has tiny write
/// volumes, so a single connection behind a lock is the simplest reliable arrangement.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public Database(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        ExecuteRaw("PRAGMA journal_mode=WAL;");
        ExecuteRaw("PRAGMA synchronous=NORMAL;");
        ExecuteRaw("PRAGMA foreign_keys=ON;");
        ExecuteRaw("PRAGMA busy_timeout=5000;");
        Migrate();
    }

    private void ExecuteRaw(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        long version;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (version < 1)
        {
            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Schema.V1;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            ExecuteRaw("PRAGMA user_version=1;");
        }
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, parameters);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Runs an INSERT and returns the new row id.</summary>
    public long Insert(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using (var cmd = Command(sql, parameters)) cmd.ExecuteNonQuery();
            using var idCmd = _connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";
            return Convert.ToInt64(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, parameters);
            var value = cmd.ExecuteScalar();
            return value is DBNull ? null : value;
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, parameters);
            using var reader = cmd.ExecuteReader();
            var list = new List<T>();
            while (reader.Read()) list.Add(map(reader));
            return list;
        }
    }

    /// <summary>Runs several statements atomically.</summary>
    public void InTransaction(Action<Database> work)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            _ambient = tx;
            try
            {
                work(this);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
            finally
            {
                _ambient = null;
            }
        }
    }

    private SqliteTransaction? _ambient;

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Database));
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _ambient;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
    public static object? IsoOrNull(DateTime? utc) => utc.HasValue ? Iso(utc.Value) : null;
    public static DateTime ParseUtc(string text)
        => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    public static DateTime? ParseUtcOrNull(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseUtc(reader.GetString(ordinal));
}

internal static class Schema
{
    public const string V1 = @"
CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

CREATE TABLE conversations (
  id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, mode TEXT NOT NULL,
  summary TEXT NOT NULL DEFAULT '', summarized_through INTEGER NOT NULL DEFAULT 0,
  created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);

CREATE TABLE messages (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
  role TEXT NOT NULL, content TEXT NOT NULL, created_utc TEXT NOT NULL);
CREATE INDEX ix_messages_conversation ON messages(conversation_id, id);

CREATE TABLE memories (
  id INTEGER PRIMARY KEY AUTOINCREMENT, category TEXT NOT NULL, content TEXT NOT NULL,
  source TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);

CREATE TABLE reminders (
  id INTEGER PRIMARY KEY AUTOINCREMENT, text TEXT NOT NULL, due_utc TEXT NOT NULL,
  recurrence TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, last_fired_utc TEXT, created_utc TEXT NOT NULL);
CREATE INDEX ix_reminders_due ON reminders(active, due_utc);

CREATE TABLE todos (
  id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, notes TEXT NOT NULL DEFAULT '',
  priority INTEGER NOT NULL DEFAULT 1, deadline_utc TEXT, completed INTEGER NOT NULL DEFAULT 0,
  completed_utc TEXT, created_utc TEXT NOT NULL);

CREATE TABLE agent_tasks (
  id TEXT PRIMARY KEY, title TEXT NOT NULL, description TEXT NOT NULL, state TEXT NOT NULL, mode TEXT NOT NULL,
  steps_json TEXT NOT NULL, current_step INTEGER NOT NULL DEFAULT 0, result TEXT, error TEXT,
  held INTEGER NOT NULL DEFAULT 0, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);

CREATE TABLE focus_sessions (
  id INTEGER PRIMARY KEY AUTOINCREMENT, goal TEXT NOT NULL, planned_minutes INTEGER NOT NULL,
  start_utc TEXT NOT NULL, end_utc TEXT, closed_apps TEXT NOT NULL DEFAULT '[]');

CREATE TABLE file_ops (
  id INTEGER PRIMARY KEY AUTOINCREMENT, batch_id TEXT NOT NULL, source TEXT NOT NULL, destination TEXT NOT NULL,
  created_utc TEXT NOT NULL, undone INTEGER NOT NULL DEFAULT 0);
CREATE INDEX ix_file_ops_batch ON file_ops(batch_id);

CREATE TABLE audit (
  id INTEGER PRIMARY KEY AUTOINCREMENT, at_utc TEXT NOT NULL, tool TEXT NOT NULL, risk TEXT NOT NULL,
  decision TEXT NOT NULL, success INTEGER NOT NULL, summary TEXT NOT NULL);

CREATE TABLE indexed_files (
  id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL UNIQUE, name TEXT NOT NULL, ext TEXT NOT NULL,
  size INTEGER NOT NULL, mtime_utc TEXT NOT NULL, has_content INTEGER NOT NULL DEFAULT 0);
CREATE INDEX ix_indexed_files_name ON indexed_files(name);

CREATE VIRTUAL TABLE file_fts USING fts5(path UNINDEXED, name, content, tokenize='unicode61');
";
}
