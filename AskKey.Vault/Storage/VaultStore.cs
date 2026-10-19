using Microsoft.Data.Sqlite;

namespace AskKey.Vault;

/// <summary>
/// One SQLite connection with serialized access. WAL is enabled for new files;
/// read-only opens never create or migrate anything.
/// </summary>
public sealed partial class VaultStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly string _path;
    private byte[]? _authenticationKey;
    private bool _closed;

    public string Path => _path;

    public VaultStore(string path, byte[]? authenticationKey = null, Action<VaultStore>? validation = null)
    {
        _path = path;
        _authenticationKey = authenticationKey;
        var exists = File.Exists(path);
        if (!exists && authenticationKey is not null)
        {
            throw new VaultBootstrapException(VaultBootstrapError.MissingDatabase);
        }

        CurrentLibrarySchema.Opening? opening = null;
        if (exists) Preflight(path, authenticationKey, validation, out opening);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = exists ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        if (!exists) ExecuteRaw("PRAGMA journal_mode=WAL;");
        ExecuteRaw("PRAGMA foreign_keys=ON;");
        ExecuteRaw("PRAGMA busy_timeout=5000;");

        if (opening == CurrentLibrarySchema.Opening.LegacyV15)
        {
            RunTransaction(() => AdoptLegacyV15(validation));
        }
        else if (exists)
        {
            var actual = ReadOpening();
            if (actual != opening) throw new VaultBootstrapException(VaultBootstrapError.InvalidState);
            if (authenticationKey is not null) ValidateCredentialRows(authenticationKey);
            validation?.Invoke(this);
        }

        if (opening != CurrentLibrarySchema.Opening.Current) Migrate();
    }

    private VaultStore(string path, SqliteConnection connection)
    {
        _path = path;
        _connection = connection;
    }

    /// <summary>
    /// Builds a library at exactly the baseline stage, without running the
    /// drop-legacy migration. Bootstrap uses it as the expected value for an
    /// unfinished first creation, so the expectation comes from the real
    /// baseline SQL rather than a hand-maintained copy.
    /// </summary>
    internal static VaultStore CreateBaseline(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        var store = new VaultStore(path, connection);
        try
        {
            store.ApplyBaseline();
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public static VaultStore OpenReadOnly(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return new VaultStore(path, connection);
    }

    public void BindCredentialAuthenticationKey(byte[]? key)
    {
        lock (_gate) _authenticationKey = key;
    }

    private byte[] RequireAuthenticationKey() => _authenticationKey ?? throw VaultException.Locked();

    public CredentialRecord SealForPersistence(CredentialRecord record)
        => CredentialRecordAuthentication.Seal(record, RequireAuthenticationKey());

    public CredentialRecord Authenticate(CredentialRecord record)
    {
        CredentialRecordAuthentication.Verify(record, RequireAuthenticationKey());
        return record;
    }

    public void ValidateCredentialRows(byte[] key)
    {
        foreach (var record in FetchAllCredentialsIncludingRecycledRaw())
        {
            CredentialRecordAuthentication.Verify(record, key);
        }
    }

    private static void Preflight(string path, byte[]? key, Action<VaultStore>? validation,
        out CurrentLibrarySchema.Opening? opening)
    {
        opening = CurrentLibrarySnapshot.WithCopy(path, copy =>
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = copy,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var store = new VaultStore(copy, connection);
            var result = store.ReadOpening();
            if (key is not null) store.ValidateCredentialRows(key);
            validation?.Invoke(store);
            return result;
        });
    }

    public CurrentLibrarySchema.Opening ReadOpening()
    {
        lock (_gate)
        {
            if (!TableExistsRaw("grdb_migrations"))
            {
                throw new VaultBootstrapException(VaultBootstrapError.UnsupportedMigrations);
            }
            var identifiers = QueryStrings("SELECT identifier FROM grdb_migrations ORDER BY rowid;");
            CurrentLibrarySchema.Opening opening;
            bool[] acceptedSchemas;
            if (identifiers.SequenceEqual(new[] { CurrentLibrarySchema.BaselineIdentifier }))
            {
                opening = CurrentLibrarySchema.Opening.Baseline;
                acceptedSchemas = new[] { false };
            }
            else if (identifiers.SequenceEqual(CurrentLibrarySchema.LegacyIdentifiers))
            {
                opening = CurrentLibrarySchema.Opening.LegacyV15;
                acceptedSchemas = new[] { false };
            }
            else if (identifiers.SequenceEqual(CurrentLibrarySchema.CurrentIdentifiers))
            {
                opening = CurrentLibrarySchema.Opening.Current;
                acceptedSchemas = new[] { true, false };
            }
            else
            {
                throw new VaultBootstrapException(VaultBootstrapError.UnsupportedMigrations);
            }

            var schema = NormalizedSchema();
            foreach (var dropped in acceptedSchemas)
            {
                if (ExpectedSchema(dropped).SequenceEqual(schema, StringComparer.Ordinal)) return opening;
            }
            throw new VaultBootstrapException(VaultBootstrapError.SchemaMismatch);
        }
    }

    private static List<string> ExpectedSchema(bool legacyTablesDropped)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "AskKeySchema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "schema.db");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var store = new VaultStore(path, connection);
            store.ApplyBaseline();
            if (legacyTablesDropped) store.DropLegacyTables();
            return store.NormalizedSchema();
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    public void Migrate()
    {
        lock (_gate)
        {
            // A brand-new file has no migration table yet; that is the empty
            // history that ApplyBaseline below turns into askkey-0001-baseline.
            var identifiers = TableExistsRaw("grdb_migrations")
                ? QueryStrings("SELECT identifier FROM grdb_migrations ORDER BY rowid;")
                : new List<string>();
            if (identifiers.SequenceEqual(CurrentLibrarySchema.CurrentIdentifiers)) return;

            if (identifiers.SequenceEqual(new[] { CurrentLibrarySchema.BaselineIdentifier }))
            {
                RunTransaction(() =>
                {
                    if (ReadOpening() != CurrentLibrarySchema.Opening.Baseline)
                    {
                        throw new VaultBootstrapException(VaultBootstrapError.InvalidState);
                    }
                    if (LegacyTablesHoldNoUserData()) DropLegacyTables();
                    ExecuteRaw("INSERT INTO grdb_migrations (identifier) VALUES ($id);",
                        ("$id", CurrentLibrarySchema.DropLegacyTablesIdentifier));
                });
                return;
            }

            if (identifiers.Count == 0)
            {
                ApplyBaseline();
                RunTransaction(() =>
                {
                    if (LegacyTablesHoldNoUserData()) DropLegacyTables();
                    ExecuteRaw("INSERT INTO grdb_migrations (identifier) VALUES ($id);",
                        ("$id", CurrentLibrarySchema.DropLegacyTablesIdentifier));
                });
                return;
            }

            throw new VaultBootstrapException(VaultBootstrapError.UnsupportedMigrations);
        }
    }

    private void ApplyBaseline()
    {
        RunTransaction(() =>
        {
            ExecuteRaw("CREATE TABLE IF NOT EXISTS grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY);");
            foreach (var statement in SplitStatements(CurrentLibrarySchema.BaselineSql)) ExecuteRaw(statement);
            var projectId = Guid.NewGuid().ToString().ToUpperInvariant();
            var environmentId = Guid.NewGuid().ToString().ToUpperInvariant();
            var now = CredentialRecord.Timestamp(DateTimeOffset.UtcNow);
            ExecuteRaw("""
                INSERT INTO projects (id, name, active_environment, icon, created_at, updated_at)
                VALUES ($id, 'Default', 'Default', 'folder', $now, $now);
                """, ("$id", projectId), ("$now", now));
            ExecuteRaw("""
                INSERT INTO environments (id, project_id, name, created_at)
                VALUES ($id, $project, 'Default', $now);
                """, ("$id", environmentId), ("$project", projectId), ("$now", now));
            ExecuteRaw("INSERT INTO config (key, value) VALUES ('active_project_id', $id);", ("$id", projectId));
            ExecuteRaw("INSERT INTO grdb_migrations (identifier) VALUES ($id);",
                ("$id", CurrentLibrarySchema.BaselineIdentifier));
        });
    }

    /// <summary>
    /// Runs a unit of work inside one SQLite transaction. Commands issued by the
    /// nested helpers share this connection, so a single BEGIN/COMMIT covers them;
    /// Microsoft.Data.Sqlite would otherwise reject commands not bound to a
    /// SqliteTransaction object.
    /// </summary>
    private void RunTransaction(Action body) => RunTransaction<object?>(() =>
    {
        body();
        return null;
    });

    private void AdoptLegacyV15(Action<VaultStore>? validation)
    {
        var state = ReadOpening();
        validation?.Invoke(this);
        if (state != CurrentLibrarySchema.Opening.LegacyV15) return;
        ExecuteRaw("DELETE FROM grdb_migrations;");
        ExecuteRaw("INSERT INTO grdb_migrations (identifier) VALUES ($id);",
            ("$id", CurrentLibrarySchema.BaselineIdentifier));
    }

    private static IEnumerable<string> SplitStatements(string sql)
    {
        var start = 0;
        var inQuote = false;
        for (var index = 0; index < sql.Length; index++)
        {
            if (sql[index] == '\'') inQuote = !inQuote;
            if (sql[index] != ';' || inQuote) continue;
            var statement = sql[start..index].Trim();
            if (statement.Length > 0) yield return statement;
            start = index + 1;
        }
        var tail = sql[start..].Trim();
        if (tail.Length > 0) yield return tail;
    }

    public bool LegacyTablesHoldNoUserData()
    {
        foreach (var table in new[] { "secrets", "secret_values", "activity_log" })
        {
            if (ScalarLong($"SELECT COUNT(*) FROM \"{table}\";") != 0) return false;
        }
        using var projects = Query("SELECT id, name, active_environment, icon FROM projects;");
        var projectRows = ReadAll(projects);
        using var environments = Query("SELECT project_id, name, color FROM environments;");
        var environmentRows = ReadAll(environments);
        if (projectRows.Count == 0) return environmentRows.Count == 0;
        if (projectRows.Count != 1) return false;
        var project = projectRows[0];
        if (!Equals(project["name"], "Default") || !Equals(project["active_environment"], "Default")
            || !Equals(project["icon"], "folder")) return false;
        if (environmentRows.Count == 0) return true;
        return environmentRows.Count == 1
            && Equals(environmentRows[0]["project_id"], project["id"])
            && Equals(environmentRows[0]["name"], "Default")
            && environmentRows[0]["color"] is null;
    }

    public void DropLegacyTables()
    {
        foreach (var table in CurrentLibrarySchema.LegacyTables)
        {
            ExecuteRaw($"DROP TABLE IF EXISTS \"{table}\";");
        }
    }

    public List<string> NormalizedSchema()
    {
        var result = new List<string>();
        using var reader = Query("SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name;");
        while (reader.Read())
        {
            var sql = reader.IsDBNull(3) ? "\u0002" : NormalizeSql(reader.GetString(3));
            result.Add(string.Join("\0", reader.GetString(0), reader.GetString(1), reader.GetString(2), sql));
        }
        return result;
    }

    internal static string NormalizeSql(string sql)
    {
        var tokens = new List<string>();
        var word = new System.Text.StringBuilder();
        var index = 0;
        void Flush()
        {
            if (word.Length == 0) return;
            tokens.Add(word.ToString().ToLowerInvariant());
            word.Clear();
        }
        while (index < sql.Length)
        {
            var character = sql[index];
            if (character is '\'' or '"' or '`' or '[')
            {
                Flush();
                var delimiter = character == '[' ? ']' : character;
                var quoted = new System.Text.StringBuilder().Append(character);
                index++;
                while (index < sql.Length)
                {
                    var next = sql[index];
                    quoted.Append(next);
                    index++;
                    if (next != delimiter) continue;
                    if (index < sql.Length && sql[index] == delimiter)
                    {
                        quoted.Append(sql[index]);
                        index++;
                    }
                    else break;
                }
                tokens.Add(quoted.ToString());
                continue;
            }
            if (char.IsLetterOrDigit(character) || character is '_' or '$')
            {
                word.Append(character);
            }
            else
            {
                Flush();
                if (!char.IsWhiteSpace(character)) tokens.Add(character.ToString());
            }
            index++;
        }
        Flush();
        return string.Join("\u0001", tokens);
    }

    public VaultStore QuiescedCopy() => OpenReadOnly(_path);

    public void Checkpoint()
    {
        lock (_gate) ExecuteRaw("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _connection.Dispose();
        }
    }

    // MARK: - Config

    public string? ConfigValue(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM config WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetConfigValue(string key, string? value)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = value is null
                ? "DELETE FROM config WHERE key = $key;"
                : "INSERT OR REPLACE INTO config (key, value) VALUES ($key, $value);";
            command.Parameters.AddWithValue("$key", key);
            if (value is not null) command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    // MARK: - Credentials

    public void InsertCredential(CredentialRecord record)
    {
        lock (_gate)
        {
            var sealedRecord = SealForPersistence(record);
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO credentials (id, name_index, encrypted_display_name, encrypted_payload,
                    encrypted_usage_instructions, encrypted_private_notes, encrypted_group_name,
                    encrypted_environment_variable, payload_kind, permission, expires_at, created_at, updated_at,
                    encrypted_original_filename, byte_size, content_digest, deleted_at, authentication_tag)
                VALUES ($id, $nameIndex, $displayName, $payload, $usage, $notes, $group, $environment,
                    $kind, $permission, $expires, $created, $updated, $filename, $size, $digest, $deleted, $tag);
                """;
            AddCredentialParameters(command, sealedRecord);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateCredential(CredentialRecord record)
    {
        lock (_gate)
        {
            var current = FetchCredentialRaw(record.Id) ?? throw VaultException.NotFound(record.Id);
            Authenticate(current);
            var sealedRecord = SealForPersistence(record);
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE credentials SET name_index=$nameIndex, encrypted_display_name=$displayName,
                    encrypted_payload=$payload, encrypted_usage_instructions=$usage,
                    encrypted_private_notes=$notes, encrypted_group_name=$group,
                    encrypted_environment_variable=$environment, payload_kind=$kind, permission=$permission,
                    expires_at=$expires, created_at=$created, updated_at=$updated,
                    encrypted_original_filename=$filename, byte_size=$size, content_digest=$digest,
                    deleted_at=$deleted, authentication_tag=$tag
                WHERE id=$id;
                """;
            AddCredentialParameters(command, sealedRecord);
            command.ExecuteNonQuery();
        }
    }

    private static void AddCredentialParameters(SqliteCommand command, CredentialRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$nameIndex", record.NameIndex);
        command.Parameters.AddWithValue("$displayName", record.EncryptedDisplayName);
        command.Parameters.AddWithValue("$payload", record.EncryptedPayload);
        command.Parameters.AddWithValue("$usage", record.EncryptedUsageInstructions);
        command.Parameters.AddWithValue("$notes", record.EncryptedPrivateNotes);
        command.Parameters.AddWithValue("$group", (object?)record.EncryptedGroupName ?? DBNull.Value);
        command.Parameters.AddWithValue("$environment", (object?)record.EncryptedEnvironmentVariable ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", record.PayloadKind);
        command.Parameters.AddWithValue("$permission", record.Permission);
        command.Parameters.AddWithValue("$expires", (object?)record.ExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", record.CreatedAt);
        command.Parameters.AddWithValue("$updated", record.UpdatedAt);
        command.Parameters.AddWithValue("$filename", (object?)record.EncryptedOriginalFilename ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)record.ByteSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$digest", (object?)record.ContentDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted", (object?)record.DeletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$tag", (object?)record.AuthenticationTag ?? DBNull.Value);
    }

    public CredentialRecord? FetchCredential(string id)
    {
        lock (_gate)
        {
            var record = FetchCredentialRaw(id);
            return record is null || record.DeletedAt is not null ? null : Authenticate(record);
        }
    }

    public CredentialRecord? FetchCredentialByNameIndex(byte[] nameIndex)
    {
        lock (_gate)
        {
            var record = QueryCredential("SELECT * FROM credentials WHERE name_index = $index AND deleted_at IS NULL;",
                ("$index", nameIndex));
            return record is null ? null : Authenticate(record);
        }
    }

    public CredentialRecord? FetchCredentialIncludingRecycledByNameIndex(byte[] nameIndex)
    {
        lock (_gate)
        {
            var record = QueryCredential("SELECT * FROM credentials WHERE name_index = $index;", ("$index", nameIndex));
            return record is null ? null : Authenticate(record);
        }
    }

    public List<CredentialRecord> FetchAllCredentials()
    {
        lock (_gate)
        {
            return QueryCredentials("SELECT * FROM credentials WHERE deleted_at IS NULL;")
                .Select(Authenticate).ToList();
        }
    }

    public List<CredentialRecord> FetchRecycledCredentials()
    {
        lock (_gate)
        {
            return QueryCredentials("SELECT * FROM credentials WHERE deleted_at IS NOT NULL ORDER BY deleted_at;")
                .Select(Authenticate).ToList();
        }
    }

    internal List<CredentialRecord> FetchAllCredentialsIncludingRecycledRaw()
        => QueryCredentials("SELECT * FROM credentials;");

    public List<CredentialRecord> FetchAllCredentialsIncludingRecycled()
    {
        lock (_gate)
        {
            return FetchAllCredentialsIncludingRecycledRaw().Select(Authenticate).ToList();
        }
    }

    private CredentialRecord? FetchCredentialRaw(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM credentials WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCredential(reader) : null;
    }

    private CredentialRecord? QueryCredential(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCredential(reader) : null;
    }

    private List<CredentialRecord> QueryCredentialsWithParameters(string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var result = new List<CredentialRecord>();
        while (reader.Read()) result.Add(ReadCredential(reader));
        return result;
    }

    private List<CredentialRecord> QueryCredentials(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<CredentialRecord>();
        while (reader.Read()) result.Add(ReadCredential(reader));
        return result;
    }

    private static CredentialRecord ReadCredential(SqliteDataReader reader)
    {
        return new CredentialRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            NameIndex = (byte[])reader["name_index"],
            EncryptedDisplayName = (byte[])reader["encrypted_display_name"],
            EncryptedPayload = (byte[])reader["encrypted_payload"],
            EncryptedUsageInstructions = (byte[])reader["encrypted_usage_instructions"],
            EncryptedPrivateNotes = (byte[])reader["encrypted_private_notes"],
            EncryptedGroupName = NullableBytes(reader["encrypted_group_name"]),
            EncryptedEnvironmentVariable = NullableBytes(reader["encrypted_environment_variable"]),
            PayloadKind = reader.GetString(reader.GetOrdinal("payload_kind")),
            Permission = reader.GetString(reader.GetOrdinal("permission")),
            ExpiresAt = reader["expires_at"] as string,
            CreatedAt = reader.GetString(reader.GetOrdinal("created_at")),
            UpdatedAt = reader.GetString(reader.GetOrdinal("updated_at")),
            EncryptedOriginalFilename = NullableBytes(reader["encrypted_original_filename"]),
            ByteSize = reader["byte_size"] is long size ? checked((int)size) : null,
            ContentDigest = NullableBytes(reader["content_digest"]),
            DeletedAt = reader["deleted_at"] as string,
            AuthenticationTag = NullableBytes(reader["authentication_tag"]),
        };
    }

    private static byte[]? NullableBytes(object value) => value is byte[] bytes ? bytes : null;

    public void RecycleCredential(string id, string deletedAt)
    {
        lock (_gate)
        {
            var record = FetchCredentialRaw(id) ?? throw VaultException.NotFound(id);
            var authenticated = Authenticate(record);
            if (authenticated.DeletedAt is not null) throw VaultException.NotFound(id);
            authenticated.DeletedAt = deletedAt;
            UpdateCredential(authenticated);
        }
    }

    public void RestoreCredential(string id)
    {
        lock (_gate)
        {
            var record = FetchCredentialRaw(id) ?? throw VaultException.NotFound(id);
            var authenticated = Authenticate(record);
            if (authenticated.DeletedAt is null) throw VaultException.NotFound(id);
            authenticated.DeletedAt = null;
            UpdateCredential(authenticated);
        }
    }

    public void DeleteRecycledCredential(string id)
    {
        lock (_gate)
        {
            var record = FetchCredentialRaw(id);
            if (record is not null) Authenticate(record);
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM credentials WHERE id = $id AND deleted_at IS NOT NULL;";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) throw VaultException.NotFound(id);
        }
    }

    public int PurgeRecycledCredentials(string deletedOnOrBefore)
    {
        lock (_gate)
        {
            var records = QueryCredentialsWithParameters(
                "SELECT * FROM credentials WHERE deleted_at IS NOT NULL AND deleted_at <= $cutoff;",
                ("$cutoff", deletedOnOrBefore));
            foreach (var record in records) Authenticate(record);
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM credentials WHERE deleted_at IS NOT NULL AND deleted_at <= $cutoff;";
            command.Parameters.AddWithValue("$cutoff", deletedOnOrBefore);
            return command.ExecuteNonQuery();
        }
    }

    // MARK: - Groups

    public List<string> EditCredentialGroup(string name, byte[] key, bool deleting, string updatedAt)
    {
        lock (_gate)
        {
            const string configKey = VaultGroups.ConfigKey;
            var groups = new HashSet<string>(VaultGroups.StoredGroups(ConfigValue(configKey), key), StringComparer.Ordinal);
            if (deleting) groups.Remove(name); else groups.Add(name);
            var plaintext = System.Text.Json.JsonSerializer.Serialize(
                groups.OrderBy(x => x, StringComparer.Ordinal).ToList());
            SetConfigValue(configKey, Convert.ToBase64String(VaultCrypto.Encrypt(plaintext, key)));

            var cleared = new List<string>();
            if (!deleting) return cleared;
            foreach (var fetched in FetchAllCredentialsIncludingRecycledRaw())
            {
                var record = Authenticate(fetched);
                if (record.EncryptedGroupName is not { } encrypted) continue;
                if (VaultCrypto.Decrypt(encrypted, key) != name) continue;
                record.EncryptedGroupName = null;
                record.UpdatedAt = updatedAt;
                UpdateCredential(record);
                cleared.Add(record.Id);
            }
            return cleared;
        }
    }

    // MARK: - Access records

    internal void InsertCredentialAccessRecord(CredentialAccessRecord record, int capacity)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO credential_access_records (id, encrypted_record) VALUES ($id, $record);
                DELETE FROM credential_access_records
                WHERE rowid NOT IN (
                    SELECT rowid FROM credential_access_records ORDER BY rowid DESC LIMIT $capacity
                );
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$record", record.EncryptedRecord);
            command.Parameters.AddWithValue("$capacity", capacity);
            command.ExecuteNonQuery();
        }
    }

    internal List<CredentialAccessRecord> FetchCredentialAccessRecords()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, encrypted_record FROM credential_access_records ORDER BY rowid DESC;";
            using var reader = command.ExecuteReader();
            var result = new List<CredentialAccessRecord>();
            while (reader.Read())
            {
                result.Add(new CredentialAccessRecord
                {
                    Id = reader.GetString(0),
                    EncryptedRecord = (byte[])reader[1],
                });
            }
            return result;
        }
    }

    public void DeleteCredentialAccessRecords(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM credential_access_records WHERE id = $id;";
            var parameter = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in ids)
            {
                parameter.Value = id;
                command.ExecuteNonQuery();
            }
        }
    }

    public void DeleteAllCredentialAccessRecords()
    {
        lock (_gate) { ExecuteRaw("DELETE FROM credential_access_records;"); }
    }

    public void FailCredentialAccessRecordWritesForTesting()
    {
        lock (_gate) { ExecuteRaw("""
            CREATE TRIGGER fail_credential_access_record_write
            BEFORE INSERT ON credential_access_records
            BEGIN SELECT RAISE(FAIL, 'injected access record failure'); END;
            """);
        }
    }

    // MARK: - Agent write receipts

    internal AgentWriteOperationRecord? FetchAgentWriteOperation(string operationId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM agent_write_operations WHERE operation_id = $id;";
            command.Parameters.AddWithValue("$id", operationId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadWriteOperation(reader) : null;
        }
    }

    internal AgentWriteOperationRecord? FetchAgentFileWriteReceipt(string requestId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM agent_write_operations WHERE request_id = $id;";
            command.Parameters.AddWithValue("$id", requestId);
            using var reader = command.ExecuteReader();
            AgentWriteOperationRecord? result = null;
            var count = 0;
            while (reader.Read())
            {
                count++;
                result = ReadWriteOperation(reader);
            }
            if (count > 1) throw VaultException.Database("Multiple write receipts share one request id.");
            return result;
        }
    }

    private static AgentWriteOperationRecord ReadWriteOperation(SqliteDataReader reader) => new()
    {
        OperationId = reader.GetString(reader.GetOrdinal("operation_id")),
        PayloadDigest = reader.GetString(reader.GetOrdinal("payload_digest")),
        CredentialId = reader.GetString(reader.GetOrdinal("credential_id")),
        Operation = reader.GetString(reader.GetOrdinal("operation")),
        CommittedAt = reader.GetString(reader.GetOrdinal("committed_at")),
        RequestId = reader.GetString(reader.GetOrdinal("request_id")),
        CapabilityDigest = reader.GetString(reader.GetOrdinal("capability_digest")),
        ResultDigest = reader["result_digest"] as string,
    };

    // MARK: - Raw helpers (tests and migration verification)

    internal void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        lock (_gate) { ExecuteRaw(sql, parameters); }
    }

    internal long ScalarLong(string sql)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    internal List<string> QueryStrings(string sql)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    internal List<Dictionary<string, object?>> QueryRows(string sql)
    {
        lock (_gate)
        {
            using var reader = Query(sql);
            return ReadAll(reader);
        }
    }

    internal bool TableExists(string name)
    {
        lock (_gate) return TableExistsRaw(name);
    }

    private bool TableExistsRaw(string name)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private SqliteDataReader Query(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteReader();
    }

    private static List<Dictionary<string, object?>> ReadAll(SqliteDataReader reader)
    {
        var result = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }
            result.Add(row);
        }
        return result;
    }

    private void ExecuteRaw(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}





