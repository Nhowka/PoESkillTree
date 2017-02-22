﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using log4net;
using static POESKillTree.Utils.WikiApi.ItemRdfPredicates;
using static POESKillTree.Utils.WikiApi.WikiApiUtils;

namespace POESKillTree.Utils.WikiApi
{
    /// <summary>
    /// Retrieves images for items from the wiki's API and saves them to the filesystem. Images are retrieved
    /// once there were a fixed number of calls to <see cref="ProduceAsync"/> or once a fixed amount of time passed.
    /// </summary>
    /// <remarks>
    /// Implemented using a producer-consumer-pattern. The producer sends the images to be downloaded and the
    /// consumer acts once a fixed number was produced or a fixed amount of time passed. The consumer is started
    /// in the constructor.
    /// 
    /// Exceptions in the consumer are relayed to the task returned by the producer.
    /// </remarks>
    public class PoolingImageLoader
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PoolingImageLoader));

        // with more the queries for the 'ask' action are getting to big
        private const int MaxBatchSize = 10;
        private static readonly TimeSpan MaxWaitTime = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan InfiniteWaitTime = TimeSpan.FromMilliseconds(-1);

        private readonly HttpClient _httpClient;
        private readonly ApiAccessor _apiAccessor;

        // producer-consumer-queue with capped capacity
        private readonly BufferBlock<PoolItem> _queue =
            new BufferBlock<PoolItem>(new DataflowBlockOptions {BoundedCapacity = MaxBatchSize});

        public PoolingImageLoader(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _apiAccessor = new ApiAccessor(httpClient);
            ConsumeAsync();
        }

        /// <summary>
        /// Returns a task that completes once the image for the item with the given name was downloaded to a file
        /// of the given name.
        /// </summary>
        public async Task ProduceAsync(string itemName, string fileName)
        {
            var tcs = new TaskCompletionSource<string>();
            var item = new PoolItem(itemName, fileName, tcs);
            await _queue.SendAsync(item).ConfigureAwait(false);
            await tcs.Task.ConfigureAwait(false);
        }

        // return type can be void as this should never return and is never awaited
        // all exceptions that can be handled are relayed to a producer task
        private async void ConsumeAsync()
        {
            var pool = new List<PoolItem>();
            while (true)
            {
                var canceled = false;
                try
                {
                    // timeout at MaxWaitTime if there are items in the pool
                    var task = _queue.ReceiveAsync(pool.Any() ? MaxWaitTime : InfiniteWaitTime);
                    pool.Add(await task.ConfigureAwait(false));
                }
                catch (TimeoutException)
                {
                    canceled = true;
                }
                if (canceled || pool.Count >= MaxBatchSize)
                {
                    await LoadPoolAsync(pool).ConfigureAwait(false);
                    pool.Clear();
                }
            }
        }

        private async Task LoadPoolAsync(IReadOnlyList<PoolItem> pool)
        {
            if (!pool.Any())
            {
                return;
            }
            try
            {
                // retrieve page titles of the items' icons
                var nameToItem = pool.ToDictionary(p => p.ItemName);
                var conditions = new ConditionBuilder
                {
                    {RdfName, string.Join("||", pool.Select(p => p.ItemName))}
                };
                var printouts = new[] {RdfName, RdfIcon};
                var results = (from ps in await _apiAccessor.Ask(conditions, printouts).ConfigureAwait(false)
                               let title = ps[RdfIcon].First.Value<string>("fulltext")
                               let name = SingularValue<string>(ps, RdfName)
                               select new { name, title }).ToList();
                var titleToItem = results.ToDictionary(x => x.title, x => nameToItem[x.name]);

                // retrieve urls of the actual icons
                var task = _apiAccessor.QueryImageInfoUrls(results.Select(t => t.title));
                var imageInfo =
                    from tuple in await task.ConfigureAwait(false)
                    select new {Item = titleToItem[tuple.Item1], Url = tuple.Item2};

                // download and save icons
                var saveTasks = new List<Task>();
                var missing = new HashSet<PoolItem>(pool);
                foreach (var x in imageInfo)
                {
                    // Don't load duplicates (e.g. for Two-Stone Ring or items with weapon skins)
                    if (missing.Remove(x.Item))
                    {
                        saveTasks.Add(LoadSingleAsync(x.Item, x.Url));
                    }
                }
                await Task.WhenAll(saveTasks).ConfigureAwait(false);
                foreach (var item in missing)
                {
                    item.Tcs.SetException(new Exception($"Item image for {item.ItemName} was not downloaded"));
                }
            }
            catch (Exception e)
            {
                var first = pool[0];
                first.Tcs.SetException(e);
                foreach (var item in pool.Skip(1))
                {
                    item.Tcs.SetCanceled();
                }
            }
        }

        private async Task LoadSingleAsync(PoolItem item, string url)
        {
            try
            {
                var imgData = await _httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                using (var outputStream = File.Create(item.FileName))
                {
                    SaveImage(imgData, outputStream, true);
                }
                Log.Info($"Downloaded item image for {item.ItemName} to the file system.");
                item.Tcs.SetResult(item.ItemName);
            }
            catch (Exception e)
            {
                item.Tcs.SetException(e);
            }
        }


        private class PoolItem
        {
            public string ItemName { get; }
            public string FileName { get; }
            public TaskCompletionSource<string> Tcs { get; }

            public PoolItem(string itemName, string fileName, TaskCompletionSource<string> tcs)
            {
                ItemName = itemName;
                FileName = fileName;
                Tcs = tcs;
            }
        }
    }
}