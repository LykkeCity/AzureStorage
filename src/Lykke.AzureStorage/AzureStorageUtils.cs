﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AzureStorage.Tables.Templates;
using AzureStorage.Tables.Templates.Index;
using JetBrains.Annotations;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

namespace AzureStorage
{
    public enum ToIntervalOption
    {
        IncludeTo,
        ExcludeTo
    }

    public enum RowKeyDateTimeFormat
    {
        Iso,
        Short
    }

    [PublicAPI]
    public static class AzureStorageUtils
    {
        public const int Conflict = 409;

        public static string ToDateTimeMask(this RowKeyDateTimeFormat format)
        {
            return format == RowKeyDateTimeFormat.Short ? "yyyyMMddHHmmss" : "yyyy-MM-dd HH:mm:ss.fffffff";
        }

        public static string ToDateTimeSuffix(this int value, RowKeyDateTimeFormat format)
        {
            return value.ToString("000");
        }

        public static bool HandleStorageException(this StorageException storageException, IEnumerable<int> notLogCodes)
        {
            return notLogCodes.Any(notLogCode => storageException.RequestInformation.HttpStatusCode == notLogCode);
        }

        public static string PrintItem(object item)
        {
            if (item is string s)
                return s;

            var stringBuilder = new StringBuilder();

            foreach (
                var propertyInfo in
                    item.GetType().GetProperties().Where(propertyInfo => propertyInfo.CanRead && propertyInfo.CanWrite))

                stringBuilder.Append($"{propertyInfo.Name}=[{propertyInfo.GetValue(item, null)}];");

            return stringBuilder.ToString();
        }

        public static IEnumerable<T> ApplyFilter<T>(IEnumerable<T> data, Func<T, bool> filter)
        {
            return filter == null ? data : data.Where(filter);
        }

        [Obsolete("Use InsertOrReplaceAsync(T, Func<T, bool>) or InsertOrModifyAsync(string, string, Func<T> create, Func<T, bool> modify) instead of this method, according to your requirements")]
        public static async Task<T> ModifyOrCreateAsync<T>(this INoSQLTableStorage<T> tableStorage,
            string partitionKey, string rowKey, Func<T> create, Action<T> update) where T : ITableEntity, new()
        {
            for (var i = 0; i < 15; i++)
            {
                try
                {
                    var entity = await tableStorage.ReplaceAsync(partitionKey, rowKey, itm =>
                    {
                        update(itm);
                        return itm;
                    });

                    if (entity != null) return entity;

                    entity = create();
                    await tableStorage.InsertAsync(entity);
                    return entity;
                }
                catch (Exception)
                {
                }
            }
            throw new Exception("Can not modify or update entity: " + PrintItem(create()));
        }     

		public static Task<IEnumerable<T>> WhereAsync<T>(this INoSQLTableStorage<T> tableStorage, string partitionKey,
		  DateTime from, DateTime to, ToIntervalOption intervalOption, Func<T, bool> filter = null, bool includeTime = false)
		  where T : ITableEntity, new()
		{
			var rangeQuery = QueryGenerator<T>.BetweenQuery(partitionKey, from, to, intervalOption, includeTime);

			return filter == null
				? tableStorage.WhereAsync(rangeQuery)
				: tableStorage.WhereAsync(rangeQuery, filter);
		}

		public static Task<IEnumerable<T>> WhereAsync<T>(this INoSQLTableStorage<T> tableStorage, string partitionKey,
            int year, int month, Func<T, bool> filter = null)
            where T : ITableEntity, new()
        {
            var from = new DateTime(year, month, 1);
            var to = from.AddMonths(1);

            var rangeQuery = QueryGenerator<T>.BetweenQuery(partitionKey, from, to, ToIntervalOption.ExcludeTo);

            return filter == null
                ? tableStorage.WhereAsync(rangeQuery)
                : tableStorage.WhereAsync(rangeQuery, filter);
        }

