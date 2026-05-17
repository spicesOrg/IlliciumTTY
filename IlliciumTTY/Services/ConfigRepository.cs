using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using IlliciumTTY.Models;
using Microsoft.Data.Sqlite;

namespace IlliciumTTY.Services;

public sealed class ConfigRepository
{
    private const string StorageVersion = "3";
    private readonly SemaphoreSlim _databaseGate = new(1, 1);

    private readonly JsonSerializerOptions _legacyJsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public ConfigRepository()
    {
        ConfigPath = Path.Combine(ResolveWritableDataDirectory(), "illiciumtty.db");
    }

    public string ConfigPath { get; }

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            if (await HasNormalizedConfigAsync(connection, cancellationToken))
            {
                var config = await ReadNormalizedConfigAsync(connection, cancellationToken);
                await DropLegacySnapshotTableAsync(connection, cancellationToken);
                return config;
            }

            var migratedConfig = await TryLoadLegacySqliteSnapshotAsync(connection, cancellationToken)
                                 ?? await TryLoadLegacyJsonAsync(cancellationToken);

            if (migratedConfig is not null)
            {
                migratedConfig.TemporaryNodes = [];
                await WriteNormalizedConfigAsync(connection, migratedConfig, cancellationToken);
                await DropLegacySnapshotTableAsync(connection, cancellationToken);
                return migratedConfig;
            }

