﻿using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace CoreSync.SqlServer
{
    public class SqlSyncProvider : ISyncProvider
    {
        private bool _initialized = false;
        private Guid _storeId;

        public SqlSyncProvider(SqlSyncConfiguration configuration)
        {
            Configuration = configuration;
        }

        public SqlSyncConfiguration Configuration { get; }

        public async Task<SyncAnchor> ApplyChangesAsync([NotNull] SyncChangeSet changeSet, Func<SyncItem, ConflictResolution> onConflictFunc = null)
        {
            Validate.NotNull(changeSet, nameof(changeSet));

            await InitializeAsync();

            if (changeSet.TargetAnchor.StoreId != _storeId)
            {
                throw new InvalidOperationException("ChangeSet doesn't target this store");
            }

            using (var c = new SqlConnection(Configuration.ConnectionString))
            {
                await c.OpenAsync();

                using (var cmd = new SqlCommand())
                {
                    using (var tr = c.BeginTransaction(IsolationLevel.Snapshot))
                    {
                        cmd.Connection = c;
                        cmd.Transaction = tr;
                        cmd.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION()";

                        long version = (long)await cmd.ExecuteScalarAsync();
                        bool atLeastOneChangeApplied = false;

                        foreach (var item in changeSet.Items)
                        {
                            var table = (SqlSyncTable)Configuration.Tables.First(_ => _.Name == item.Table.Name);

                            cmd.Parameters.Clear();
                            cmd.CommandText = $"SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('{table.Schema}.[{table.Name}]'))";

                            long minVersionForTable = (long)await cmd.ExecuteScalarAsync();

                            if (changeSet.TargetAnchor.Version < minVersionForTable)
                                throw new InvalidOperationException($"Unable to apply changes, version of data to apply ({changeSet.TargetAnchor.Version}) for table '{table.Schema}.[{table.Name}]' is too old (min valid version {minVersionForTable})");

                            bool syncForceWrite = false;
                            var itemChangeType = item.ChangeType;

                        retryWrite:
                            cmd.Parameters.Clear();

                            switch (itemChangeType)
                            {
                                case ChangeType.Insert:
                                    cmd.CommandText = table.InsertQuery;
                                    break;

                                case ChangeType.Update:
                                    cmd.CommandText = table.UpdateQuery;
                                    break;

                                case ChangeType.Delete:
                                    cmd.CommandText = table.DeleteQuery;
                                    break;
                            }

                            cmd.Parameters.Add(new SqlParameter("@last_sync_version", changeSet.TargetAnchor.Version));
                            cmd.Parameters.Add(new SqlParameter("@sync_force_write", syncForceWrite));

                            foreach (var valueItem in item.Values)
                                cmd.Parameters.Add(new SqlParameter("@" + valueItem.Key.Replace(" ", "_"), valueItem.Value ?? DBNull.Value));

                            var affectedRows = cmd.ExecuteNonQuery();

                            if (affectedRows == 0)
                            {
                                if (itemChangeType == ChangeType.Insert)
                                {
                                    //If we can't apply an insert means that we already
                                    //applied the insert or another record with same values (see primary key)
                                    //is already present in table.
                                    //In any case we can't proceed
                                    throw new InvalidSyncOperationException(new SyncAnchor(_storeId, changeSet.TargetAnchor.Version + 1));
                                }
                                else if (itemChangeType == ChangeType.Update ||
                                    itemChangeType == ChangeType.Delete)
                                {
                                    if (syncForceWrite)
                                    {
                                        if (itemChangeType == ChangeType.Delete)
                                        {
                                            //item is already deleted in data store
                                            //so this means that we're going to delete a already deleted record
                                            //i.e. nothing to do
                                        }
                                        else
                                        {
                                            //if user wants to update forcely a deletes record means
                                            //he wants to actually insert it again in store
                                            itemChangeType = ChangeType.Insert;
                                            goto retryWrite;
                                        }
                                    }
                                    //conflict detected
                                    var res = onConflictFunc?.Invoke(item);
                                    if (res.HasValue && res.Value == ConflictResolution.ForceWrite)
                                    {
                                        syncForceWrite = true;
                                        goto retryWrite;
                                    }
                                }
                            }
                            else
                                atLeastOneChangeApplied = true;
                        }

                        var newAnchor = new SyncAnchor(_storeId, version + (atLeastOneChangeApplied ? 1 : 0));

                        cmd.Parameters.Clear();
                        cmd.CommandText = "UPDATE [__CORE_SYNC_REMOTE_ANCHOR] SET [VERSION] = @version WHERE [ID] = @id";
                        cmd.Parameters.AddWithValue("@id", changeSet.SourceAnchor.StoreId);
                        cmd.Parameters.AddWithValue("@version", newAnchor.Version);

                        if (0 == await cmd.ExecuteNonQueryAsync())
                        {
                            cmd.Parameters.Clear();
                            cmd.CommandText = "INSERT INTO [__CORE_SYNC_REMOTE_ANCHOR] ([ID], [VERSION]) VALUES (@id, @version)";
                            cmd.Parameters.AddWithValue("@id", changeSet.SourceAnchor.StoreId);
                            cmd.Parameters.AddWithValue("@version", newAnchor.Version);

                            await cmd.ExecuteNonQueryAsync();
                        }

                        tr.Commit();

                        return newAnchor;
                    }
                }
            }
        }

        public async Task ApplyProvisionAsync()
        {
            using (var c = new SqlConnection(Configuration.ConnectionString))
            {
                await c.OpenAsync();

                await c.EnableChangeTrakingAsync();
            }
        }

        public async Task<SyncChangeSet> GetChangesAsync(Guid otherStoreId)
        {
            if (otherStoreId == Guid.Empty)
            {
                throw new ArgumentException("Invalid store id", nameof(otherStoreId));
            }

            var lastAnchor = await GetLastAnchorForStoreAsync(otherStoreId);

            if (lastAnchor == null)
            {
                return await GetInitialSetAsync(otherStoreId);
            }

            return await GetIncrementalChangesAsync(lastAnchor);
        }

        private async Task<SyncChangeSet> GetIncrementalChangesAsync(SyncAnchor otherStoreAnchor)
        {
            Validate.NotNull(otherStoreAnchor, nameof(otherStoreAnchor));

            await InitializeAsync();

            using (var c = new SqlConnection(Configuration.ConnectionString))
            {
                await c.OpenAsync();

                using (var cmd = new SqlCommand())
                {
                    var items = new List<SqlSyncItem>();

                    using (var tr = c.BeginTransaction(IsolationLevel.Snapshot))
                    {
                        cmd.Connection = c;
                        cmd.Transaction = tr;

                        cmd.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION()";

                        long version = (long)await cmd.ExecuteScalarAsync();

                        foreach (SqlSyncTable table in Configuration.Tables)
                        {
                            cmd.CommandText = $"SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('{table.Schema}.[{table.Name}]'))";

                            long minVersionForTable = (long)await cmd.ExecuteScalarAsync();

                            if (otherStoreAnchor.Version < minVersionForTable)
                                throw new InvalidOperationException($"Unable to get changes, version of data requested ({otherStoreAnchor.Version}) for table '{table.Schema}.[{table.Name}]' is too old (min valid version {minVersionForTable})");

                            cmd.CommandText = table.IncrementalDataQuery.Replace("@last_synchronization_version", otherStoreAnchor.Version.ToString());

                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    var values = Enumerable.Range(0, r.FieldCount).ToDictionary(_ => r.GetName(_), _ => r.GetValue(_));
                                    items.Add(new SqlSyncItem(table, DetectChangeType(values), values));
                                }
                            }
                        }

                        tr.Commit();

                        return new SyncChangeSet(new SyncAnchor(_storeId, version), otherStoreAnchor, items);
                    }
                }
            }
        }

        private async Task<SyncChangeSet> GetInitialSetAsync(Guid otherStoreId)
        {
            await InitializeAsync();

            using (var c = new SqlConnection(Configuration.ConnectionString))
            {
                await c.OpenAsync();

                using (var cmd = new SqlCommand())
                {
                    var items = new List<SqlSyncItem>();

                    using (var tr = c.BeginTransaction(IsolationLevel.Snapshot))
                    {
                        cmd.Connection = c;
                        cmd.Transaction = tr;

                        cmd.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION()";

                        long version = (long)await cmd.ExecuteScalarAsync();

                        foreach (SqlSyncTable table in Configuration.Tables)
                        {
                            cmd.CommandText = table.InitialDataQuery;

                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    var values = Enumerable.Range(0, r.FieldCount).ToDictionary(_ => r.GetName(_), _ => r.GetValue(_));
                                    items.Add(new SqlSyncItem(table, DetectChangeType(values), values));
                                }
                            }
                        }

                        tr.Commit();

                        return new SyncChangeSet(new SyncAnchor(_storeId, version), new SyncAnchor(otherStoreId, 0), items);
                    }
                }
            }
        }

        private async Task<SyncAnchor> GetLastAnchorForStoreAsync(Guid otherStoreId)
        {
            await InitializeAsync();

            using (var c = new SqlConnection(Configuration.ConnectionString))
            {
                await c.OpenAsync();

                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT [VERSION] FROM [dbo].[__CORE_SYNC_REMOTE_ANCHOR] WHERE [ID] = @storeId";
                    cmd.Parameters.AddWithValue("@storeId", otherStoreId);

                    var version = await cmd.ExecuteScalarAsync();

                    if (version == null)
                        return null;

                    return new SyncAnchor(otherStoreId, (long)version);
                }
            }
        }

        public async Task<Guid> GetStoreIdAsync()
        {
            await InitializeAsync();

            return _storeId;
        }

        public async Task RemoveProvisionAsync()
        {
            using (var c = new SqlConnection(Configuration.ConnectionString))
            {
                await c.OpenAsync();

                await c.DisableChangeTrakingAsync();
            }
        }

        private ChangeType DetectChangeType(Dictionary<string, object> values)
        {
            if (values.TryGetValue("SYS_CHANGE_OPERATION", out var syncChangeOperation))
            {
                switch (syncChangeOperation.ToString())
                {
                    case "I":
                        return ChangeType.Insert;

                    case "U":
                        return ChangeType.Update;

                    case "D":
                        return ChangeType.Delete;

                    default:
                        throw new NotSupportedException();
                }
            }

            return ChangeType.Insert;
        }

        private async Task InitializeAsync()
        {
            if (_initialized)
                return;

            var connStringBuilder = new SqlConnectionStringBuilder(Configuration.ConnectionString);
            if (string.IsNullOrWhiteSpace(connStringBuilder.InitialCatalog))
                throw new InvalidOperationException("Invalid connection string: InitialCatalog property is missing");

            using (var connection = new SqlConnection(Configuration.ConnectionString))
            {
                await connection.OpenAsync();

                if (!await connection.GetIsChangeTrakingEnabledAsync())
                {
                    await connection.EnableChangeTrakingAsync();
                }

                if (!await connection.SetSnapshotIsolationAsync())
                {
                    await connection.SetSnapshotIsolationAsync(true);
                }

                var tableNames = await connection.GetTableNamesAsync();
                if (!tableNames.Contains("__CORE_SYNC_REMOTE_ANCHOR"))
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = $@"CREATE TABLE [dbo].[__CORE_SYNC_REMOTE_ANCHOR](
	[ID] [uniqueidentifier] NOT NULL,
	[VERSION] [BIGINT] NOT NULL
 CONSTRAINT [PK___CORE_SYNC_REMOTE_ANCHOR] PRIMARY KEY CLUSTERED
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]";
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                if (!tableNames.Contains("__CORE_SYNC_LOCAL_ID"))
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = $@"CREATE TABLE [dbo].[__CORE_SYNC_LOCAL_ID](
	[ID] [uniqueidentifier] NOT NULL
 CONSTRAINT [PK___CORE_SYNC_LOCAL_ID] PRIMARY KEY CLUSTERED
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]";
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT TOP 1 [ID] FROM [__CORE_SYNC_LOCAL_ID]";
                    var localId = await cmd.ExecuteScalarAsync();
                    if (localId == null)
                    {
                        localId = Guid.NewGuid();
                        cmd.CommandText = $"INSERT INTO [__CORE_SYNC_LOCAL_ID] ([ID]) VALUES (@id)";
                        cmd.Parameters.Add(new SqlParameter("@id", localId));
                        if (1 != await cmd.ExecuteNonQueryAsync())
                        {
                            throw new InvalidOperationException();
                        }
                        cmd.Parameters.Clear();
                    }

                    _storeId = (Guid)localId;
                }

                foreach (SqlSyncTable table in Configuration.Tables)
                {
                    if (!await connection.GetIsChangeTrakingEnabledAsync(table))
                    {
                        await connection.EnableChangeTrakingAsync(table);
                    }

                    var primaryKeyIndexName = (await connection.GetClusteredPrimaryKeyIndexesAsync(table)).FirstOrDefault(); //dbTable.Indexes.Cast<Index>().FirstOrDefault(_ => _.IsClustered && _.IndexKeyType == IndexKeyType.DriPrimaryKey);
                    if (primaryKeyIndexName == null)
                    {
                        throw new InvalidOperationException($"Table '{table.NameWithSchema}' doesn't have a primary key");
                    }

                    var primaryKeyColumns = await connection.GetIndexColumnNamesAsync(table, primaryKeyIndexName); //primaryKeyIndexName.IndexedColumns.Cast<IndexedColumn>().ToList();
                    var allColumns = await connection.GetTableColumnNamesAsync(table); //dbTable.Columns.Cast<Column>().ToList();
                    var tableColumns = allColumns.Where(_ => !primaryKeyColumns.Any(kc => kc == _)).ToList();

                    table.InitialDataQuery = $@"SELECT
                {string.Join(", ", allColumns.Select(_ => "[" + _ + "]"))}
            FROM
                {table.NameWithSchema}";

                    table.IncrementalDataQuery = $@"SELECT
                {string.Join(", ", primaryKeyColumns.Select(_ => "CT.[" + _ + "]"))} {(tableColumns.Any() ? ", " + string.Join(", ", tableColumns.Select(_ => "T.[" + _ + "]")) : string.Empty)},
                CT.SYS_CHANGE_OPERATION, CT.SYS_CHANGE_COLUMNS, CT.SYS_CHANGE_CONTEXT
            FROM
                {table.NameWithSchema} AS T
            RIGHT OUTER JOIN
                CHANGETABLE(CHANGES {table.NameWithSchema}, @last_synchronization_version) AS CT
            ON
                {string.Join(" AND ", primaryKeyColumns.Select(_ => $"T.[{_}] = CT.[{_}]"))}";

                    table.InsertQuery = $@"INSERT INTO {table.NameWithSchema} ({string.Join(", ", allColumns.Select(_ => $"[{_}]"))}) SELECT {string.Join(", ", allColumns.Select(_ => "@" + _.Replace(' ', '_')))} EXCEPT
   SELECT {string.Join(", ", allColumns.Select(_ => $"[{_}]"))} FROM {table.NameWithSchema};";

                    table.DeleteQuery = $@"DELETE FROM {table.NameWithSchema}
FROM
    {table.NameWithSchema}
JOIN CHANGETABLE(VERSION {table.NameWithSchema}, ({string.Join(", ", primaryKeyColumns.Select(_ => "[" + _ + "]"))}), ({string.Join(", ", primaryKeyColumns.Select(_ => "@" + _.Replace(' ', '_')))})) CT  ON {string.Join(" AND ", primaryKeyColumns.Select(_ => $"CT.[{_}] = {table.Schema}.[{table.Name}].[{_}]"))}
WHERE
    @sync_force_write = 1 OR @last_sync_version >= ISNULL(CT.SYS_CHANGE_VERSION, 0)";

                    table.UpdateQuery = $@"UPDATE {table.NameWithSchema}
SET
    {string.Join(", ", tableColumns.Select(_ => "[" + _ + "] = @" + _.Replace(" ", "_")))}
FROM
    {table.NameWithSchema}
JOIN CHANGETABLE(VERSION {table.NameWithSchema}, ({string.Join(", ", primaryKeyColumns.Select(_ => $"[{_}]"))}), ({string.Join(", ", primaryKeyColumns.Select(_ => "@" + _.Replace(' ', '_')))})) CT ON {string.Join(" AND ", primaryKeyColumns.Select(_ => $"CT.[{_}] = {table.Schema}.[{table.Name}].[{_}]"))}
WHERE
    @sync_force_write = 1 OR @last_sync_version >= ISNULL(CT.SYS_CHANGE_VERSION, 0)";
                }
            }

            _initialized = true;
        }
    }
}