# Replace PEP with OpenNest.Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read cut programs from OpenNest.Server instead of the PEP API, and remove every PEP reference from Material Ledger. Material holds, found sheets, shortages and closeout stay in Material Ledger, unchanged in behavior.

**Architecture:** Material Ledger keeps the pull model it has today: it does not store programs, it reads them live over HTTP. `PepNestQueryService` is replaced by `OpenNestClient`, which calls OpenNest.Server. Reservations point at an OpenNest nest by its `Guid` id instead of PEP's year + name. The PEP material-number mapping becomes an OpenNest material/grade/thickness mapping in the same `MaterialSpecExternalMappings` table.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 8), EF Core (SQL Server), xUnit. OpenNest.Server is a separate repo (`C:\Users\AJ\Desktop\Projects\OpenNest`), minimal APIs on SQLite.

## What OpenNest.Server offers today

| Need | PEP today | OpenNest.Server today |
|------|-----------|-----------------------|
| List programs | `GET nests/{year}?Status=&Customer=` | `GET /api/nests/query?status=&search=&material=&materialGrade=&thickness=&sort=&order=&offset=&limit=` → `NestPage { items, total, offset, limit }` |
| Program header | `GET nests/{year}/{name}` | `GET /api/nests/{id:guid}` → `NestRecord` (name, customer, status, material, materialGrade, thickness, plateCount, partCount, comments, madeBy, dates) |
| Sheets the program needs (`Plates`) | in nest details | **Missing.** Only inside the `.nest` archive (`GET /api/nests/{id}/file`, `nest.json` → `plates[].size {width,length}`, `plates[].quantity`) |
| Parts (required / nested) | in nest details | **Missing.** Only inside the archive (`drawings[].quantity.required`, count of `plates[].parts` per `drawingId` × plate quantity) |
| Materials | `GET materials` (number, name, grade, thickness) | `GET /api/materials` → `[{ name, grades[] }]` (no thickness, no numeric id) |
| Status values | 12 free-text strings | `draft`, `quote`, `toBeCut`, `hasBeenCut` (camelCase enum) |
| Auth | none | none |

The sheet list is the one thing the hold workflow cannot do without, so OpenNest.Server needs one new read endpoint (Task 1). Material Ledger should not parse `.nest` archives itself: the format belongs to OpenNest and already has lenient-reading rules (`NestArchive`, `NestReader`) that would drift if copied.

## Global Constraints

- Hold behavior must not change: one sheet per reservation, serializable batch creation, found-sheet recording, shortage dialog. Only the source of the program and its sheet sizes changes.
- No status write-back. Material Ledger stays read-only against OpenNest.Server, as it was against PEP.
- `ExpectedRemainderSource.OpenNest` already exists on `MaterialReservation` and is unrelated to this work; leave it.
- Public API (`docs/api/v1.md`, `CommandContracts.cs`) changes `pepNestYear`/`pepNestName` to `openNestNestId`/`nestName`. That is a breaking change for any API caller; check `docs/api/v1.md` consumers before shipping.

## Open decisions (answer before Task 3)

1. ~~**OpenNest.Server URL.**~~ Decided: `http://barge.lan:8091/`. This is the `OpenNest:BaseUrl` value in `appsettings.json`.
2. **Active PEP holds at cutover.** Reservations still `Proposed`/`Approved`/`AwaitingCloseout` against a PEP nest keep their text reference (Task 3 migration) but lose their link to a nest page. Either close them out before deploying, or accept that they show as "legacy PEP nest" text.

---

## Task 1 (OpenNest repo): `GET /api/nests/{id}/sheets`

**Files:**
- Modify: `OpenNest.Server/NestArchive.cs` — extend the private `NestJson`/`PlateJson`/`DrawingJson` shapes with `Size`, `Quantity` and drawing `Name`/`Quantity.Required`, and add a `ReadSheets(byte[] file)` method alongside `Apply`, reusing the same bounded read and trailing-empty-plate rule.
- Create: `OpenNest.Data/NestSheets.cs` — wire contract:

```csharp
public sealed class NestSheets
{
    public Guid NestId { get; init; }
    public int Revision { get; init; }
    public IReadOnlyList<NestSheetSize> Sheets { get; init; } = [];   // grouped by width x length
    public IReadOnlyList<NestPartCount> Parts { get; init; } = [];
}
public sealed class NestSheetSize { public double Width { get; init; } public double Length { get; init; } public int Quantity { get; init; } }
public sealed class NestPartCount { public string Name { get; init; } = ""; public int Required { get; init; } public int Nested { get; init; } }
```

- Modify: `OpenNest.Server/Program.cs` — `app.MapGet("/api/nests/{id:guid}/sheets", ...)` → 404 when the nest or its file is missing.
- Test: `OpenNest.Server.Tests` — pin against real `NestWriter` output like `NestArchiveTests` does.

