using Microsoft.Data.Sqlite;

namespace Einsatzcockpit.PrintAgent.Core.Jobs;

public sealed class JobStore
{
    private readonly string _connectionString;
    public JobStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;" +
            "CREATE TABLE IF NOT EXISTS jobs (job_id TEXT PRIMARY KEY, lease_token TEXT NOT NULL, generation INTEGER NOT NULL, state TEXT NOT NULL, document_type TEXT NOT NULL, artifact_url TEXT NOT NULL, render_kind TEXT NOT NULL, options_json TEXT NULL, route_json TEXT NOT NULL, pdf_path TEXT NULL, spool_ref TEXT NULL, document_name TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, error TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS outbox (id TEXT PRIMARY KEY, type TEXT NOT NULL, payload_json TEXT NOT NULL, created_at TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0);";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    public StoredJob? Get(string jobId)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT * FROM jobs WHERE job_id=$id"; cmd.Parameters.AddWithValue("$id", jobId);
        using var r = cmd.ExecuteReader(); return r.Read() ? ReadJob(r) : null;
    }
    public IReadOnlyList<StoredJob> GetByState(params string[] states)
    {
        if (states.Length == 0) return [];
        using var c = Open(); using var cmd = c.CreateCommand();
        var names = states.Select((_, i) => "$s" + i).ToArray(); cmd.CommandText = $"SELECT * FROM jobs WHERE state IN ({string.Join(',', names)})";
        for (var i = 0; i < states.Length; i++) cmd.Parameters.AddWithValue(names[i], states[i]);
        using var r = cmd.ExecuteReader(); var result = new List<StoredJob>(); while (r.Read()) result.Add(ReadJob(r)); return result;
    }
    public void Insert(StoredJob job)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO jobs(job_id,lease_token,generation,state,document_type,artifact_url,render_kind,options_json,route_json,pdf_path,spool_ref,document_name,attempts,error,created_at,updated_at) VALUES($id,$token,$gen,$state,$doc,$url,$kind,$options,$route,$pdf,$spool,$name,$attempts,$error,$created,$updated)";
        Bind(cmd, job); cmd.ExecuteNonQuery();
    }
    public void Update(StoredJob job)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET lease_token=$token,generation=$gen,state=$state,document_type=$doc,artifact_url=$url,render_kind=$kind,options_json=$options,route_json=$route,pdf_path=$pdf,spool_ref=$spool,document_name=$name,attempts=$attempts,error=$error,updated_at=$updated WHERE job_id=$id";
        Bind(cmd, job); cmd.ExecuteNonQuery();
    }
    public void AddOutbox(string type, string payloadJson, DateTimeOffset now)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO outbox(id,type,payload_json,created_at,attempts) VALUES($id,$type,$payload,$created,0)";
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); cmd.Parameters.AddWithValue("$type", type); cmd.Parameters.AddWithValue("$payload", payloadJson); cmd.Parameters.AddWithValue("$created", now.ToString("O")); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<OutboxItem> PendingOutbox()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,type,payload_json,created_at,attempts FROM outbox ORDER BY created_at"; using var r = cmd.ExecuteReader(); var result = new List<OutboxItem>(); while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3)), r.GetInt32(4))); return result;
    }
    public void RemoveOutbox(string id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM outbox WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    private static StoredJob ReadJob(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.GetInt32(12), r.IsDBNull(13) ? null : r.GetString(13), DateTimeOffset.Parse(r.GetString(14)), DateTimeOffset.Parse(r.GetString(15)));
    private static void Bind(SqliteCommand cmd, StoredJob j)
    {
        cmd.Parameters.AddWithValue("$id", j.JobId); cmd.Parameters.AddWithValue("$token", j.LeaseToken); cmd.Parameters.AddWithValue("$gen", j.Generation); cmd.Parameters.AddWithValue("$state", j.State); cmd.Parameters.AddWithValue("$doc", j.DocumentType); cmd.Parameters.AddWithValue("$url", j.ArtifactUrl); cmd.Parameters.AddWithValue("$kind", j.RenderKind); cmd.Parameters.AddWithValue("$options", (object?)j.OptionsJson ?? DBNull.Value); cmd.Parameters.AddWithValue("$route", j.RouteJson); cmd.Parameters.AddWithValue("$pdf", (object?)j.PdfPath ?? DBNull.Value); cmd.Parameters.AddWithValue("$spool", (object?)j.SpoolRef ?? DBNull.Value); cmd.Parameters.AddWithValue("$name", j.DocumentName); cmd.Parameters.AddWithValue("$attempts", j.Attempts); cmd.Parameters.AddWithValue("$error", (object?)j.Error ?? DBNull.Value); cmd.Parameters.AddWithValue("$created", j.CreatedAt.ToString("O")); cmd.Parameters.AddWithValue("$updated", j.UpdatedAt.ToString("O"));
    }
}
