# VaultFlow System Diagram

This diagram shows the main runtime components and the two ways a business event
reaches the authoritative inventory ledger: directly while online, or through
the device outbox and synchronization pipeline while offline.

![VaultFlow system architecture overview](assets/vaultflow-system-overview.png)

## System overview

```mermaid
flowchart LR
    subgraph Users[People and external actors]
        Owner[Owner / Admin\nBrowser]
        Cashier[Cashier / Store staff]
        Supplier[Supplier\nExternal]
    end

    subgraph Devices[Store devices - untrusted boundary]
        Client[Pos.Client\n.NET MAUI Blazor Hybrid\nWindows / Android]
        LocalUI[POS and inventory UI\nSharedUI components]
        LocalDB[(Encrypted SQLite\nscoped offline data)]
        Outbox[Outbox\nordered business events]
        Hardware[Hardware adapters\nscanner / printer / cash drawer]
        LocalUI --> Client
        Cashier --> LocalUI
        Client --> LocalDB
        Client --> Outbox
        Client --> Hardware
    end

    subgraph Server[Central server]
        Proxy[Caddy\nHTTPS / TLS]
        API[Pos.Api\nASP.NET Core Web API\nAuthN/AuthZ + endpoints + SignalR]
        App[Pos.Application\ncommands / queries\nvalidation / authorization\nunit of work]
        Domain[Pos.Domain\ninvariants / state machines\nvalue objects]
        Infra[Pos.Infrastructure\nEF Core / sync / ledger\nnotifications / identity]
        Proxy --> API
        API --> App
        App --> Domain
        App --> Infra
        Infra --> Domain
    end

    subgraph Data[Authoritative data and supporting services]
        PG[(PostgreSQL 17\nauthoritative history)]
        Ledger[Immutable inventory ledger\nInventoryMovement rows]
        Balance[InventoryBalance\nmaterialized projection]
        ChangeFeed[Sync change feed\nmonotonic cursor]
        Audit[Audit log + notifications]
        Redis[(Redis optional\ncache / SignalR backplane)]
        PG --> Ledger
        Ledger --> Balance
        PG --> ChangeFeed
        PG --> Audit
    end

    Owner -->|HTTPS| Proxy
    Supplier -->|manual / out of band| API
    Client -->|HTTPS JSON\nwhen online| Proxy
    Infra --> PG
    API -.-> Redis
    Outbox -->|push events\nretry + ordered| API
    API -->|pull changes\nscoped by location| Client
    Infra --> Ledger
    App -.->|same use cases and ledger code| Client
```

## Online and offline sale flow

```mermaid
sequenceDiagram
    autonumber
    participant U as Cashier
    participant C as Pos.Client
    participant L as Local SQLite / Outbox
    participant A as Pos.Api
    participant P as Application pipeline
    participant D as Domain + Ledger
    participant DB as PostgreSQL

    U->>C: Create sale
    C->>P: Dispatch command
    P->>P: Correlation -> logging -> validation\n-> authorization -> idempotency -> unit of work

    alt Online
        C->>A: HTTPS command
        A->>P: Dispatch command
        P->>D: Validate and execute sale
        D->>DB: Append immutable double-entry movements\n+and sale in one transaction
        DB-->>D: Commit / result
        D-->>A: Accepted result + document number
        A-->>C: Sale result
    else Offline
        P->>D: Execute same whitelisted use case locally
        D->>L: Save sale, movement, and outbox event
        L-->>C: Immediate local result
        C-->>U: Sale complete; sync pending
        C->>A: Later: push EventId + device sequence
        A->>DB: Check idempotency and sequence
        A->>P: Re-authorize and re-validate now
        P->>D: Apply business effect if accepted
        D->>DB: Commit effect + processed event + audit\nin one transaction
        DB-->>A: Accepted / Duplicate / Rejected / Review / Conflict
        A-->>C: Original stored result
    end
```

## Synchronization loop

```mermaid
flowchart TB
    Event[Local business event\nEventId + device sequence + payload hash]
    Queue[Outbox\nPending -> Sending -> Synchronized\nor Failed / RequiresReview / Conflict]
    Push[POST sync/push\nmax 100 events or 512 KB\nstrict device-sequence order]
    Gate{Server checks}
    Idempotency[Idempotency\nreturn stored result on retry]
    Ordering[Ordering\nbuffer gaps; reject or defer duplicates]
    Auth[Current device + user authorization]
    Validate[Current domain validation]
    Apply[Apply sale / transfer / receipt / adjustment]
    Result[Persist result + audit + checkpoint\nin same transaction]
    Feed[Change feed\nscoped by location]
    Pull[GET sync/pull\nfrom stored cursor]
    ApplyLocal[Apply page + advance cursor\nin one SQLite transaction]
    Rebase[410 / stale cursor -> full baseline]

    Event --> Queue --> Push --> Gate
    Gate --> Idempotency --> Ordering --> Auth --> Validate --> Apply --> Result
    Result --> Queue
    Feed --> Pull --> ApplyLocal --> Local[Updated local cache\nmaster data / notifications / directives]
    Pull -.->|cursor outside retention| Rebase --> Local
```

## Inventory integrity boundary

```mermaid
flowchart LR
    Command[Sale / purchase / transfer /\nreturn / adjustment]
    Pipeline[Validated command\npermissions + location scope\n+ idempotency]
    Movement[Append-only InventoryMovement\n2+ signed legs sum to zero]
    Trigger[PostgreSQL triggers\nreject invalid balance changes]
    Projection[InventoryBalance\nupdated in same transaction]
    Query[Stock queries / reports]
    Audit[Audit + notifications]

    Command --> Pipeline --> Movement
    Movement --> Trigger --> Projection
    Movement --> Audit
    Projection --> Query
    Direct[Direct stock quantity write] -.->|blocked by domain types,\nEF interceptor, triggers, and DB grants| Trigger
```

### Key rules represented above

- PostgreSQL is authoritative; the device is an event producer, not a table replica.
- Offline execution is limited to an explicit command whitelist and never widens authority.
- Every sync retry is safe because `EventId` and the stored payload hash provide idempotency.
- Stock changes only through immutable, double-entry inventory movements.
- `InventoryBalance` is a rebuildable projection, not an independent source of truth.
- The device clock and UI permissions are not trusted; the server re-checks authority and ordering.
