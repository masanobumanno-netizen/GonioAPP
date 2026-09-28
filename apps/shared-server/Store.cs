using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Gonio.Shared;
public sealed class Store
{
    public string Root { get; }
    private readonly string connection;
    public Store(string root)
    {
        Root=Path.GetFullPath(root);Directory.CreateDirectory(Root);
        connection=new SqliteConnectionStringBuilder{DataSource=Path.Combine(Root,"gonio.db"),ForeignKeys=true}.ToString();
        using var db=Open();using var command=db.CreateCommand();command.CommandText="""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);
            INSERT INTO schema_version SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_version);
            CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY, username TEXT UNIQUE COLLATE NOCASE, display_name TEXT NOT NULL, password TEXT NOT NULL, role TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS tokens(hash TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), expires TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, owner TEXT NOT NULL REFERENCES users(id), metadata TEXT NOT NULL, created TEXT NOT NULL, deleted INTEGER NOT NULL DEFAULT 0, analysis TEXT NOT NULL DEFAULT 'pending', sync TEXT NOT NULL DEFAULT 'pending', error TEXT NOT NULL DEFAULT '', sync_error TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS stimuli(id TEXT PRIMARY KEY,name TEXT NOT NULL,extension TEXT NOT NULL,created TEXT NOT NULL,deleted INTEGER NOT NULL DEFAULT 0,sync TEXT NOT NULL DEFAULT 'pending');
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,at TEXT NOT NULL,actor TEXT NOT NULL,action TEXT NOT NULL,target TEXT NOT NULL);
            """;command.ExecuteNonQuery();
    }
    public SqliteConnection Open(){var db=new SqliteConnection(connection);db.Open();return db;}
    public static SqliteCommand Command(SqliteConnection db,string sql,params (string,object?)[] args){var c=db.CreateCommand();c.CommandText=sql;foreach(var (k,v) in args)c.Parameters.AddWithValue(k,v??DBNull.Value);return c;}
    public void Exec(string sql,params (string,object?)[] args){using var db=Open();using var c=Command(db,sql,args);c.ExecuteNonQuery();}
    public List<Dictionary<string,object?>> Query(string sql,params (string,object?)[] args){using var db=Open();using var c=Command(db,sql,args);using var reader=c.ExecuteReader();var rows=new List<Dictionary<string,object?>>();while(reader.Read()){var row=new Dictionary<string,object?>();for(int i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);rows.Add(row);}return rows;}
    public string? Setting(string key)=>Query("SELECT value FROM settings WHERE key=$k",("$k",key)).FirstOrDefault()?.GetValueOrDefault("value") as string;
    public void Setting(string key,string value)=>Exec("INSERT INTO settings VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v",("$k",key),("$v",value));
    public void Audit(string actor,string action,string target)=>Exec("INSERT INTO audit(at,actor,action,target) VALUES($at,$a,$b,$c)",("$at",DateTimeOffset.UtcNow.ToString("O")),("$a",actor),("$b",action),("$c",target));
    public static string Id(string id)=>Guid.TryParseExact(id,"D",out var _) ? id : throw new ArgumentException("IDが不正です。");
    public string SessionDir(string id)=>Path.Combine(Root,"sessions",Id(id));
    public static string HashToken(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Password(string value){if(value.Length<12||value.Length>256)throw new ArgumentException("パスワードは12〜256文字で指定してください。");var salt=RandomNumberGenerator.GetBytes(16);return Convert.ToBase64String(salt)+":"+Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(value,salt,210000,HashAlgorithmName.SHA256,32));}
    public static bool VerifyPassword(string value,string encoded){try{var p=encoded.Split(':');return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(p[1]),Rfc2898DeriveBytes.Pbkdf2(value,Convert.FromBase64String(p[0]),210000,HashAlgorithmName.SHA256,32));}catch{return false;}}
    public User? Authenticate(string token){if(token.Length!=64)return null;var row=Query("SELECT u.* FROM tokens t JOIN users u ON u.id=t.user_id WHERE t.hash=$h AND t.expires>$now AND u.active=1",("$h",HashToken(token)),("$now",DateTimeOffset.UtcNow.ToString("O"))).FirstOrDefault();return row==null?null:User.From(row);}
}
public record User(string Id,string Username,string DisplayName,string Role){public static User From(Dictionary<string,object?> row)=>new((string)row["id"]!,(string)row["username"]!,(string)row["display_name"]!,(string)row["role"]!);}
public record Login(string Username,string Password,string? DisplayName,string? SetupToken);
public record UserEdit(string Username,string DisplayName,string Role,bool Active,string? Password);
public static class JsonTools
{
    public static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web);
    public static void Atomic(string path,object data){Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(data,Options));File.Move(temp,path,true);}
}