        public static Task WhereAsync<T>(this INoSQLTableStorage<T> tableStorage, string partitionKey,
            int year, int month, Action<IEnumerable<T>> chunk = null)
            where T : ITableEntity, new()
        {
            var from = new DateTime(year, month, 1);
            var to = from.AddMonths(1);

            var rangeQuery = QueryGenerator<T>.BetweenQuery(partitionKey, from, to, ToIntervalOption.ExcludeTo);

            return tableStorage.ExecuteAsync(rangeQuery, chunk);
        }

        public static async Task<IEnumerable<T>> WhereAsync<T>(this INoSQLTableStorage<T> tableStorage,
            IEnumerable<string> partitionKeys, DateTime from, DateTime to,
            ToIntervalOption intervalOption, Func<T, bool> filter = null)
            where T : ITableEntity, new()
        {
            var result = new List<T>();

            await Task.WhenAll(
                partitionKeys.Select(
                    partitionKey => tableStorage.WhereAsync(partitionKey, from, to, intervalOption, filter)
                        .ContinueWith(task =>
                        {
                            lock (result) result.AddRange(task.GetAwaiter().GetResult());
                        }))
                );

            return result;
        }

		public static async Task<T> GetFirstOrDefaultAsync<T>(this INoSQLTableStorage<AzureMultiIndex> indexTable, string partitionKey, string rowKey, INoSQLTableStorage<T> dataTable) where T : class, ITableEntity, new()
		{
			var indexEntity = await indexTable.GetDataAsync(partitionKey, rowKey);
			if (indexEntity == null)
				return null;

			var indices = indexEntity.GetData();

			if (indices.Length == 0)
				return null;

			return await dataTable.GetDataAsync(indices[0].Pk, indices[0].Rk);
		}

		public static async Task<string> GenerateIdAsync(this INoSQLTableStorage<SetupEntity> tableStorage,
            string partitionKey, string rowKey, int fromId)
        {
            while (true)
            {
                try
                {
                    var result = await tableStorage.ReplaceAsync(partitionKey, rowKey, itm =>
                    {
                        int i;

                        try
                        {
                            i = int.Parse(itm.Value);
                        }
                        catch (Exception)
                        {
                            i = fromId;
                        }

                        itm.Value = (i + 1).ToString(CultureInfo.InvariantCulture);
                        return itm;
                    });

                    if (result != null)
                        return result.Value;


                    var idEntity = SetupEntity.Create(partitionKey, rowKey,
                        fromId.ToString(CultureInfo.InvariantCulture));

                    await tableStorage.InsertAsync(idEntity, Conflict);
                }

                catch (StorageException e)
                {
                    if (e.RequestInformation.HttpStatusCode != Conflict)
                        throw;
                }
            }
        }

        public static Task<IEnumerable<T>> GetDataRowKeyOnlyAsync<T>(this INoSQLTableStorage<T> tableStorage,
            string rowKey)
            where T : ITableEntity, new()
        {
            var query = QueryGenerator<T>.RowKeyOnly.GetTableQuery(rowKey);
            return tableStorage.WhereAsync(query);
        }

        public static Task<T> ReplaceAsync<T>(this INoSQLTableStorage<T> tableStorage, T item,
            Func<T, T> updateAction) where T : ITableEntity, new()
        {
            return tableStorage.ReplaceAsync(item.PartitionKey, item.RowKey, updateAction);
        }

        public static async Task<IEnumerable<T>> ScanAndGetList<T>(this INoSQLTableStorage<T> tableStorage,
            string partitionKey, Func<T, bool> condition)
            where T : class, ITableEntity, new()
        {
            var result = new List<T>();

            await tableStorage.FirstOrNullViaScanAsync(partitionKey, items =>
            {
                result.AddRange(items.Where(condition));
                return null;
            });

            return result;
        }

        public static Dictionary<string, string> ParseTableStorageConnectionString(this string connString)
        {
            return
                connString.Split(';')
                    .Select(keyValuePair => keyValuePair.Split('='))
                    .Where(pair => pair.Length >= 2)
                    .ToDictionary(pair => pair[0].ToLower(), pair => pair[1]);
        }