            var defaultConfig = CreateDefaultConfig();
            await WriteNormalizedConfigAsync(connection, defaultConfig, cancellationToken);
            return defaultConfig;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await WriteNormalizedConfigAsync(connection, config, cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ConfigPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var timeoutCommand = connection.CreateCommand())
        {
            timeoutCommand.CommandText = "PRAGMA busy_timeout = 5000;";
            await timeoutCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var journalCommand = connection.CreateCommand())
        {
            journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
            await journalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE IF NOT EXISTS app_settings (
                                  key TEXT PRIMARY KEY NOT NULL,
                                  value TEXT NOT NULL,
                                  updated_at TEXT NOT NULL
                              );

                              CREATE TABLE IF NOT EXISTS connection_nodes (
                                  id TEXT PRIMARY KEY NOT NULL,
                                  node_type TEXT NOT NULL,
                                  name TEXT NOT NULL,
                                  parent_id TEXT NULL,
                                  group_type TEXT NOT NULL,
                                  sort_order INTEGER NOT NULL,
                                  created_at TEXT NOT NULL,
                                  updated_at TEXT NOT NULL,
                                  host TEXT NULL,
                                  port INTEGER NOT NULL,
                                  username TEXT NULL,
                                  auth_type TEXT NOT NULL,
                                  password_ref TEXT NULL,
                                  private_key_path TEXT NULL,
                                  passphrase_ref TEXT NULL,
                                  default_remote_path TEXT NULL
                              );

                              CREATE TABLE IF NOT EXISTS connection_secrets (
                                  ref TEXT PRIMARY KEY NOT NULL,
                                  secret_kind TEXT NOT NULL,
                                  protected_value TEXT NOT NULL,
                                  updated_at TEXT NOT NULL
                              );

                              CREATE INDEX IF NOT EXISTS ix_connection_nodes_parent
                                  ON connection_nodes(parent_id, sort_order);

                              CREATE INDEX IF NOT EXISTS ix_connection_nodes_group
                                  ON connection_nodes(group_type, sort_order);

                              CREATE TABLE IF NOT EXISTS expanded_nodes (
                                  node_id TEXT PRIMARY KEY NOT NULL
                              );
                              """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasNormalizedConfigAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var settingsCommand = connection.CreateCommand())
        {
            settingsCommand.CommandText = "SELECT COUNT(*) FROM app_settings WHERE key = 'storage_version';";
            var settingsCount = Convert.ToInt32(await settingsCommand.ExecuteScalarAsync(cancellationToken));
            if (settingsCount > 0)
            {
                return true;
            }
        }

        await using (var nodesCommand = connection.CreateCommand())
        {
            nodesCommand.CommandText = "SELECT COUNT(*) FROM connection_nodes;";
            var nodeCount = Convert.ToInt32(await nodesCommand.ExecuteScalarAsync(cancellationToken));
            return nodeCount > 0;
        }
    }

    private async Task<AppConfig> ReadNormalizedConfigAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var allNodes = await ReadNodesAsync(connection, cancellationToken);
        var secrets = await ReadSecretsAsync(connection, cancellationToken);
        HydrateTransientSecrets(allNodes, secrets);
        var roots = BuildTree(allNodes);
        var hasStoredTemporaryNodes = allNodes.Any(node => node.GroupType == GroupType.Temporary);

        var config = new AppConfig
        {
            FavoriteNodes = roots
                .Where(node => node.GroupType == GroupType.Favorite)
                .OrderBy(node => node.SortOrder)
                .ToList(),
            TemporaryNodes = [],
            DefaultSyncMode = ReadEnumSetting(settings, "default_sync_mode", SyncMode.Bidirectional),
            LastOpenedNodeId = ReadNullableSetting(settings, "last_opened_node_id"),
            ExpandedNodeIds = await ReadExpandedNodeIdsAsync(connection, cancellationToken),
            SidebarWidth = ReadDoubleSetting(settings, "sidebar_width", 230),
            WorkspaceSplitRatio = ReadDoubleSetting(settings, "workspace_split_ratio", 0.52)
        };

        if (hasStoredTemporaryNodes)
        {
            await WriteNormalizedConfigAsync(connection, config, cancellationToken);
        }

        return config;
    }

    private static async Task<Dictionary<string, string>> ReadSettingsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_settings;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }

        return settings;
    }

    private static async Task<List<ConnectionNodeModel>> ReadNodesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var nodes = new List<ConnectionNodeModel>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT
                                  id,
                                  node_type,
                                  name,
                                  parent_id,
                                  group_type,
                                  sort_order,
                                  created_at,
                                  updated_at,
                                  host,
                                  port,
                                  username,
                                  auth_type,
                                  password_ref,
                                  private_key_path,
                                  passphrase_ref,
                                  default_remote_path
                              FROM connection_nodes
                              ORDER BY group_type, parent_id, sort_order, name;
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add(new ConnectionNodeModel
            {
                Id = reader.GetString(0),
                Type = ReadEnum(reader.GetString(1), NodeType.SshLink),
                Name = reader.GetString(2),
                ParentId = ReadNullableString(reader, 3),
                GroupType = ReadEnum(reader.GetString(4), GroupType.Favorite),
                SortOrder = reader.GetInt32(5),
                CreatedAt = ReadDateTimeOffset(reader.GetString(6)),
                UpdatedAt = ReadDateTimeOffset(reader.GetString(7)),
                Host = ReadNullableString(reader, 8) ?? string.Empty,
                Port = reader.GetInt32(9),
                Username = ReadNullableString(reader, 10) ?? string.Empty,
                AuthType = ReadEnum(ReadNullableString(reader, 11), AuthType.Password),
                PasswordRef = ReadNullableString(reader, 12),
                PrivateKeyPath = ReadNullableString(reader, 13),
                PassphraseRef = ReadNullableString(reader, 14),
                DefaultRemotePath = ReadNullableString(reader, 15),
                Children = []
            });
        }

        return nodes;
    }

    private static async Task<Dictionary<string, string>> ReadSecretsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ref, protected_value FROM connection_secrets;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var secretRef = reader.GetString(0);
            var protectedValue = reader.GetString(1);
            var secretValue = SecretProtector.Unprotect(protectedValue);
            if (!string.IsNullOrEmpty(secretValue))
            {
                secrets[secretRef] = secretValue;
            }
        }

        return secrets;
    }

    private static List<ConnectionNodeModel> BuildTree(List<ConnectionNodeModel> nodes)
    {
        var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            node.Children = nodes
                .Where(child => child.ParentId == node.Id)
                .OrderBy(child => child.SortOrder)
                .ThenBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        return nodes
            .Where(node => string.IsNullOrWhiteSpace(node.ParentId) || !nodesById.ContainsKey(node.ParentId))
            .OrderBy(node => node.SortOrder)
            .ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<HashSet<string>> ReadExpandedNodeIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT node_id FROM expanded_nodes;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            nodeIds.Add(reader.GetString(0));
        }

        return nodeIds;
    }

    private static async Task WriteNormalizedConfigAsync(
        SqliteConnection connection,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        PrepareSecretReferences(config.FavoriteNodes);
        var referencedSecretRefs = CollectSecretRefs(config.FavoriteNodes).ToHashSet(StringComparer.Ordinal);
        var lastOpenedNodeId = ContainsNodeId(config.FavoriteNodes, config.LastOpenedNodeId)
            ? config.LastOpenedNodeId
            : null;
        var expandedNodeIds = config.ExpandedNodeIds
            .Where(nodeId => ContainsNodeId(config.FavoriteNodes, nodeId))
            .ToList();

        using var transaction = connection.BeginTransaction();

        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM expanded_nodes;", cancellationToken);
        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM connection_nodes;", cancellationToken);
        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM app_settings;", cancellationToken);

        await InsertSettingAsync(connection, transaction, "storage_version", StorageVersion, cancellationToken);
        await InsertSettingAsync(connection, transaction, "default_sync_mode", config.DefaultSyncMode.ToString(),
            cancellationToken);
        await InsertSettingAsync(connection, transaction, "last_opened_node_id", lastOpenedNodeId ?? string.Empty,
            cancellationToken);
        await InsertSettingAsync(connection, transaction, "sidebar_width",
            config.SidebarWidth.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await InsertSettingAsync(connection, transaction, "workspace_split_ratio",
            config.WorkspaceSplitRatio.ToString(CultureInfo.InvariantCulture), cancellationToken);

        foreach (var nodeId in expandedNodeIds)
        {
            await InsertExpandedNodeAsync(connection, transaction, nodeId, cancellationToken);
        }

        for (var i = 0; i < config.FavoriteNodes.Count; i++)
        {
            await InsertNodeTreeAsync(connection, transaction, config.FavoriteNodes[i], null, GroupType.Favorite, i,
                cancellationToken);
        }

        await UpsertSecretsAsync(connection, transaction, config.FavoriteNodes, cancellationToken);
        await DeleteUnreferencedSecretsAsync(connection, transaction, referencedSecretRefs, cancellationToken);

        transaction.Commit();
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSettingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO app_settings (key, value, updated_at)
                              VALUES ($key, $value, $updatedAt);
                              """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertExpandedNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO expanded_nodes (node_id) VALUES ($nodeId);";
        command.Parameters.AddWithValue("$nodeId", nodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertNodeTreeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConnectionNodeModel node,
        string? parentId,
        GroupType groupType,
        int sortOrder,
        CancellationToken cancellationToken)
    {
        await InsertNodeAsync(connection, transaction, node, parentId, groupType, sortOrder, cancellationToken);

        for (var i = 0; i < node.Children.Count; i++)
        {
            await InsertNodeTreeAsync(connection, transaction, node.Children[i], node.Id, groupType, i,
                cancellationToken);
        }
    }

    private static async Task InsertNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConnectionNodeModel node,
        string? parentId,
        GroupType groupType,
        int sortOrder,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO connection_nodes (
                                  id,
                                  node_type,
                                  name,
                                  parent_id,
                                  group_type,
                                  sort_order,
                                  created_at,
                                  updated_at,
                                  host,
                                  port,
                                  username,
                                  auth_type,
                                  password_ref,
                                  private_key_path,
                                  passphrase_ref,
                                  default_remote_path
                              )
                              VALUES (
                                  $id,
                                  $nodeType,
                                  $name,
                                  $parentId,
                                  $groupType,
                                  $sortOrder,
                                  $createdAt,
                                  $updatedAt,
                                  $host,
                                  $port,
                                  $username,
                                  $authType,
                                  $passwordRef,
                                  $privateKeyPath,
                                  $passphraseRef,
                                  $defaultRemotePath
                              );
                              """;
        command.Parameters.AddWithValue("$id", node.Id);
        command.Parameters.AddWithValue("$nodeType", node.Type.ToString());
        command.Parameters.AddWithValue("$name", node.Name);
        command.Parameters.AddWithValue("$parentId", DbValue(parentId));
        command.Parameters.AddWithValue("$groupType", groupType.ToString());
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        command.Parameters.AddWithValue("$createdAt", node.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", node.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$host", DbValue(node.Host));
        command.Parameters.AddWithValue("$port", node.Port);
        command.Parameters.AddWithValue("$username", DbValue(node.Username));
        command.Parameters.AddWithValue("$authType", node.AuthType.ToString());
        command.Parameters.AddWithValue("$passwordRef", DbValue(node.PasswordRef));
        command.Parameters.AddWithValue("$privateKeyPath", DbValue(node.PrivateKeyPath));
        command.Parameters.AddWithValue("$passphraseRef", DbValue(node.PassphraseRef));
        command.Parameters.AddWithValue("$defaultRemotePath", DbValue(node.DefaultRemotePath));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<AppConfig?> TryLoadLegacySqliteSnapshotAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "app_config", cancellationToken))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_config WHERE key = 'config' LIMIT 1;";

        try
        {
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<AppConfig>(json, _legacyJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AppConfig?> TryLoadLegacyJsonAsync(CancellationToken cancellationToken)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            return null;
        }

        var legacyPath = Path.Combine(appData, "IlliciumTTY", "config.json");
        if (!File.Exists(legacyPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(legacyPath);
            return await JsonSerializer.DeserializeAsync<AppConfig>(stream, _legacyJsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task DropLegacySnapshotTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE IF EXISTS app_config;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void HydrateTransientSecrets(
        IEnumerable<ConnectionNodeModel> nodes,
        IReadOnlyDictionary<string, string> secrets)
    {
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.PasswordRef) &&
                secrets.TryGetValue(node.PasswordRef, out var password))
            {
                node.TransientPassword = password;
            }

            if (!string.IsNullOrWhiteSpace(node.PassphraseRef) &&
                secrets.TryGetValue(node.PassphraseRef, out var passphrase))
            {
                node.TransientPassphrase = passphrase;
            }

            HydrateTransientSecrets(node.Children, secrets);
        }
    }

    private static void PrepareSecretReferences(IEnumerable<ConnectionNodeModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Type != NodeType.SshLink)
            {
                node.PasswordRef = null;
                node.PassphraseRef = null;
                node.TransientPassword = null;
                node.TransientPassphrase = null;
                PrepareSecretReferences(node.Children);
                continue;
            }

            if (node.AuthType == AuthType.Password)
            {
                node.PasswordRef = string.IsNullOrWhiteSpace(node.PasswordRef)
                    ? BuildSecretRef(node.Id, "password")
                    : node.PasswordRef;
            }
            else
            {
                node.PasswordRef = null;
                node.TransientPassword = null;
            }

            if (node.AuthType == AuthType.PrivateKey)
            {
                node.PassphraseRef = string.IsNullOrWhiteSpace(node.PassphraseRef)
                    ? BuildSecretRef(node.Id, "passphrase")
                    : node.PassphraseRef;
            }
            else
            {
                node.PassphraseRef = null;
                node.TransientPassphrase = null;
            }

            PrepareSecretReferences(node.Children);
        }
    }

    private static IEnumerable<string> CollectSecretRefs(IEnumerable<ConnectionNodeModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.PasswordRef))
            {
                yield return node.PasswordRef;
            }

            if (!string.IsNullOrWhiteSpace(node.PassphraseRef))
            {
                yield return node.PassphraseRef;
            }

            foreach (var secretRef in CollectSecretRefs(node.Children))
            {
                yield return secretRef;
            }
        }
    }

    private static bool ContainsNodeId(IEnumerable<ConnectionNodeModel> nodes, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        foreach (var node in nodes)
        {
            if (node.Id == id || ContainsNodeId(node.Children, id))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task UpsertSecretsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<ConnectionNodeModel> nodes,
        CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.PasswordRef) &&
                !string.IsNullOrEmpty(node.TransientPassword))
            {
                await UpsertSecretAsync(
                    connection,
                    transaction,
                    node.PasswordRef,
                    "password",
                    node.TransientPassword,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(node.PassphraseRef) &&
                !string.IsNullOrEmpty(node.TransientPassphrase))
            {
                await UpsertSecretAsync(
                    connection,
                    transaction,
                    node.PassphraseRef,
                    "passphrase",
                    node.TransientPassphrase,
                    cancellationToken);
            }

            await UpsertSecretsAsync(connection, transaction, node.Children, cancellationToken);
        }
    }

    private static async Task UpsertSecretAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string secretRef,
        string secretKind,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO connection_secrets (ref, secret_kind, protected_value, updated_at)
                              VALUES ($ref, $secretKind, $protectedValue, $updatedAt)
                              ON CONFLICT(ref) DO UPDATE SET
                                  secret_kind = excluded.secret_kind,
                                  protected_value = excluded.protected_value,
                                  updated_at = excluded.updated_at;
                              """;
        command.Parameters.AddWithValue("$ref", secretRef);
        command.Parameters.AddWithValue("$secretKind", secretKind);
        command.Parameters.AddWithValue("$protectedValue", SecretProtector.Protect(value));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteUnreferencedSecretsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlySet<string> referencedSecretRefs,
        CancellationToken cancellationToken)
    {
        var existingRefs = new List<string>();

        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = "SELECT ref FROM connection_secrets;";
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingRefs.Add(reader.GetString(0));
            }
        }

        foreach (var existingRef in existingRefs.Where(secretRef => !referencedSecretRefs.Contains(secretRef)))
        {
            await using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM connection_secrets WHERE ref = $ref;";
            deleteCommand.Parameters.AddWithValue("$ref", existingRef);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string BuildSecretRef(string nodeId, string secretKind)
    {
        return $"connection/{nodeId}/{secretKind}";
    }

    private static object DbValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static TEnum ReadEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct
    {
        return Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : fallback;
    }

    private static TEnum ReadEnumSetting<TEnum>(
        IReadOnlyDictionary<string, string> settings,
        string key,
        TEnum fallback)
        where TEnum : struct
    {
        return settings.TryGetValue(key, out var value)
            ? ReadEnum(value, fallback)
            : fallback;
    }

    private static string? ReadNullableSetting(IReadOnlyDictionary<string, string> settings, string key)
    {
        return settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static double ReadDoubleSetting(
        IReadOnlyDictionary<string, string> settings,
        string key,
        double fallback)
    {
        return settings.TryGetValue(key, out var value) &&
               double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static DateTimeOffset ReadDateTimeOffset(string value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }

    private static string ResolveWritableDataDirectory()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = Path.Combine(candidate, "IlliciumTTY");
            if (CanWriteToDirectory(directory))
            {
                return directory;
            }
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AppConfig CreateDefaultConfig()
    {
        var now = DateTimeOffset.UtcNow;
        var folderId = Guid.NewGuid().ToString("N");

        return new AppConfig
        {
            FavoriteNodes =
            [
                new ConnectionNodeModel
                {
                    Id = folderId,
                    Type = NodeType.Folder,
                    Name = "常用服务器",
                    GroupType = GroupType.Favorite,
                    SortOrder = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Children =
                    [
                        new ConnectionNodeModel
                        {
                            Type = NodeType.SshLink,
                            Name = "本机 SSH",
                            ParentId = folderId,
                            GroupType = GroupType.Favorite,
                            SortOrder = 0,
                            Host = "localhost",
                            Port = 22,
                            Username = Environment.UserName,
                            AuthType = AuthType.Password,
                            DefaultRemotePath = "~",
                            CreatedAt = now,
                            UpdatedAt = now
                        }
                    ]
                }
            ],
            TemporaryNodes = [],
            DefaultSyncMode = SyncMode.Bidirectional,
            ExpandedNodeIds = [folderId]
        };
    }
}