Rules:
- Sheet quantity = sum of `plate.quantity` over plates that are not the trailing empty plate, grouped by `(width, length)` with width as the short side.
- `Nested` per drawing = Σ over plates of (parts on that plate with that `drawingId`) × `plate.quantity`, skipping parts whose drawing is missing (same rule as `Apply`).

- [ ] Write failing tests (single plate, plate quantity > 1, trailing empty plate ignored, two plates same size grouped, missing drawing skipped)
- [ ] Implement `NestArchive.ReadSheets` and the endpoint
- [ ] Deploy OpenNest.Server and confirm `curl <base>/api/nests/<id>/sheets` against a real to-be-cut nest

## Task 2: `OpenNestClient` replaces `PepNestQueryService`

Additive only: the PEP client stays registered and in use until Task 5 moves the pages over, so the app keeps building and working between tasks.

**Files:**
- Create: `MaterialLedger.Web/Services/OpenNestClient.cs`
- Modify: `MaterialLedger.Web/Program.cs` — register `AddHttpClient<OpenNestClient>` from `OpenNest:BaseUrl` next to the PEP registration (`:51-55`), and keep the 15 s timeout.
- Modify: `MaterialLedger.Web/appsettings.json` — add `"OpenNest": { "BaseUrl": "http://barge.lan:8091/" }` beside `PepApi`.
- Task 5 deletes `PepNestQueryService.cs` and the `PepApi` section.

**Interfaces (produced):**

```csharp
public sealed class OpenNestClient(HttpClient client)
{
    Task<OpenNestPage> QueryNestsAsync(OpenNestQuery query, CancellationToken ct);       // GET api/nests/query
    Task<OpenNestRecord?> GetNestAsync(Guid id, CancellationToken ct);                    // GET api/nests/{id}, null on 404
    Task<OpenNestSheets?> GetSheetsAsync(Guid id, CancellationToken ct);                  // GET api/nests/{id}/sheets, null on 404
}
public sealed record OpenNestQuery(string? Status, string? Search, int Offset = 0, int Limit = 100);
```

- DTOs mirror `NestRecord`, `NestPage`, `NestSheets` (Task 1). Deserialize with `JsonSerializerDefaults.Web` + `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`; keep `Status` as a string on the Material Ledger side so an unknown future status still renders.
- Send `status` only when set, `search` only when non-blank, `sort=dateCreated&order=desc`. OpenNest rejects unknown or repeated query keys, so never send `year` or `customer`.
- Width/length come back as `double`; convert to `decimal` rounded to 3 places (matches `decimal(10,3)` columns) at the DTO boundary, since `BuildReservationPlan` matches sizes by exact equality against inventory.

- [ ] Write failing tests with a stub `HttpMessageHandler`: query string shape, 404 → null, enum-as-string status, double→decimal rounding
- [ ] Implement

## Task 3: Data model — reservations and mappings (additive)

Like Task 2, this is additive. The PEP columns, the PEP mapping rows and `PepMaterialMappingService` all stay so the PEP pages keep working. Task 6 drops them.