        public static async Task<(IEnumerable<T> Entities, string ContinuationToken)> GetDataWithContinuationTokenAsync<T>(this INoSQLTableStorage<T> tableStorage, int take, string continuationToken)
            where T : class, ITableEntity, new()
        {
            var rangeQuery = new TableQuery<T>
            {
                TakeCount = take
            };
            

            return await tableStorage.GetDataWithContinuationTokenAsync(rangeQuery, continuationToken);
        }

        public static async Task<(IEnumerable<T> Entities, string ContinuationToken)> GetDataWithContinuationTokenAsync<T>(this INoSQLTableStorage<T> tableStorage, TableQuery<T> rangeQuery, int take, string continuationToken)
            where T : class, ITableEntity, new()
        {
            rangeQuery.TakeCount = take;

            return await tableStorage.GetDataWithContinuationTokenAsync(rangeQuery, continuationToken);
        }

        public static async Task<(IEnumerable<T> Entities, string ContinuationToken)> GetDataWithContinuationTokenAsync<T>(this INoSQLTableStorage<T> tableStorage, string partitionKey, int take, string continuationToken)
            where T : class, ITableEntity, new()
        {
            var rangeQuery = QueryGenerator<T>.PartitionKeyOnly.GetTableQuery(partitionKey);

            rangeQuery.TakeCount = take;

            return await tableStorage.GetDataWithContinuationTokenAsync(rangeQuery, continuationToken);
        }

        public static class QueryGenerator<T> where T : ITableEntity, new()
        {			
			private static string ConvertDateTimeToString(DateTime dateTime, bool includeTime = false)
			{
				return includeTime ? dateTime.ToString("yyyy-MM-dd HH:mm:ss") :
					dateTime.ToString("yyyy-MM-dd") + " 00:00:00.000";
			}

			private static string GenerateRowFilterString(string rowKeyFrom, string rowKeyTo,
                ToIntervalOption intervalOption)
            {
                if (intervalOption == ToIntervalOption.IncludeTo)
                    return
                        "RowKey " + QueryComparisons.GreaterThanOrEqual + " '" + rowKeyFrom + "' and " +
                        "RowKey " + QueryComparisons.LessThanOrEqual + " '" + rowKeyTo + "'";

                return
                    "RowKey " + QueryComparisons.GreaterThanOrEqual + " '" + rowKeyFrom + "' and " +
                    "RowKey " + QueryComparisons.LessThan + " '" + rowKeyTo + "'";
            }

            public static TableQuery<T> GreaterThanQuery(string partitionKey, string rowKey)
            {
                var sqlFilter =
                    $"PartitionKey {QueryComparisons.Equal} '{partitionKey}' and RowKey {QueryComparisons.GreaterThanOrEqual} '{rowKey}'";

                return new TableQuery<T>().Where(sqlFilter);
            }

            public static TableQuery<T> BetweenQuery(string partitionKey, string rowKeyFrom, string rowKeyTo,
                ToIntervalOption intervalOption)
            {
                var sqlFilter = "PartitionKey " + QueryComparisons.Equal + " '" + partitionKey + "' and " +
                                GenerateRowFilterString(rowKeyFrom, rowKeyTo, intervalOption);

                return new TableQuery<T>().Where(sqlFilter);
            }

	        public static TableQuery<T> BetweenQuery(string partitionKey, DateTime rowKeyFrom, DateTime rowKeyTo,
		        ToIntervalOption intervalOption,
		        bool includeTime = false)
	        {
		        var sqlFilter = "PartitionKey " + QueryComparisons.Equal + " '" + partitionKey + "' and " +
		                        GenerateRowFilterString(ConvertDateTimeToString(rowKeyFrom, includeTime),
			                        ConvertDateTimeToString(rowKeyTo, includeTime), intervalOption);

		        return new TableQuery<T>().Where(sqlFilter);
	        }

