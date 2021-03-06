﻿using System;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;

namespace AzureStorage.Tables.Templates
{
    /// <summary>
    /// Используем для сохранения сложный объектов (с листами, с объектами)
    /// </summary>
    /// <typeparam name="T">Тип, который сохраняем</typeparam>
    [Obsolete("Use AzureTableEntity. Will be removed in the future releases")]
    public class JsonTableEntity<T> : TableEntity 
    {
        public T Instance { get; set; }

        public string Data
        {
            get => JsonConvert.SerializeObject(Instance);
            set => Instance = JsonConvert.DeserializeObject<T>(value);
        }
    }
}