**Files:**
- Modify: `MaterialLedger.Data/Models/MaterialReservation.cs` — add `Guid? OpenNestNestId` and `string? NestName` next to `PepNestYear`/`PepNestName`. `NestName` is a display snapshot taken when the hold is created, so hold lists never need an HTTP call.
- Modify: `MaterialLedger.Data/MaterialLedgerDbContext.cs:96` — the check constraint becomes `[ExternalSystem] IN (N'PEP', N'OpenNest')`. At `:207`, `NestName` gets a max length of 200. Add an index on `OpenNestNestId`.
- Modify: `MaterialLedger.Data/Models/MaterialSpecExternalMapping.cs:10` — update the doc comment to list both systems.
- Create: migration `AddOpenNestReferences` — adds the two nullable columns and the index, and swaps the check constraint. No data changes.
- Create: `MaterialLedger.Data/Materials/OpenNestMaterialMappingService.cs` beside the PEP service (don't rename it). It takes `MapOpenNestMaterialRequest(string Material, string Grade, decimal ThicknessIn, long MaterialSpecId)`, logs operations as `MapOpenNestMaterial`, and stores mappings with `ExternalSystem = "OpenNest"`. Copy the transaction and operation-log pattern from `PepMaterialMappingService`. Add a static `OpenNestMaterialKey.For(material, grade, thicknessIn)` that Task 5's Details page will also use to look mappings up.

Mapping key: `ExternalKey = "{material}|{grade}|{thickness:0.####}"`. Trim each part, collapse whitespace and lower-case it, the same normalization as OpenNest's `SharedListNames.Key`. A blank grade stays an empty segment. There's one approved mapping per key, just as there's one per PEP material number today. Reject a blank material or a thickness ≤ 0.

- [ ] Update `SchemaShapeTests` first (new columns, index, constraint) and see it fail
- [ ] Make the model changes, run `dotnet ef migrations add AddOpenNestReferences`, then `dotnet ef database update`
- [ ] Write `OpenNestMaterialMappingServiceTests` covering create, remap, replay, key normalization and rejects, modeled on `PepMaterialMappingServiceTests`, then implement the service

## Task 4: Reservation plumbing (additive)

Every place that reads or writes the PEP pair also carries `OpenNestNestId` + `NestName`. The PEP fields stay, so PEP holds and pages keep working. Task 6 removes them, which is when the API break lands. Display order: OpenNest reference first, then the PEP pair, then job/drawing as today.

- `MaterialLedger.Data/Reservations/ReservationService.cs:21-22, 89-90, 170-171` (request record, create, batch create). Update the comment at `:128`.
- `MaterialLedger.Data/Inventory/InventoryQueryService.cs:313-330` — work reference text becomes `Nest {NestName}`.
- `MaterialLedger.Web/Services/WorkQueueQueryService.cs:79-80, 111, 126-127`
- `MaterialLedger.Web/Shared/ReservationWorkReference.cs` and `MaterialLedger.Web/Pages/Shared/ReservationWorkReference.cs` — link to `/Nests/{id}` when `OpenNestNestId` is set, plain text otherwise (legacy PEP holds).
- `MaterialLedger.Web/Pages/Reservations/Create.cshtml(.cs)` — the manual hold form's nest fields.
- `MaterialLedger.Web/Api/Contracts/CommandContracts.cs:34-35, 58-59`, `Api/CommandEndpoints.cs:108, 131`, `Api/ReadEndpoints.cs:113-114` — `openNestNestId` (Guid) + `nestName`; validation message at `:108` becomes "nestName is required when openNestNestId is set" or similar.
- `docs/api/v1.md` — same rename.

- [ ] Port `ReservationServiceTests`, `ReservationDetailsContextPresentationTests`, `ReservationHoldPresentationTests` to the new fields first
- [ ] Make the changes until the solution builds and every PEP path still works alongside the new one
- [ ] (Task 6) Remove the PEP fields from all of the above and from `docs/api/v1.md`

## Task 5: Pages — `/PepNests` → `/Nests`

**Files:**
- Move `MaterialLedger.Web/Pages/PepNests/` → `Pages/Nests/` (namespace `MaterialLedger.Web.Pages.Nests`).
- Index: `@page` keeps paging-free list for now (limit 100, newest first). Filters become **Status** (dropdown `Draft / Quote / To Be Cut / Has Been Cut`, default `To Be Cut`, wire values `draft/quote/toBeCut/hasBeenCut`) and **Search** (free text → OpenNest `search`, which already covers customer, material, grade, made-by and comments). The year box and customer dropdown go away; OpenNest has neither filter. Show material as `{material} {grade} {thickness}"`.
- Details: route `@page "{id:guid}"`. Header from `GetNestAsync`; `ProgramSheetRequirements` from `GetSheetsAsync(id).Sheets` (replaces `Nest.Plates` at `Details.cshtml.cs:156-160`); parts table from `.Parts` (`Name`, `Required`, `Nested`). Hold lookups query `OpenNestNestId == id`. Material mapping looks up the key built from the record's material/grade/thickness. "Already cut" banner keys off `hasBeenCut`.
- Error text: "OpenNest nests could not be reached." in place of the PEP messages.
- Delete `MaterialLedger.Web/Services/PepNestQueryService.cs`, its registration in `Program.cs`, and the `PepApi` section in `appsettings.json`.
- `MaterialLedger.Web/Pages/Index.cshtml:150` and `Pages/Shared/_Layout.cshtml` nav — "Nests", pointing at `/Nests/Index`.

- [ ] Port `PepNestPresentationTests` → `NestPresentationTests` and `NavigationPresentationTests:131` first
- [ ] Move and rewrite the pages
- [ ] Run the app against the real OpenNest.Server: list loads, open a to-be-cut nest, map its material, create holds, record a found sheet for a shortage

## Task 6: Cleanup

- [ ] Migration `RemovePepReferences`: copy legacy holds' text with `UPDATE MaterialReservations SET NestName = CONCAT('PEP ', PepNestYear, ': ', PepNestName) WHERE PepNestName IS NOT NULL AND NestName IS NULL`, drop `PepNestYear` and `PepNestName`, run `DELETE FROM MaterialSpecExternalMappings WHERE ExternalSystem = N'PEP'`, and narrow the check constraint to `IN (N'OpenNest')`. Then delete `PepMaterialMappingService` and its tests.
- [ ] `wwwroot/css/pep-nests.css` → `nests.css`, `.pep-nest-*`/`.pep-*-dialog` → `.nest-*`; update `app.css:13` and `responsive.css:243-496`
- [ ] `README.md:25` config table: `OpenNest:BaseUrl`
- [ ] `grep -ri pep` over `MaterialLedger.*` and `README.md` returns only migration history
- [ ] Full test run green; leave `docs/superpowers/**` and `.hermes/plans/**` as historical records
