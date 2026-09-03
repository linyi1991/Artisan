using ECommons;
using ECommons.DalamudServices;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.Universalis
{
    internal class UniversalisClient
    {
        private const string Endpoint = "https://universalis.app/api/v2/";
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
        private readonly HttpClient httpClient;
        private readonly ConcurrentDictionary<string, CacheEntry> cache = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<MarketboardData?>>> inFlight = new();
        private readonly CancellationTokenSource disposeToken = new();
        public uint? PlayerWorld;
        public UniversalisClient()
        {
            this.httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(10000),
            };
        }

        private sealed record CacheEntry(MarketboardData Data, DateTimeOffset ExpiresAt);

        public Task<MarketboardData?> GetMarketBoardAsync(string region, ulong itemId, bool forceRefresh = false)
        {
            var key = $"{region}:{itemId}";
            if (!forceRefresh && cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return Task.FromResult<MarketboardData?>(cached.Data);

            var request = inFlight.GetOrAdd(key, _ => new Lazy<Task<MarketboardData?>>(
                () => FetchAndCacheAsync(key, region, itemId), LazyThreadSafetyMode.ExecutionAndPublication));
            return AwaitAndReleaseAsync(key, request);
        }

        public async Task<MarketboardData?> GetRegionDataAsync(ulong itemId, bool forceRefresh = false)
        {
            var world = PlayerWorld;
            if (world == null)
                return null;

            var region = Regions.GetRegionByWorld(world.Value);
            if (region == null)
                return null;

            return await GetMarketBoardAsync(region, itemId, forceRefresh).ConfigureAwait(false);
        }

        public async Task<MarketboardData?> GetDCDataAsync(ulong itemId, bool forceRefresh = false)
        {
            var world = PlayerWorld;
            if (world == null)
                return null;

            var region = DataCenters.GetDataCenterName(world.Value);
            if (region == null)
                return null;

            return await GetMarketBoardAsync(region, itemId, forceRefresh).ConfigureAwait(false);
        }

        public void Dispose()
        {
            disposeToken.Cancel();
            this.httpClient.Dispose();
            disposeToken.Dispose();
            cache.Clear();
            inFlight.Clear();
        }

        private async Task<MarketboardData?> AwaitAndReleaseAsync(string key, Lazy<Task<MarketboardData?>> request)
        {
            try
            {
                return await request.Value.ConfigureAwait(false);
            }
            finally
            {
                inFlight.TryRemove(key, out _);
            }
        }

        private async Task<MarketboardData?> FetchAndCacheAsync(string key, string region, ulong itemId)
        {
            try
            {
                var data = await GetMarketBoardDataAsync(region, itemId, disposeToken.Token).ConfigureAwait(false);
                if (data != null)
                {
                    cache[key] = new(data, DateTimeOffset.UtcNow.Add(CacheLifetime));
                    return data;
                }
            }
            catch (OperationCanceledException) when (disposeToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ex.Log();
            }

            return cache.TryGetValue(key, out var stale) ? stale.Data : null;
        }

        private async Task<MarketboardData?> GetMarketBoardDataAsync(string region, ulong itemId, CancellationToken cancellationToken)
        {
            try
            {
                var request = Endpoint + Uri.EscapeDataString(region) + "/" + itemId;
                Svc.Log.Debug($"universalisRequest={request}");
                using var result = await httpClient.GetAsync(new Uri(request), cancellationToken).ConfigureAwait(false);
                if (result.StatusCode != HttpStatusCode.OK)
                {
                    Svc.Log.Warning(
                        "Failed to retrieve data from Universalis for ItemId {0} / region {1} with HttpStatusCode {2}.",
                        itemId,
                        region,
                        result.StatusCode);
                    return null;
                }

                var content = await result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var json = JsonConvert.DeserializeObject<dynamic>(content);
                if (json == null)
                {
                    Svc.Log.Error("Failed to deserialize Universalis response for ItemId {0} / region {1}.", itemId, region);
                    return null;
                }

                var marketBoardData = new MarketboardData
                {
                    LastCheckTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    LastUploadTime = json.lastUploadTime?.Value,
                    AveragePriceNQ = json.averagePriceNQ?.Value,
                    AveragePriceHQ = json.averagePriceHQ?.Value,
                    CurrentAveragePriceNQ = json.currentAveragePriceNQ?.Value,
                    CurrentAveragePriceHQ = json.currentAveragePriceHQ?.Value,
                    MinimumPriceNQ = json.minPriceNQ?.Value,
                    MinimumPriceHQ = json.minPriceHQ?.Value,
                    MaximumPriceNQ = json.maxPriceNQ?.Value,
                    MaximumPriceHQ = json.maxPriceHQ?.Value,
                    TotalNumberOfListings = json.listingsCount?.Value,
                    TotalQuantityOfUnits = json.unitsForSale?.Value
                };
                if (json.listings != null && json.listings.Count > 0)
                {
                    foreach (var item in json.listings)
                    {
                        Listing listing = new()
                        {
                            World = item.worldName.Value,
                            Quantity = item.quantity.Value,
                            TotalPrice = item.total.Value,
                            UnitPrice = item.pricePerUnit.Value
                        };

                        if (listing.World != "Cloudtest01" && listing.World != "Cloudtest02")
                            marketBoardData.AllListings.Add(listing);
                    }

                    var cheapest = marketBoardData.AllListings.OrderBy(x => x.UnitPrice).FirstOrDefault();
                    if (cheapest != null)
                    {
                        marketBoardData.CurrentMinimumPrice = cheapest.UnitPrice;
                        marketBoardData.LowestWorld = cheapest.World;
                        marketBoardData.ListingQuantity = cheapest.Quantity;
                    }
                }

                return marketBoardData;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(
                    ex,
                    "Failed to parse marketBoard data for ItemId {0} / worldId {1}.",
                    itemId,
                    region);
                return null;
            }
        }
    }
}