public sealed partial class VaultStore
{
    // MARK: - Transactional commit

    /// <summary>
    /// Runs a body inside one BEGIN IMMEDIATE/COMMIT on this connection. Nested
    /// helpers share the connection, so the whole business write is atomic.
    /// </summary>
    internal T RunTransaction<T>(Func<T> body)
    {
        lock (_gate)
        {
            ExecuteRaw("BEGIN IMMEDIATE;");
            try
            {
                var result = body();
                ExecuteRaw("COMMIT;");
                return result;
            }
            catch
            {
                try { ExecuteRaw("ROLLBACK;"); } catch (Microsoft.Data.Sqlite.SqliteException) { }
                throw;
            }
        }
    }

    // MARK: - Agent write receipts

    internal void InsertAgentWriteOperation(AgentWriteOperationRecord record)
    {
        lock (_gate)
        {
            ExecuteRaw("""
                INSERT INTO agent_write_operations (operation_id, payload_digest, credential_id, operation,
                    committed_at, request_id, capability_digest, result_digest)
                VALUES ($operationId, $payloadDigest, $credentialId, $operation, $committedAt,
                    $requestId, $capabilityDigest, $resultDigest);
                """,
                ("$operationId", record.OperationId),
                ("$payloadDigest", record.PayloadDigest),
                ("$credentialId", record.CredentialId),
                ("$operation", record.Operation),
                ("$committedAt", record.CommittedAt),
                ("$requestId", record.RequestId),
                ("$capabilityDigest", record.CapabilityDigest),
                ("$resultDigest", (object?)record.ResultDigest ?? DBNull.Value));
        }
    }