	        public static TableQuery<T> BetweenQuery(IEnumerable<string> partitionKeys, DateTime rowKeyFrom,
                DateTime rowKeyTo, ToIntervalOption intervalOption)
            {
                var partitions = new StringBuilder();
                foreach (var partitionKey in partitionKeys)
                {
                    if (partitions.Length > 0)
                        partitions.Append(" or ");

                    partitions.Append("PartitionKey " + QueryComparisons.Equal + " '" + partitionKey + "'");
                }

                var sqlFilter = "(" + partitions + ") and " +
                                GenerateRowFilterString(ConvertDateTimeToString(rowKeyFrom),
                                    ConvertDateTimeToString(rowKeyTo), intervalOption);

                return new TableQuery<T>().Where(sqlFilter);
            }

            public static TableQuery<T> MultiplePartitionKeys(params string[] partitionKeys)
            {
                var partitionKeysString = new StringBuilder();

                foreach (var rowKey in partitionKeys)
                {
                    if (partitionKeysString.Length > 0)
                        partitionKeysString.Append(" or ");
                    partitionKeysString.Append("PartitionKey " + QueryComparisons.Equal + " '" + rowKey + "'");
                }
                var sqlFilter = partitionKeysString.ToString();
                return new TableQuery<T>().Where(sqlFilter);
            }

            public static TableQuery<T> MultipleRowKeys(string partitionKey, params string[] rowKeys)
            {
                var rowKeysString = new StringBuilder();

                foreach (var rowKey in rowKeys)
                {
                    if (rowKeysString.Length > 0)
                        rowKeysString.Append(" or ");
                    rowKeysString.Append("RowKey " + QueryComparisons.Equal + " '" + rowKey + "'");
                }
                var sqlFilter = "PartitionKey " + QueryComparisons.Equal + " '" + partitionKey + "' and (" +
                                rowKeysString + ")";
                return new TableQuery<T>().Where(sqlFilter);
            }

            public static TableQuery<T> MultipleKeys(IEnumerable<Tuple<string, string>> keys)
            {
                var sqlFilter = new StringBuilder();

                foreach (var key in keys)
                {
                    if (sqlFilter.Length > 0)
                        sqlFilter.Append(" or ");

                    sqlFilter.Append("PartitionKey " + QueryComparisons.Equal + " '" + key.Item1 + "' and RowKey " +
                                     QueryComparisons.Equal + " '" + key.Item2 + "'");
                }
                return new TableQuery<T>().Where(sqlFilter.ToString());
            }

            public static TableQuery<T> RangeQuery(string partitionFrom, string partitionTo, string rowKey,
                ToIntervalOption intervalOption)
            {
                var sqlFilter = intervalOption == ToIntervalOption.IncludeTo
                    ? "PartitionKey " + QueryComparisons.GreaterThanOrEqual + " '" + partitionFrom + "' and " +
                      "PartitionKey " + QueryComparisons.LessThanOrEqual + " '" + partitionTo + "'"
                    : "PartitionKey " + QueryComparisons.GreaterThanOrEqual + " '" + partitionFrom + "' and " +
                      "PartitionKey " + QueryComparisons.LessThan + " '" + partitionTo + "'";

                return
                    new TableQuery<T>().Where(sqlFilter + " and RowKey " + QueryComparisons.Equal + " '" + rowKey + "'");
            }

            public static class PartitionKeyOnly
            {
                public static TableQuery<T> GetTableQuery(string partitionKey)
                {
                    var sqlFilter =
                        "PartitionKey " + QueryComparisons.Equal + " '" + partitionKey + "'";

                    return new TableQuery<T>().Where(sqlFilter);
                }

