using Microsoft.Data.Sqlite;

namespace Hark.Core;

public sealed record Conversation(long Id, string Title, string? Model, string? SessionId, DateTime Created, DateTime Updated, string Preview);

public sealed record StoredMessage(long Id, long ConversationId, string Kind, string Content, DateTime Created);

/// <summary>Chat history in a local SQLite file. Kinds: user, assistant, tool, error.</summary>
public sealed class ChatStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public ChatStore(string? databaseFile = null)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databaseFile ?? AppPaths.DatabaseFile }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS conversations(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                model TEXT,
                session_id TEXT,
                created TEXT NOT NULL,
                updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                content TEXT NOT NULL,
                created TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages(conversation_id, id);
            """);
    }

    public long CreateConversation(string title, string? model)
    {
        var now = Now();
        return Scalar("INSERT INTO conversations(title, model, created, updated) VALUES($t,$m,$n,$n); SELECT last_insert_rowid();",
            ("$t", title), ("$m", model), ("$n", now));
    }

    public void SetSession(long conversationId, string? sessionId, string? model) =>
        Execute("UPDATE conversations SET session_id=$s, model=$m WHERE id=$id", ("$s", sessionId), ("$m", model), ("$id", conversationId));

    public void Rename(long conversationId, string title) =>
        Execute("UPDATE conversations SET title=$t WHERE id=$id", ("$t", title), ("$id", conversationId));

    public long AddMessage(long conversationId, string kind, string content)
    {
        var now = Now();
        var id = Scalar("INSERT INTO messages(conversation_id, kind, content, created) VALUES($c,$k,$t,$n); SELECT last_insert_rowid();",
            ("$c", conversationId), ("$k", kind), ("$t", content), ("$n", now));
        Execute("UPDATE conversations SET updated=$n WHERE id=$id", ("$n", now), ("$id", conversationId));
        return id;
    }

    public void DeleteConversation(long conversationId)
    {
        Execute("DELETE FROM messages WHERE conversation_id=$id", ("$id", conversationId));
        Execute("DELETE FROM conversations WHERE id=$id", ("$id", conversationId));
    }

    public List<Conversation> ListConversations(string? search = null)
    {
        var list = new List<Conversation>();
        lock (_gate)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT c.id, c.title, c.model, c.session_id, c.created, c.updated,
                       COALESCE((SELECT content FROM messages m WHERE m.conversation_id=c.id AND m.kind='assistant' ORDER BY m.id DESC LIMIT 1), '')
                FROM conversations c
                WHERE $q = '' OR c.title LIKE $like
                   OR EXISTS(SELECT 1 FROM messages m WHERE m.conversation_id=c.id AND m.content LIKE $like)
                ORDER BY c.updated DESC LIMIT 500
                """;
            cmd.Parameters.AddWithValue("$q", search?.Trim() ?? "");
            cmd.Parameters.AddWithValue("$like", "%" + (search?.Trim() ?? "") + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var preview = r.GetString(6).ReplaceLineEndings(" ");
                if (preview.Length > 140) preview = preview[..140] + "…";
                list.Add(new Conversation(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), Parse(r.GetString(4)), Parse(r.GetString(5)), preview));
            }
        }
        return list;
    }

    public List<StoredMessage> GetMessages(long conversationId)
    {
        var list = new List<StoredMessage>();
        lock (_gate)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT id, conversation_id, kind, content, created FROM messages WHERE conversation_id=$id ORDER BY id";
            cmd.Parameters.AddWithValue("$id", conversationId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredMessage(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), Parse(r.GetString(4))));
        }
        return list;
    }

    private static string Now() => DateTime.UtcNow.ToString("o");
    private static DateTime Parse(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();

    private SqliteConnection Open()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        return con;
    }

    private void Execute(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private long Scalar(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }
}
