# MaterialLedger

Shop-floor sheet-material ledger for laser, plasma and shear work: inventory
(full sheets, bundles, drops), rack locations, reservations against nests,
closeouts, purchase orders/receiving, vendors and an append-only transaction
history. ASP.NET Core 8 + EF Core on SQL Server.

## Run

```sh
docker run -p 8080:8080 \
  -e 'ConnectionStrings__MaterialLedger=Server=...;Database=MaterialLedger;User ID=...;Password=...;TrustServerCertificate=True' \
  -v materialledger_attachments:/app/App_Data/attachments \
  ghcr.io/ajisaacs/materialledger:latest
```

Tags: `latest` and the full commit SHA of every `master` build.

Settings (`appsettings.json`, overridable with `__` environment variables):

| Key | Purpose |
|---|---|
| `ConnectionStrings:MaterialLedger` | SQL Server connection string (required) |
| `Storage:AttachmentsPath` | Attachment directory (default `App_Data/attachments`) |
| `OpenNest:BaseUrl` | OpenNest.Server nest lookup service (default `http://barge.lan:8091/`) |
| `Api:Keys:*` | API keys for read-only integrations |
| `SeedAccounts` | Initial users; blank `InitialPassword` skips seeding |

## API

See [docs/api/v1.md](docs/api/v1.md).

## Develop

```sh
dotnet test MaterialLedger.Tests
dotnet run --project MaterialLedger.Web
```

## License

MIT — see [LICENSE](LICENSE).