                /// <summary>
                ///     Генерация запроса-диапазона только по PartitionKey
                /// </summary>
                /// <param name="from">Partition from</param>
                /// <param name="to">Partition to</param>
                /// <param name="intervalOption">Включить участок to</param>
                /// <returns></returns>
                public static TableQuery<T> BetweenQuery(string from, string to, ToIntervalOption intervalOption)
                {
                    var sqlFilter = intervalOption == ToIntervalOption.IncludeTo
                        ? "PartitionKey " + QueryComparisons.GreaterThanOrEqual + " '" + from + "' and " +
                          "PartitionKey " + QueryComparisons.LessThanOrEqual + " '" + to + "'"
                        : "PartitionKey " + QueryComparisons.GreaterThanOrEqual + " '" + from + "' and " +
                          "PartitionKey " + QueryComparisons.LessThan + " '" + to + "'";

                    return new TableQuery<T>().Where(sqlFilter);
                }

                public static TableQuery<T> BetweenQuery(DateTime from, DateTime to, ToIntervalOption intervalOption)
                {
                    return BetweenQuery(ConvertDateTimeToString(from), ConvertDateTimeToString(to), intervalOption);
                }
            }

            public static class RowKeyOnly
            {
                public static TableQuery<T> BetweenQuery(string rowKeyFrom, string rowKeyTo,
                    ToIntervalOption intervalOption)
                {
                    var sqlFilter = GenerateRowFilterString(rowKeyFrom, rowKeyTo, intervalOption);
                    return new TableQuery<T>().Where(sqlFilter);
                }

                public static TableQuery<T> BetweenQuery(DateTime from, DateTime to, ToIntervalOption intervalOption)
                {
                    var sqlFilter = GenerateRowFilterString(ConvertDateTimeToString(from), ConvertDateTimeToString(to),
                        intervalOption);
                    return new TableQuery<T>().Where(sqlFilter);
                }

                public static TableQuery<T> GetTableQuery(string rowKey)
                {
                    var sqlFilter = "RowKey " + QueryComparisons.Equal + " '" + rowKey + "'";
                    return new TableQuery<T>().Where(sqlFilter);
                }

                public static TableQuery<T> GetTableQuery(IEnumerable<string> rowKeys)
                {
                    var queryString = new StringBuilder();
                    foreach (var rowKey in rowKeys)
                    {
                        if (queryString.Length != 0)
                            queryString.Append(" or ");

                        queryString.Append("RowKey " + QueryComparisons.Equal + " '" + rowKey + "'");
                    }
                    return new TableQuery<T>().Where(queryString.ToString());
                }
            }
        }

        #region Inserts

        /// <summary>
        ///     Перебирает по одному ключу, пока не получится вставить запись в таблицу
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="table"></param>
        /// <param name="entity"></param>
        /// <param name="dateTime"></param>
        /// <param name="rowKeyDateTimeFormat">Формат ключа</param>
        /// <returns></returns>
        public static async Task<T> InsertAndGenerateRowKeyAsDateTimeAsync<T>(this INoSQLTableStorage<T> table, T entity,
            DateTime dateTime, RowKeyDateTimeFormat rowKeyDateTimeFormat = RowKeyDateTimeFormat.Iso)
            where T : ITableEntity, new()
        {
            var dt = dateTime.ToString(rowKeyDateTimeFormat.ToDateTimeMask());
            var no = 0;

            while (true)
            {
                entity.RowKey = dt + no.ToDateTimeSuffix(rowKeyDateTimeFormat);

                try
                {
                    await table.InsertAsync(entity, Conflict);
                    return entity;
                }
                catch (AggregateException e)
                {
                    if (e.InnerExceptions[0] is StorageException se)
                    {
                        if (se.RequestInformation.HttpStatusCode != Conflict)
                            throw;
                    }
                    else throw;
                }
                catch (StorageException e)
                {
                    if (e.RequestInformation.HttpStatusCode != Conflict)
                        throw;
                }

                if (no == 999)
                    throw new Exception("Can not insert record: " + PrintItem(entity));
                no++;
            }
        }

        public delegate string GenerateRowKey<in T>(T entity, int retryNumber, int batchItemNumber);

