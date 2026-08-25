# Milestone 0 — Foundation

## Goal

Leave a small, documented, buildable, and tested .NET 10 foundation without
implementing SMTP intake, queueing, OAuth, or delivery.

## Included

- solution with Core, Infrastructure, Host, unit-test, and integration-test projects
- basic Blazor host that can run as a Windows Service
- loopback-only default HTTP endpoint and foundation health endpoint
- architecture overview, threat model, ADR-0001, and ADR-0002
- repository governance basics and GitHub Actions build/test workflow

## Exit criteria

- `dotnet build RelayBridge.sln` succeeds without warnings
- `dotnet test RelayBridge.sln` succeeds
- the host integration test observes a healthy `/health` response
- no SMTP listener, Microsoft credential, queue, or delivery implementation exists

## Explicitly deferred

All Milestone 1 and later functionality, including SMTP commands, device security,
spool storage, SQLite, token acquisition, and outbound mail delivery.
