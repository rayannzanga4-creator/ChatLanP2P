using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ChatLan.Core;

public class AppDb : DbContext
{
    readonly string _path;
    public AppDb(string path) { _path = path; }

    public DbSet<ProfileRow> Profiles => Set<ProfileRow>();
    public DbSet<ContactRow> Contacts => Set<ContactRow>();
    public DbSet<MessageRow> Messages => Set<MessageRow>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    public DbSet<GroupMemberRow> GroupMembers => Set<GroupMemberRow>();
    public DbSet<ReactionRow> Reactions => Set<ReactionRow>();
    public DbSet<StatusRow> Statuses => Set<StatusRow>();
    public DbSet<StatusViewRow> StatusViews => Set<StatusViewRow>();
    public DbSet<StatusReactionRow> StatusReactions => Set<StatusReactionRow>();

    // Default Timeout = attente en cas de verrou (plusieurs opérations arrivent en même temps)
    protected override void OnConfiguring(DbContextOptionsBuilder o) =>
        o.UseSqlite("Data Source=" + _path + ";Pooling=False;Default Timeout=30");

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ProfileRow>().HasKey(x => x.Id);
        b.Entity<ContactRow>().HasKey(x => x.PeerId);
        b.Entity<MessageRow>().HasIndex(x => x.Uid).IsUnique();
        b.Entity<MessageRow>().HasIndex(x => x.ContactId);
        b.Entity<OutboxRow>().HasIndex(x => x.ContactId);
        b.Entity<GroupMemberRow>().HasIndex(x => x.GroupId);
        b.Entity<ReactionRow>().HasIndex(x => x.MessageUid);
        b.Entity<StatusRow>().HasKey(x => x.Id);
        b.Entity<StatusRow>().HasIndex(x => x.ExpiresAt);
        b.Entity<StatusViewRow>().HasIndex(x => x.StatusId);
        b.Entity<StatusReactionRow>().HasIndex(x => x.StatusId);
    }
}

// Met la base au niveau 2 : EnsureCreated ne touche pas une base déjà existante,
// on ajoute donc les colonnes et les tables manquantes à la main (comptes du Niveau 1 conservés).
public static class Schema
{
    public static void Ensure(AppDb db)
    {
        db.Database.EnsureCreated();
        // WAL : lectures et écritures simultanées sans blocage (messages, accusés, appels)
        try { db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;"); } catch { }
        try { db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=30000;"); } catch { }
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS GroupMembers (
            Id INTEGER NOT NULL CONSTRAINT PK_GroupMembers PRIMARY KEY AUTOINCREMENT,
            GroupId TEXT NOT NULL, PeerId TEXT NOT NULL, Name TEXT NOT NULL DEFAULT '',
            IsAdmin INTEGER NOT NULL DEFAULT 0, IsMe INTEGER NOT NULL DEFAULT 0);");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS Reactions (
            Id INTEGER NOT NULL CONSTRAINT PK_Reactions PRIMARY KEY AUTOINCREMENT,
            MessageUid TEXT NOT NULL, AuthorPeerId TEXT NOT NULL DEFAULT '', Emoji TEXT NOT NULL DEFAULT '');");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_GroupMembers_GroupId ON GroupMembers (GroupId);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Reactions_MessageUid ON Reactions (MessageUid);");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS Statuses (
            Id TEXT NOT NULL CONSTRAINT PK_Statuses PRIMARY KEY,
            AuthorPeerId TEXT NOT NULL DEFAULT '', AuthorName TEXT NOT NULL DEFAULT '',
            Kind TEXT NOT NULL DEFAULT 'text', Text TEXT NOT NULL DEFAULT '',
            MediaId TEXT NOT NULL DEFAULT '', MediaName TEXT NOT NULL DEFAULT '',
            MediaMime TEXT NOT NULL DEFAULT '', MediaSize INTEGER NOT NULL DEFAULT 0,
            Background TEXT NOT NULL DEFAULT '0', Audience TEXT NOT NULL DEFAULT 'contacts',
            AudienceList TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL,
            Deleted INTEGER NOT NULL DEFAULT 0, FromMe INTEGER NOT NULL DEFAULT 0);");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS StatusViews (
            Id INTEGER NOT NULL CONSTRAINT PK_StatusViews PRIMARY KEY AUTOINCREMENT,
            StatusId TEXT NOT NULL, ViewerPeerId TEXT NOT NULL DEFAULT '',
            ViewerName TEXT NOT NULL DEFAULT '', At TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS StatusReactions (
            Id INTEGER NOT NULL CONSTRAINT PK_StatusReactions PRIMARY KEY AUTOINCREMENT,
            StatusId TEXT NOT NULL, AuthorPeerId TEXT NOT NULL DEFAULT '', Emoji TEXT NOT NULL DEFAULT '');");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Statuses_ExpiresAt ON Statuses (ExpiresAt);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_StatusViews_StatusId ON StatusViews (StatusId);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_StatusReactions_StatusId ON StatusReactions (StatusId);");

