using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Management.DataShare;
using Microsoft.Azure.Management.DataShare.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Rest;
using Microsoft.Rest.Azure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AzureDataShareUsageReport
{
    // Usage:
    //   AzureDataShareUsageReport [options]
    //
    // Options:
    //   --subscription-id <subscription-id>            subscriptionId
    //   --resource-group-name <resource-group-name>    resourceGroupName
    //   --data-share-name <data-share-name>            dataShareName
    //   --version                                      Display version information

    class Program
    {
        private static string ResourceGroupName;
        private static string DataShareName;
        private static DataShareManagementClient Client;

        static async Task Main(string subscriptionId, string resourceGroupName, string dataShareName)
        {
            //
            // Validate
            //

            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                Console.WriteLine($"{nameof(subscriptionId)} must be provided.");
                return;
            }

            if (string.IsNullOrWhiteSpace(resourceGroupName))
            {
                Console.WriteLine($"{nameof(resourceGroupName)} must be provided.");
                return;
            }

            if (string.IsNullOrWhiteSpace(dataShareName))
            {
                Console.WriteLine($"{nameof(dataShareName)} must be provided.");
                return;
            }

            //
            // Initialize
            //

            ResourceGroupName = resourceGroupName;
            DataShareName = dataShareName;

            var tokenProvider = new AzureServiceTokenProvider();
            var accessToken = await tokenProvider.GetAccessTokenAsync("https://management.azure.com");

            Client = new DataShareManagementClient(new TokenCredentials(accessToken))
            {
                SubscriptionId = subscriptionId
            };

            //
            // Begin building report
            //

            var tenantSyncs = new Dictionary<string, UsageReportTenantData>();

            var shares = await GetAllPages(skipToken => Client.Shares.ListByAccountAsync(ResourceGroupName, DataShareName, skipToken));
            var shareNames = shares.Select(x => x.Name);

            foreach (var shareName in shareNames)
            {
                var syncs = await GetAllPages(skipToken => Client.Shares.ListSynchronizationsAsync(ResourceGroupName, DataShareName, shareName));

                if (!syncs.Any())
                {
                    continue;
                }

                // var tenantLast30DaysCost = await GetTenantLast30DaysCost(shareName, syncs);

                // Since each sent share is for a single tenant we can use the first sync for tenant name
                var tenantName = syncs.First().ConsumerTenantName;

                if (tenantSyncs.ContainsKey(tenantName))
                {
                    throw new InvalidOperationException("This report does not support multiple tenants with the same name.");
                }

                tenantSyncs.Add(tenantName, new UsageReportTenantData
                {
                    LastSync = syncs.Max(x => x.StartTime).GetValueOrDefault(),
                    // UnofficialCostLast30Days = tenantLast30DaysCost,
                });
            }

            var notSyncedFor30DaysCount = tenantSyncs
                .Select(x => x.Value)
                .Count(x => x.LastSync < DateTime.UtcNow.AddDays(-30));

            var usageReport = new
            {
                Totals = new
                {
                    // UnofficialCostLast30Days = tenantSyncs.Select(x => x.Value).Sum(x => x.UnofficialCostLast30Days),
                    SyncedAtLeastOnceButNotSyncedFor30Days = notSyncedFor30DaysCount,
                    SyncedAtLeastOnceButNotSyncedFor30DaysPercentage = shares.Count() > 0
                        ? decimal.Round(100 - ((shares.Count() - notSyncedFor30DaysCount) / (decimal)shares.Count() * 100), 2)
                        : -1,
                    SentShares = shares.Count(),
                    SentShareWithSyncActivity = tenantSyncs.Count(),
                    SentShareWithSyncActivityPercentage = shares.Count() > 0
                        ? decimal.Round(100 - ((shares.Count() - tenantSyncs.Count()) / (decimal)shares.Count() * 100), 2)
                        : -1,
                },
                TenantSyncs = tenantSyncs
                    .Select(x => new
                    {
                        Name = x.Key,
                        LastSync = x.Value.LastSync,
                        // UnofficialCostLast30Days = x.Value.UnofficialCostLast30Days,
                    })
                    .OrderBy(x => x.Name),
                TenantsSyncedAtLeastOnceButNotSyncedFor30Days = tenantSyncs
                    .Where(x => x.Value.LastSync < DateTime.UtcNow.AddDays(-30))
                    .Select(x => x.Key)
                    .OrderBy(x => x),
            };

            //
            // Output
            //

            var output = JsonSerializer.Serialize(usageReport, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            Console.WriteLine(output);
        }

        private static async Task<decimal> GetTenantLast30DaysCost(string shareName, IEnumerable<ShareSynchronization> syncs)
        {
            var allSyncDetails = new List<IEnumerable<SynchronizationDetails>>();
            foreach (var sync in syncs)
            {
                // TODO: This isn't optimized to only retrieve the 30 days necessary.
                // Can look into this more if the report has it re-added (currently commented out).
                var syncDetails = await GetAllPages(skipToken => Client.Shares.ListSynchronizationDetailsAsync(
                    ResourceGroupName, DataShareName, shareName, sync, skipToken));

                allSyncDetails.Add(syncDetails);
            }

            var syncDetailsLast30Days = allSyncDetails
                .SelectMany(x => x)
                .Where(x => x.StartTime >= DateTime.UtcNow.AddDays(-30));

            // https://azure.microsoft.com/en-us/pricing/calculator/?service=data-share
            var datasetSnapshotCost = syncDetailsLast30Days.Count() * .05m;
            var snapshotExecutionCost = syncDetailsLast30Days
                .Sum(x => x.VCore.GetValueOrDefault() * x.DurationMs.GetValueOrDefault() / 3_600_000 * .50m); // 3_600_000 ms per hour

            var tenantLast30DaysCost = datasetSnapshotCost + snapshotExecutionCost;

            return tenantLast30DaysCost;
        }

        private static async Task<IEnumerable<T>> GetAllPages<T>(Func<string, Task<IPage<T>>> getResults)
        {
            var returnParameter = getResults.Method.ReturnParameter.ToString();

            var results = new List<T>();

            string skipToken = null;
            while (true)
            {
                Console.WriteLine($"{DateTime.Now}: Retrieving page of {returnParameter}");
                var resultPage = await getResults(skipToken);

                results.AddRange(resultPage);

                skipToken = resultPage.NextPageLink == null
                    ? (string)null
                    : QueryHelpers.ParseNullableQuery(new Uri(resultPage.NextPageLink).Query)["$skipToken"];

                if (string.IsNullOrEmpty(skipToken))
                {
                    break;
                }
            }

            return results;
        }

        private class UsageReportTenantData
        {
            // public decimal UnofficialCostLast30Days { get; set; }

            public DateTimeOffset LastSync { get; set; }
        }
    }
}
