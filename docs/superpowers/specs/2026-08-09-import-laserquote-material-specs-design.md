# Import LaserQuote Material Specs — design

## Context

`MaterialSpecs` has no seed data today — every row is currently created ad hoc through
operator UI flows (`Census/NewOpeningBalance`, the in-flight `Receiving/NewReceipt`)
via `MaterialSpecService.GetOrCreateAsync`. LaserQuote (a separate shop-floor quoting
system, accessed in this session only via its MCP tool — MaterialLedger has no live
API/DB connection to it) already has a curated list of 10 materials and 110
material/gauge/thickness combinations that the shop actually uses. This design imports
that list as one-time reference data so operators aren't hand-typing family/grade/
thickness combinations that already exist elsewhere.

## Non-goals

- **No ongoing sync or external-system tracking.** This is a one-time bulk seed, not an
  integration. `MaterialSpecExternalMapping` (the mechanism used for `PEP`) is not used
  here — there is no LaserQuote spec ID stored anywhere in MaterialLedger, and no
  mechanism to detect if LaserQuote's list changes later. A future re-import means
  updating the static list in code and redeploying.
- **No live API call at runtime.** The 110 rows are captured as a static snapshot (see
  Seed Data below) embedded in source, not fetched from LaserQuote at startup.
- **No schema or migration changes.** `MaterialSpec`, `MaterialSpecService.GetOrCreateAsync`,
  and the `NormalizedKey` unique index already support this without modification.
- **Tolerance values are dropped.** LaserQuote tracks a `±` tolerance per spec;
  `MaterialSpec` has no field for it, and none is added.

## Data flow

New file `MaterialLedger.Web/Services/MaterialSpecSeeder.cs`, following the exact
`LocationSeeder` pattern:

1. A static list of `(family, grade, finish, thicknessIn, displayName)` tuples (110
   entries, see Seed Data).
2. `SeedAsync()` loops the list and calls `MaterialSpecService.GetOrCreateAsync` for
   each — already idempotent (looks up by family+grade+finish+thickness before
   inserting, and falls back gracefully on a `NormalizedKey` unique-index race).
3. Registered as `builder.Services.AddScoped<MaterialSpecSeeder>()` in `Program.cs` and
   invoked in the same startup block as `AppUserSeeder`/`LocationSeeder`, so it runs on
   every boot and is a no-op after the first successful run.

No new database objects, no new grants file — `MaterialSpecs` write access is already
granted (v1/v2, per the `ReceiveMaterial` design notes).

## Grade / Finish mapping

LaserQuote's "material" (type + grade) and "gauge" fields map to MaterialLedger's
`MaterialFamily` / `Grade` / `Finish` / `ThicknessIn` as a fixed, hardcoded table (10
materials — not worth a generic parser):

| LaserQuote material | MaterialFamily | Grade | Finish |
|---|---|---|---|
| Aluminum, 5052 | Aluminum | 5052 | — |
| Aluminum, 6061 | Aluminum | 6061 | — |
| Stainless Steel, 304 | Stainless Steel | 304 | — |
| Steel, A36 | Steel | A36 | — |
| Steel, A572 GR50 | Steel | A572 GR50 | — |
| Steel, A1008 CRS | Steel | A1008 | CRS |
| Steel, A1011 HR | Steel | A1011 | HR |
| Steel, A1011 HRPO | Steel | A1011 | HRPO |
| Steel, HRPO | Steel | HRPO | — |
| Steel, Floor Plate | Steel | Floor Plate | — |

`Steel/HRPO` and `Steel/A1011/HRPO` are two distinct LaserQuote materials with an
identical thickness table — both are imported as separate rows (distinct
`NormalizedKey`s: `Grade` differs) because they are distinct materials in the source
system, not a duplicate to be collapsed.

`DisplayName` format: `"{Family} {Grade}{ Finish?} — {gauge label} ({thickness}\")"`,
e.g. `Steel A1011 HR — 16ga (0.0598")`, `Aluminum 5052 — 30ga (0.0100")`,
`Steel Floor Plate — 3/16 (0.1875")`.

## Seed data

Four gauge/thickness tables are shared across materials (identical values in the
LaserQuote source), referenced by name below to avoid repeating 110 rows of numbers
four times.

**Table G1** (ga → in): 30→0.0100, 28→0.0126, 26→0.0159, 24→0.0201, 22→0.0253,
20→0.0320, 18→0.0403, 16→0.0508, 14→0.0641, 13→0.0720, 12→0.0808, 11→0.0910,
10→0.1019
&nbsp;&nbsp;Used by: Aluminum 5052, Aluminum 6061 (13 rows each = 26 rows).

**Table G2** (ga → in): 30→0.0120, 28→0.0149, 26→0.0179, 24→0.0239, 22→0.0299,
20→0.0359, 18→0.0478, 16→0.0598, 14→0.0747, 13→0.0897, 12→0.1046, 11→0.1196,
10→0.1345
&nbsp;&nbsp;Used by: Steel A1008/CRS, Steel A1011/HR, Steel A1011/HRPO, Steel HRPO
(13 rows each = 52 rows).

**Table G3** (ga → in): 30→0.0125, 28→0.0156, 26→0.0187, 24→0.0250, 22→0.0313,
20→0.0375, 18→0.0500, 16→0.0625, 14→0.0781, 13→0.0938, 12→0.1094, 11→0.1250,
10→0.1406
&nbsp;&nbsp;Used by: Stainless Steel 304 (13 rows).

**Table P1** (label → in): 3/16→0.1875, 3ga→0.2391, 1/4→0.2500, 5/16→0.3125,
3/8→0.3750, 1/2→0.5000, 5/8→0.6250, 3/4→0.7500, 1→1.0000
&nbsp;&nbsp;Used by: Steel A36, Steel A572 GR50 (9 rows each = 18 rows).

**Single row**: Steel, Floor Plate — 3/16" → 0.1875 in (1 row).

Total: 26 + 52 + 13 + 18 + 1 = **110 rows**, matching LaserQuote's current
`list_material_specs` count.

## Testing

`MaterialLedger.Tests/MaterialSpecSeederTests.cs`, integration tests against the real
throwaway test DB (`IClassFixture<SqlServerTestDatabaseFixture>`), same shape as
`LocationSeeder`'s coverage:

- Running `SeedAsync()` against an empty database creates exactly 110 `MaterialSpec`
  rows.
- Running it a second time is a no-op — still 110 rows, no duplicates.
- Spot checks: `Steel/A36/3/16` → `0.1875`, `Aluminum/5052/30ga` → `0.0100`,
  `Steel/A1011/HRPO/16ga` → `0.0598` correctly split into `Grade=A1011`/`Finish=HRPO`,
  `Steel/Floor Plate/3/16` → `0.1875` with `Finish=null`.
- `Steel/HRPO` and `Steel/A1011/HRPO` (same thickness table, different grade) both
  present as distinct rows, not deduped against each other.

Full existing integration suite re-run to confirm no regressions.