        AddColumn(db, "Messages", "Kind", "TEXT NOT NULL DEFAULT 'text'");
        AddColumn(db, "Messages", "MediaId", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Messages", "MediaName", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Messages", "MediaSize", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "Messages", "MediaMime", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Messages", "DurationMs", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "Messages", "ReplyUid", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Messages", "ReplyAuthor", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Messages", "ReplyPreview", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Messages", "EditedAt", "TEXT NULL");
        AddColumn(db, "Messages", "FromPeerId", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Messages", "FromName", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "Messages", "Forwarded", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "Contacts", "IsGroup", "INTEGER NOT NULL DEFAULT 0");
    }

    static void AddColumn(AppDb db, string table, string column, string definition)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            var wasClosed = conn.State != System.Data.ConnectionState.Open;
            if (wasClosed) conn.Open();
            var exists = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({table});";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
            }
            if (!exists)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
                    cmd.ExecuteNonQuery();
                }
            if (wasClosed) conn.Close();
        }
        catch { }
    }
}

public static class Pwd
{
    public static string Hash(string p)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(p, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(h);
    }

    public static bool Verify(string p, string stored)
    {
        var s = stored.Split('.');
        if (s.Length != 2) return false;
        var salt = Convert.FromBase64String(s[0]);
        var h = Rfc2898DeriveBytes.Pbkdf2(p, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(s[1]));
    }
}

public sealed record AuthOutcome(string? Error, string? DbPath, ProfileRow? Profile);

// Comptes locaux : un fichier SQLite par utilisateur dans %AppData%\ChatLan\profiles
public static class Accounts
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatLan", "profiles");

    static readonly Regex UsernameRule = new Regex("^[a-z0-9_.]{3,20}$");

    public static string DbPath(string username) => Path.Combine(Dir, username + ".db");

    public static async Task<AuthOutcome> RegisterAsync(string username, string password, string displayName)
    {
        var un = (username ?? "").Trim().ToLowerInvariant();
        var name = (displayName ?? "").Trim();
        if (!UsernameRule.IsMatch(un))
            return new AuthOutcome("Nom d'utilisateur : 3 à 20 caractères (lettres, chiffres, _ et .).", null, null);
        if (name.Length < 2 || name.Length > 40)
            return new AuthOutcome("Le nom affiché doit contenir entre 2 et 40 caractères.", null, null);
        if ((password ?? "").Length < 4)
            return new AuthOutcome("Le mot de passe doit contenir au moins 4 caractères.", null, null);

        var path = DbPath(un);
        if (File.Exists(path))
            return new AuthOutcome("Ce nom d'utilisateur existe déjà sur cet ordinateur.", null, null);

        try
        {
            Directory.CreateDirectory(Dir);
            var profile = new ProfileRow
            {
                PeerId = Guid.NewGuid().ToString("N"),
                Username = un,
                DisplayName = name,
                PasswordHash = Pwd.Hash(password!)
            };
            using (var db = new AppDb(path))
            {
                await db.Database.EnsureCreatedAsync();
                try { await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;"); } catch { }
                Schema.Ensure(db);
                db.Profiles.Add(profile);
                await db.SaveChangesAsync();
            }
            return new AuthOutcome(null, path, profile);
        }
        catch (Exception ex)
        {
            return new AuthOutcome("Création du compte impossible : " + ex.Message, null, null);
        }
    }

    public static async Task<AuthOutcome> LoginAsync(string username, string password)
    {
        var un = (username ?? "").Trim().ToLowerInvariant();
        var path = DbPath(un);
        if (un.Length == 0 || !File.Exists(path))
            return new AuthOutcome("Compte introuvable sur cet ordinateur. Créez-le dans l'onglet Inscription.", null, null);
        try
        {
            using var db = new AppDb(path);
            Schema.Ensure(db);
            var profile = await db.Profiles.FirstOrDefaultAsync();
            if (profile is null || !Pwd.Verify(password ?? "", profile.PasswordHash))
                return new AuthOutcome("Nom d'utilisateur ou mot de passe incorrect.", null, null);
            return new AuthOutcome(null, path, profile);
        }
        catch (Exception ex)
        {
            return new AuthOutcome("Ouverture du compte impossible : " + ex.Message, null, null);
        }
    }
}
