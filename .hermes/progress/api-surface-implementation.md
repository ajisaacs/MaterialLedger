# MaterialLedger API Surface — Implementation Progress

Plan: `.hermes/plans/2026-08-12_202437-api-surface.md`
Started/completed: 2026-08-12

## Baseline and verification environment
- Original baseline: `master` at `67a78b8`.
- `MATERIALLEDGER_TEST_ADMIN_CONNECTION` remains unavailable. SQL-backed test classes are fixture-blocked before test logic; no secret value was recorded.

## Completed commits
- `2f26b4c refactor: expose idempotent command replay metadata` — Task 3.
- `8717326 feat: add RFC 9457 API error pipeline` — Task 1.
- `fdc881f feat: add authenticated API read endpoints` — Task 2.
- `9e30dfa feat: add idempotent material command API` — Task 4, including durable actor-bound replay ownership.
- `53fd47e feat: add secure attachment transfer API` — Task 5, including shared Razor/API upload policy and multipart-boundary handling.
- `ccf7b20 docs: document v1 API contract` — Task 6.

## Completed work and gates
- Task 1: API composition/RFC 9457 behavior. Strict review PASS; quality/security APPROVED.
- Task 2: explicit DTOs and authenticated read endpoints. Strict review PASS; quality/security APPROVED.
- Task 3: replay-aware idempotent service result metadata. Completed; shared guard further hardened in Task 4.
- Task 4: discriminated command API. Strict review PASS; quality/security APPROVED; `CommandEndpointTests` 18/18 passed; zero-warning build.
- Task 5: secure attachment storage and transfer API. Strict review PASS; quality/security APPROVED; final Razor/API attachment suite 17/17 passed; zero-warning build; diff check clean. SQL-backed storage tests were fixture-blocked.
- Task 6: documented complete contract in `docs/api/v1.md`. Strict review PASS; quality/security APPROVED. Representative API suite 56/56 passed; zero-warning build; diff check clean.

## Delivered API decisions
- Versioned `/api/v1` routes with RFC 9457 `application/problem+json` scoped to APIs, without changing normal Razor cookie redirects.
- Explicit DTO reads, typed discriminated commands, and durable idempotency replay/conflict/actor-ownership rules.
- Attachment uploads use generated root-contained storage, streaming SHA-256/length, scoped downloads, role policy, shared type/extension/size policy for API and Razor, and safe parser-size behavior.
- Documentation avoids overclaims: HTTPS use is advisory in the current app; attachment upload uses the first named `file` part; MIME and extension allowlists are independent.

## Remaining limitation
- No real SQL fixture execution was possible without `MATERIALLEDGER_TEST_ADMIN_CONNECTION`; do not report SQL-dependent service/storage tests as passed.