    internal bool HasAgentWriteOperation(string operationId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM agent_write_operations WHERE operation_id = $id;";
            command.Parameters.AddWithValue("$id", operationId);
            return command.ExecuteScalar() is not null;
        }
    }

    // MARK: - Transactional helpers

    internal CredentialRecord? FetchCredentialRawInTransaction(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM credentials WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCredential(reader) : null;
    }

    internal void InsertCredentialInTransaction(CredentialRecord record)
    {
        var sealedRecord = SealForPersistence(record);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO credentials (id, name_index, encrypted_display_name, encrypted_payload,
                encrypted_usage_instructions, encrypted_private_notes, encrypted_group_name,
                encrypted_environment_variable, payload_kind, permission, expires_at, created_at, updated_at,
                encrypted_original_filename, byte_size, content_digest, deleted_at, authentication_tag)
            VALUES ($id, $nameIndex, $displayName, $payload, $usage, $notes, $group, $environment,
                $kind, $permission, $expires, $created, $updated, $filename, $size, $digest, $deleted, $tag);
            """;
        AddCredentialParameters(command, sealedRecord);
        command.ExecuteNonQuery();
    }

    internal void UpdateCredentialInTransaction(CredentialRecord record)
    {
        var sealedRecord = SealForPersistence(record);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE credentials SET name_index=$nameIndex, encrypted_display_name=$displayName,
                encrypted_payload=$payload, encrypted_usage_instructions=$usage,
                encrypted_private_notes=$notes, encrypted_group_name=$group,
                encrypted_environment_variable=$environment, payload_kind=$kind, permission=$permission,
                expires_at=$expires, created_at=$created, updated_at=$updated,
                encrypted_original_filename=$filename, byte_size=$size, content_digest=$digest,
                deleted_at=$deleted, authentication_tag=$tag
            WHERE id=$id;
            """;
        AddCredentialParameters(command, sealedRecord);
        command.ExecuteNonQuery();
    }

    internal string? ConfigValueInTransaction(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM config WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    internal void SetConfigValueInTransaction(string key, string? value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = value is null
            ? "DELETE FROM config WHERE key = $key;"
            : "INSERT OR REPLACE INTO config (key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
