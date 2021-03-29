# Azure Data Share Usage Report

Display data consumer usage information for an Azure Data Share data provider's sent shares.

## Usage

Requires .NET 5

```
> cd src/AzureDataShareUsageReport
> dotnet run -- --subscription-id 00000000-0000-0000-0000-000000000000 --resource-group-name myresourcegroup --data-share-name mydatashare
```

## Sample output

```json
{
  "totals": {
    "syncedAtLeastOnceButNotSyncedFor30Days": 0,
    "syncedAtLeastOnceButNotSyncedFor30DaysPercentage": 0,
    "sentShares": 90,
    "sentShareWithSyncActivity": 83,
    "sentShareWithSyncActivityPercentage": 92.22
  },
  "tenantSyncs": [
    {
      "name": "Test Tenant",
      "lastSync": "2021-01-01T00:00:00.0000000+00:00"
    }
  ],
  "tenantsSyncedAtLeastOnceButNotSyncedFor30Days": []
}
```

## License

MIT License