        /// <summary>
        /// Generates unique row key for each entity in the <paramref name="entitiesBatch"/> before insert, 
        /// using <paramref name="generateRowKey"/>. If conflict accurs on insert, 
        /// increments retries counter up to <paramref name="maxRetriesCount"/> and regenerates all row keys
        /// </summary>
        public static async Task InsertBatchAndGenerateRowKeyAsync<T>(
            this INoSQLTableStorage<T> storage,
            IReadOnlyList<T> entitiesBatch,
            GenerateRowKey<T> generateRowKey,
            int maxRetriesCount = 1000)
            
            where T : ITableEntity, new()
        {
            var retryNumber = 0;

            UpdateRowKeys(entitiesBatch, generateRowKey, retryNumber);

            while (true)
            {
                ++retryNumber;

                try
                {
                    await storage.InsertAsync(entitiesBatch);
                    return;
                }
                catch (AggregateException ex)
                    when ((ex.InnerExceptions[0] as StorageException)?.RequestInformation?.HttpStatusCode ==
                          (int)HttpStatusCode.Conflict && retryNumber <= maxRetriesCount)
                {
                    UpdateRowKeys(entitiesBatch, generateRowKey, retryNumber);
                }
                catch (StorageException ex)
                    when (ex.RequestInformation.HttpStatusCode == (int)HttpStatusCode.Conflict && retryNumber <= maxRetriesCount)
                {
                    UpdateRowKeys(entitiesBatch, generateRowKey, retryNumber);
                }
            }
        }

        private static void UpdateRowKeys<T>(IReadOnlyList<T> group, GenerateRowKey<T> generateRowKey, int retryNumber)
            where T : ITableEntity
        {
            for (var itemNumber = 0; itemNumber < group.Count; ++itemNumber)
            {
                var entry = group[itemNumber];

                entry.RowKey = generateRowKey(entry, retryNumber, itemNumber);
            }
        }

        public static async Task<T> InsertAndCheckRowKeyAsync<T>(this INoSQLTableStorage<T> table, T entity,
            Func<string> generateRowKey) where T : ITableEntity, new()
        {
            var no = 0;

            while (true)
            {
                entity.RowKey = generateRowKey();

                try
                {
                    await table.InsertAsync(entity, Conflict);
                    return entity;
                }
                catch (AggregateException e)
                {
                    if (e.InnerExceptions[0] is StorageException)
                    {
                        var se = e.InnerExceptions[0] as StorageException;
                        if (se.RequestInformation.HttpStatusCode != Conflict)
                            throw;
                    }
                    else throw;
                }
                catch (StorageException e)
                {
                    if (e.RequestInformation.HttpStatusCode != Conflict)
                        throw;
                }
                if (no == 999)
                    throw new Exception("Can not insert record InsertAndCheckRowKey: " + PrintItem(entity));
                no++;
            }
        }

        /// <summary>
        ///     Перебирает по одному ключу, пока не получится вставить запись в таблицу
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="table"></param>
        /// <param name="entity"></param>
        /// <param name="dateTime"></param>
        /// <returns></returns>
        public static async Task<T> InsertAndGenerateRowKeyAsTimeAsync<T>(this INoSQLTableStorage<T> table, T entity,
            DateTime dateTime) where T : ITableEntity, new()
        {
            var dt = dateTime.ToString("HH:mm:ss");
            var no = 0;

            while (true)
            {
                entity.RowKey = dt + '.' + no.ToString("000");

                try
                {
                    await table.InsertAsync(entity, Conflict);
                    return entity;
                }
                catch (AggregateException e)
                {
                    if (e.InnerExceptions[0] is StorageException)
                    {
                        var se = e.InnerExceptions[0] as StorageException;
                        if (se.RequestInformation.HttpStatusCode != Conflict)
                            throw;
                    }
                    else throw;
                }
                catch (StorageException e)
                {
                    if (e.RequestInformation.HttpStatusCode != Conflict)
                        throw;
                }
                if (no == 999)
                    throw new Exception("Can not insert record: " + PrintItem(entity));
                no++;
            }
        }

