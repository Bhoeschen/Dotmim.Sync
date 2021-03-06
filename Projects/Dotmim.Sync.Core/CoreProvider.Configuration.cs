﻿using Dotmim.Sync.Data;
using Dotmim.Sync.Data.Surrogate;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Manager;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dotmim.Sync
{
    public abstract partial class CoreProvider
    {
        private SyncConfiguration syncConfiguration;

        /// <summary>
        /// Gets or Sets the Server configuration. Use this property only in Proxy mode, and on server side !
        /// </summary>
        public void SetConfiguration(SyncConfiguration syncConfiguration)
        {
            if (syncConfiguration == null || !syncConfiguration.HasTables)
                throw new ArgumentNullException("syncConfiguration", "Service Configuration must exists and contains at least one table to sync.");

            this.syncConfiguration = syncConfiguration;
        }

        /// <summary>
        /// Generate the DmTable configuration from a given columns list
        /// Validate that all columns are currently supported by the provider
        /// </summary>
        private void ValidateTableFromColumns(DmTable dmTable, List<DmColumn> columns, IDbManagerTable dbManagerTable)
        {
            dmTable.OriginalProvider = this.ProviderTypeName;

            var ordinal = 0;

            if (columns == null || columns.Count <= 0)
                throw new ArgumentNullException("columns", $"{dmTable.TableName} does not contains any columns.");

            // Get PrimaryKey
            var dmTableKeys = dbManagerTable.GetTablePrimaryKeys();

            if (dmTableKeys == null || dmTableKeys.Count == 0)
                throw new MissingPrimaryKeyException($"No Primary Keys in table {dmTable.TableName}, Can't make a synchronization with a table without primary keys.");

            // Check if we have more than one column (excepting primarykeys)
            var columnsNotPkeys = columns.Count(c => !dmTableKeys.Contains(c.ColumnName));

            if (columnsNotPkeys <= 0)
                throw new NotSupportedException($"{dmTable.TableName} does not contains any columns, excepting primary keys.");

            foreach (var column in columns.OrderBy(c => c.Ordinal))
            {
                // First of all validate if the column is currently supported
                if (!Metadata.IsValid(column))
                    throw new NotSupportedException($"The Column {column.ColumnName} of type {column.OriginalTypeName} from provider {this.ProviderTypeName} is not currently supported.");

                var columnNameLower = column.ColumnName.ToLowerInvariant();
                if (columnNameLower == "sync_scope_name"
                    || columnNameLower == "scope_timestamp"
                    || columnNameLower == "scope_is_local"
                    || columnNameLower == "scope_last_sync"
                    || columnNameLower == "create_scope_id"
                    || columnNameLower == "update_scope_id"
                    || columnNameLower == "create_timestamp"
                    || columnNameLower == "update_timestamp"
                    || columnNameLower == "timestamp"
                    || columnNameLower == "sync_row_is_tombstone"
                    || columnNameLower == "last_change_datetime"
                    || columnNameLower == "sync_scope_name"
                    || columnNameLower == "sync_scope_name"
                    )
                    throw new NotSupportedException($"The Column name {column.ColumnName} from provider {this.ProviderTypeName} is a reserved column name. Please choose another column name.");

                dmTable.Columns.Add(column);

                // Gets the datastore owner dbType (could be SqlDbtype, MySqlDbType, SqliteDbType, NpgsqlDbType & so on ...)
                object datastoreDbType = Metadata.ValidateOwnerDbType(column.OriginalTypeName, column.IsUnsigned, column.IsUnicode);

                // once we have the datastore type, we can have the managed type
                Type columnType = Metadata.ValidateType(datastoreDbType);

                // and the DbType
                column.DbType = Metadata.ValidateDbType(column.OriginalTypeName, column.IsUnsigned, column.IsUnicode);

                // Gets the owner dbtype (SqlDbType, OracleDbType, MySqlDbType, NpsqlDbType & so on ...)
                // Sqlite does not have it's own type, so it's DbType too
                column.OriginalDbType = datastoreDbType.ToString();

                // Validate max length
                column.MaxLength = Metadata.ValidateMaxLength(column.OriginalTypeName, column.IsUnsigned, column.IsUnicode, column.MaxLength);

                // Validate if column should be readonly
                column.ReadOnly = Metadata.ValidateIsReadonly(column);

                // set position ordinal
                column.SetOrdinal(ordinal);
                ordinal++;

                // Validate the precision and scale properties
                if (Metadata.IsNumericType(column.OriginalTypeName))
                {
                    if (Metadata.SupportScale(column.OriginalTypeName))
                    {
                        var (p, s) = Metadata.ValidatePrecisionAndScale(column);
                        column.Precision = p;
                        column.PrecisionSpecified = true;
                        column.Scale = s;
                        column.ScaleSpecified = true;
                    }
                    else
                    {
                        column.Precision = Metadata.ValidatePrecision(column);
                        column.PrecisionSpecified = true;
                        column.ScaleSpecified = false;
                    }

                }

            }

            DmColumn[] columnsForKey = new DmColumn[dmTableKeys.Count];

            for (int i = 0; i < dmTableKeys.Count; i++)
            {
                var rowColumn = dmTableKeys[i];
                var columnKey = dmTable.Columns.FirstOrDefault(c => String.Equals(c.ColumnName, rowColumn, StringComparison.InvariantCultureIgnoreCase));
                columnsForKey[i] = columnKey ?? throw new MissingPrimaryKeyException("Primary key found is not present in the columns list");
            }

            // Set the primary Key
            dmTable.PrimaryKey = new DmKey(columnsForKey);
        }

        /// <summary>
        /// Create a simple configuration, based on tables
        /// </summary>
        public async Task<SyncConfiguration> ReadConfigurationAsync(string[] tables)
        {
            // Load the configuration
            var configuration = new SyncConfiguration(tables);
            await this.ReadConfigurationAsync(configuration);
            return configuration;
        }

        /// <summary>
        /// update configuration object with tables desc from server database
        /// </summary>
        private async Task ReadConfigurationAsync(SyncConfiguration syncConfiguration)
        {
            if (syncConfiguration == null || syncConfiguration.Count() == 0)
                throw new ArgumentNullException("syncConfiguration", "Configuration should contains Tables, at least tables with a name");

            DbConnection connection = null;
            DbTransaction transaction;

            try
            {
                using (connection = this.CreateConnection())
                {
                    await connection.OpenAsync();

                    using (transaction = connection.BeginTransaction())
                    {
                        foreach (var dmTable in syncConfiguration)
                        {
                            var builderTable = this.GetDbManager(dmTable.TableName);
                            var tblManager = builderTable.CreateManagerTable(connection, transaction);
                            tblManager.TableName = dmTable.TableName;

                            // get columns list
                            var lstColumns = tblManager.GetTableDefinition();

                            // Validate the column list and get the dmTable configuration object.
                            this.ValidateTableFromColumns(dmTable, lstColumns, tblManager);

                            var relations = tblManager.GetTableRelations();

                            if (relations != null)
                            {
                                foreach (var r in relations)
                                {
                                    DmColumn tblColumn = dmTable.Columns[r.ColumnName];
                                    DmColumn foreignColumn = null;
                                    var foreignTable = syncConfiguration[r.ReferenceTableName];

                                    // Since we can have a table with a foreign key but not the parent table
                                    // It's not a problem, just forget it
                                    if (foreignTable == null)
                                        continue;

                                    foreignColumn = foreignTable.Columns[r.ReferenceColumnName];

                                    if (foreignColumn == null)
                                        throw new NotSupportedException(
                                            $"Foreign column {r.ReferenceColumnName} does not exist in table {r.TableName}");

                                    DmRelation dmRelation = new DmRelation(r.ForeignKey, tblColumn, foreignColumn);

                                    syncConfiguration.ScopeSet.Relations.Add(dmRelation);
                                }
                            }

                        }

                        transaction.Commit();
                    }

                    connection.Close();
                }
            }
            catch (Exception ex)
            {
                throw new SyncException(ex, SyncStage.ConfigurationApplying, this.ProviderTypeName);
            }
            finally
            {
                if (connection != null && connection.State != ConnectionState.Closed)
                    connection.Close();
            }
        }

        /// <summary>
        /// Ensure configuration is correct on both server and client side
        /// </summary>
        public virtual async Task<(SyncContext, SyncConfiguration)> EnsureConfigurationAsync(
            SyncContext context, SyncConfiguration syncConfiguration = null)
        {
            try
            {
                context.SyncStage = SyncStage.ConfigurationApplying;

                // Get cache manager and try to get configuration from cache
                var cacheManager = this.CacheManager;
                var cacheConfiguration = GetCacheConfiguration();

                // if we don't pass config object (configuration == null), we may be in proxy mode, so the config object is handled by a local configuration object.
                if (syncConfiguration == null && this.syncConfiguration == null)
                    throw new ArgumentNullException("syncConfiguration", "You try to set a provider with no configuration object");

                // the configuration has been set from the proxy server itself, use it.
                if (syncConfiguration == null && this.syncConfiguration != null)
                    syncConfiguration = this.syncConfiguration;

                // Raise event before
                context.SyncStage = SyncStage.ConfigurationApplying;
                var beforeArgs2 = new ConfigurationApplyingEventArgs(this.ProviderTypeName, context.SyncStage);
                this.TryRaiseProgressEvent(beforeArgs2, this.ConfigurationApplying);
                bool overWriteConfiguration = beforeArgs2.OverwriteConfiguration;

                // if we have already a cache configuration, we can return, except if we should overwrite it
                if (cacheConfiguration != null && !overWriteConfiguration)
                {
                    // Raise event after
                    context.SyncStage = SyncStage.ConfigurationApplied;
                    var afterArgs2 = new ConfigurationAppliedEventArgs(this.ProviderTypeName, context.SyncStage, cacheConfiguration);
                    this.TryRaiseProgressEvent(afterArgs2, this.ConfigurationApplied);

                    // if config has been changed by user, save it again                        
                    this.SetCacheConfiguration(cacheConfiguration);
                    return (context, cacheConfiguration);
                }

                // create local directory
                if (!String.IsNullOrEmpty(syncConfiguration.BatchDirectory) && !Directory.Exists(syncConfiguration.BatchDirectory))
                    Directory.CreateDirectory(syncConfiguration.BatchDirectory);

                // if we dont have already read the tables || we want to overwrite the current config
                if ((syncConfiguration.HasTables && !syncConfiguration.HasColumns))
                    await this.ReadConfigurationAsync(syncConfiguration);

                // save to cache
                this.SetCacheConfiguration(syncConfiguration);

                context.SyncStage = SyncStage.ConfigurationApplied;
                var afterArgs = new ConfigurationAppliedEventArgs(this.ProviderTypeName, context.SyncStage, syncConfiguration);
                this.TryRaiseProgressEvent(afterArgs, this.ConfigurationApplied);
                // if config has been changed by user, save it again                        
                this.SetCacheConfiguration(syncConfiguration);
                return (context, syncConfiguration);
            }
            catch (SyncException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SyncException(ex, SyncStage.ConfigurationApplying, this.ProviderTypeName);
            }

        }

        /// <summary>
        /// Get cached configuration (inmemory or session cache)
        /// </summary>
        public SyncConfiguration GetCacheConfiguration()
        {
            var configurationSurrogate = this.CacheManager.GetValue<DmSetSurrogate>(SYNC_CONF);
            if (configurationSurrogate == null)
                return null;

            var dmSet = configurationSurrogate.ConvertToDmSet();
            if (dmSet == null)
                return null;

            return SyncConfiguration.DeserializeFromDmSet(dmSet);
        }

        public void SetCacheConfiguration(SyncConfiguration configuration)
        {
            var dmSetConf = new DmSet();
            SyncConfiguration.SerializeInDmSet(dmSetConf, configuration);
            var dmSSetConf = new DmSetSurrogate(dmSetConf);
            this.CacheManager.Set(SYNC_CONF, dmSSetConf);
        }
    }
}