        /// <summary>
        /// Inserts the <paramref name="item"/> to the storage. If row successfully inserted, method returns true,
        /// If row with given rartition key and row key already exists, then method returns false.
        /// Method is auto-retries according to the <paramref name="storage"/> settings
        /// </summary>
        /// <returns>True is row is successfully inserted, false if row with given rartition key and row key already exists</returns>
        public static async Task<bool> TryInsertAsync<TEntity>(this INoSQLTableStorage<TEntity> storage, TEntity item) 
            where TEntity : ITableEntity, new()
        {
            try
            {
                await storage.InsertAsync(item, Conflict);

                return true;
            }
            catch (StorageException e) when (e.RequestInformation.HttpStatusCode == Conflict)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns existing or inserts new entity with the given <paramref name="partitionKey"/> and <paramref name="rowKey"/>.
        /// In case of concurrent insertion, existing entity can be returned, even if <paramref name="createNew"/> was called. 
        /// In this case result of the <paramref name="createNew"/> will be ignored.
        /// </summary>
        /// <param name="storage">The storage</param>
        /// <param name="partitionKey">Partition key</param>
        /// <param name="rowKey">Row key</param>
        /// <param name="createNew">
        /// Delegate to create new entity if it's doesn't exist yet.
        /// If existing record exists, <paramref name="createNew"/> will not be called.
        /// If concurrent insertion was performed, then <paramref name="createNew"/> will be called, but
        /// existing entity will be returned as the result.
        /// </param>
        /// <returns></returns>
        public static async Task<TEntity> GetOrInsertAsync<TEntity>(
            this INoSQLTableStorage<TEntity> storage, 
            string partitionKey, 
            string rowKey, 
            Func<TEntity> createNew) 
            where TEntity : ITableEntity, new()
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }
            if (partitionKey == null)
            {
                throw new ArgumentNullException(nameof(partitionKey));
            }
            if (rowKey == null)
            {
                throw new ArgumentNullException(nameof(rowKey));
            }
            if (createNew == null)
            {
                throw new ArgumentNullException(nameof(createNew));
            }

            while (true)
            {
                var entity = await storage.GetDataAsync(partitionKey, rowKey);
                if (entity != null)
                {
                    return entity;
                }

                var newEntity = createNew();

                if (newEntity == null)
                {
                    throw new InvalidOperationException($"Created entity should be not null");
                }
                if (newEntity.PartitionKey != partitionKey)
                {
                    throw new InvalidOperationException(
                        $"Created entity partition key ({newEntity.PartitionKey}) should equals to the partition key ({partitionKey}) that is passed to the {nameof(GetOrInsertAsync)}");
                }
                if (newEntity.RowKey != rowKey)
                {
                    throw new InvalidOperationException(
                        $"Created entity row key ({newEntity.RowKey}) should equals to the row key ({rowKey}) that is passed to the {nameof(GetOrInsertAsync)}");
                }

                if (await storage.TryInsertAsync(newEntity))
                {
                    return newEntity;
                }
            }
        }

        #endregion Inserts

        public static async Task<T> MergeAsync<T>(this INoSQLTableStorage<AzureMultiIndex> indexTable, string partitionKey, string rowKey, INoSQLTableStorage<T> dataTable, Func<T, T> replaceCallback) where T : class, ITableEntity, new()
		{
			var indexEntity = await indexTable.GetDataAsync(partitionKey, rowKey);
			if (indexEntity == null)
				return null;

			var indices = indexEntity.GetData();

			if (indices.Length == 0)
				return null;

			var tasks = new List<Task<T>>();

		    foreach (var index in indices)
			{
				var task = dataTable.MergeAsync(index.Pk, index.Rk, replaceCallback);
				tasks.Add(task);
			}

			await Task.WhenAll(tasks);

			return tasks[0].GetAwaiter().GetResult();
		}
    }
}
