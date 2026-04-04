# Documentation & Website Design for ZeroAlloc.EventSourcing

**Date:** 2026-04-04  
**Status:** APPROVED  
**Scope:** Comprehensive documentation and Docusaurus website for ZeroAlloc.EventSourcing

---

## Overview

ZeroAlloc.EventSourcing will get a professional, first-class documentation presence in the ZeroAlloc ecosystem. Documentation will be comprehensive (serving both beginners and advanced users) and published to `eventsourcing.zeroalloc.net` via a Docusaurus site integrated into the `.website` monorepo.

This brings EventSourcing to parity with other ZeroAlloc libraries (Mediator, Validation, Inject, etc.).

---

## Architecture

### Documentation Source

**Location:** EventSourcing repository (`/docs` folder)  
**Format:** Markdown with code examples  
**Ownership:** EventSourcing repo maintainers  
**Structure:** Organized by learning progression (Getting Started → Core Concepts → Usage → Advanced)

### Website Deployment

**Location:** `.website` monorepo (`apps/docs-eventsourcing/`)  
**Framework:** Docusaurus 3+  
**Build Tool:** pnpm + Turbo  
**Theme:** Shared `@zeroalloc/theme` package (consistent with other ZeroAlloc docs)  
**Hosting:** Cloudflare Workers  
**URL:** `eventsourcing.zeroalloc.net`

### Integration Pattern

1. EventSourcing repo contains `/docs` with all markdown content
2. `.website` repo pulls EventSourcing as git submodule in `repos/eventsourcing`
3. `apps/docs-eventsourcing/docusaurus.config.js` configured to read from submodule
4. Build process: Turbo runs Docusaurus build → outputs static site
5. Deploy to Cloudflare on main branch push

**Result:** One source of truth (EventSourcing repo), automated deployment

---

## Content Organization (Sequential Learning Path)

### 1. Getting Started
**Audience:** First-time users  
**Purpose:** Onboard quickly  
**Pages:**
- Installation (NuGet, dotnet add package)
- Your First Aggregate (step-by-step tutorial)
- Quick Start Example (runnable Order aggregate)

---

### 2. Core Concepts
**Audience:** Beginners learning event sourcing; advanced users learning ZeroAlloc's approach  
**Structure:** Fundamentals first, then deeper explanations  
**Pages:**
- Event Sourcing Fundamentals (what it is, why it matters)
- Events (definition, immutability, types, event types)
- Aggregates (what they are, root entities, how to model)
- Event Store (interface, append/read, in-memory, SQL)
- Snapshots (when needed, types, loading strategies)
- Projections (read models, consistency strategies)
- Deep Dive: Architecture & Design Decisions (ZeroAlloc's philosophy)

---

### 3. Usage Guides
**Audience:** Developers building with EventSourcing  
**Purpose:** Practical patterns and recipes  
**Pages:**
- Domain Modeling (designing aggregates, events, state)
- Building Aggregates (code patterns, best practices)
- Event Replay & Rebuilding (strategies, performance)
- Working with Snapshots (practical usage, snapshot caching)
- Building Projections (materialized views, consistency)
- SQL Adapters (PostgreSQL, SQL Server setup & usage)

---

### 4. Testing
**Audience:** Developers testing event-sourced systems  
**Purpose:** Testing patterns for aggregates, events, projections  
**Pages:**
- Testing Aggregates (unit testing patterns, fixtures)
- Testing Events (validation, serialization)
- Testing Projections (read model tests)
- Integration Testing (Testcontainers, real event store)

---

### 5. Performance & Benchmarks
**Audience:** Developers concerned with performance, zero-allocation design  
**Purpose:** Demonstrate performance characteristics and optimization strategies  
**Pages:**
- Performance Characteristics (from our benchmarking work)
- Zero-Allocation Design (memory efficiency, why it matters)
- Optimization Strategies (tuning aggregates, snapshots, projections)
- Detailed Benchmark Results (tables, charts, analysis)

---

### 6. API Reference
**Audience:** Developers using the library  
**Purpose:** Complete API documentation  
**Auto-generated from XML comments in source code**
**Sections:**
- Core Interfaces (IAggregate, IEventStore, IProjection, ISnapshotStore)
- Built-in Implementations (InMemoryEventStore, InMemorySnapshotStore)
- Extension Points (abstract classes, virtual methods)
- Source Generator APIs (ProjectionDispatchGenerator, AggregateDispatchGenerator)

---

### 7. Advanced
**Audience:** Advanced developers, contributors, extension authors  
**Purpose:** Deep technical patterns and extensibility  
**Pages:**
- Custom Event Store Implementations (SQL, Redis, DynamoDB)
- Custom Snapshot Store Implementations
- Custom Projection Implementations (filters, batching)
- Plugin Architecture (how to extend ZeroAlloc.EventSourcing)
- Contributing to ZeroAlloc.EventSourcing (dev setup, testing, PR process)

---

## Navigation & User Experience

### Sidebar Structure
```
Getting Started
  ├── Installation
  ├── Your First Aggregate
  └── Quick Start Example

Core Concepts
  ├── Event Sourcing Fundamentals
  ├── Events
  ├── Aggregates
  ├── Event Store
  ├── Snapshots
  ├── Projections
  └── Architecture & Design Decisions

Usage Guides
  ├── Domain Modeling
  ├── Building Aggregates
  ├── Event Replay & Rebuilding
  ├── Working with Snapshots
  ├── Building Projections
  └── SQL Adapters

Testing
  ├── Testing Aggregates
  ├── Testing Events
  ├── Testing Projections
  └── Integration Testing

Performance & Benchmarks
  ├── Performance Characteristics
  ├── Zero-Allocation Design
  ├── Optimization Strategies
  └── Benchmark Results

API Reference
  ├── Core Interfaces
  ├── Built-in Implementations
  ├── Extension Points
  └── Source Generators

Advanced
  ├── Custom Event Store Implementations
  ├── Custom Snapshot Store Implementations
  ├── Custom Projections
  ├── Plugin Architecture
  └── Contributing
```

### User Experience Features
- **Sequential navigation:** Prev/Next buttons guide beginners through learning path
- **Breadcrumbs:** Show current location in hierarchy
- **Table of Contents:** Per-page outline for longer documents
- **Code syntax highlighting:** All code examples highlighted with language detection
- **Copy button:** Copy code examples to clipboard
- **Search:** Full-text search across all documentation
- **Responsive design:** Mobile-friendly (inherits from shared theme)

---

## Code Examples & Samples

### Philosophy
- **Every section has code examples**
- **Progressive complexity:** Getting Started is simple; Advanced is sophisticated
- **Consistent domain:** Use Order aggregate throughout (from existing test suite)
- **Runnable where possible:** Examples can be copied and executed locally

### Example Structure
```
docs/examples/
├── 01-getting-started/
│   ├── CreateFirstAggregate.cs
│   └── AppendAndRead.cs
├── 02-domain-modeling/
│   ├── OrderAggregate.cs
│   ├── OrderEvents.cs
│   └── OrderState.cs
├── 03-testing/
│   ├── TestingAggregates.cs
│   └── TestingProjections.cs
└── 04-advanced/
    ├── CustomEventStore.cs
    ├── CustomProjection.cs
    └── CustomSnapshotStore.cs
```

---

## Files to Create

### EventSourcing Repository
```
docs/
├── 01-getting-started.md
├── 02-core-concepts.md
│   ├── events.md
│   ├── aggregates.md
│   ├── event-store.md
│   ├── snapshots.md
│   ├── projections.md
│   └── architecture.md
├── 03-usage-guides.md
│   ├── domain-modeling.md
│   ├── building-aggregates.md
│   ├── replay-rebuilding.md
│   ├── snapshots-usage.md
│   ├── projections-usage.md
│   └── sql-adapters.md
├── 04-testing.md
│   ├── testing-aggregates.md
│   ├── testing-events.md
│   ├── testing-projections.md
│   └── integration-testing.md
├── 05-performance.md
│   ├── characteristics.md
│   ├── zero-allocation.md
│   ├── optimization.md
│   └── benchmarks.md
├── 06-api-reference.md
├── 07-advanced.md
│   ├── custom-event-store.md
│   ├── custom-snapshots.md
│   ├── custom-projections.md
│   ├── plugin-architecture.md
│   └── contributing.md
├── sidebars.js (Docusaurus sidebar config)
├── examples/ (code samples)
└── assets/ (diagrams, images)
```

### `.website` Repository
```
apps/docs-eventsourcing/
├── docusaurus.config.js
├── package.json
├── static/ (favicons, static assets)
└── src/
    ├── css/overrides.css
    └── pages/
```

---

## Build & Deployment

### Local Development
1. Clone EventSourcing repo
2. `pnpm install` in `.website` repo
3. `pnpm run dev --filter=docs-eventsourcing` (starts local dev server)
4. Edit markdown files in EventSourcing `/docs`
5. Hot-reload updates site at localhost

### Deployment Pipeline
1. Push to EventSourcing main branch
2. `.website` repo detects submodule update
3. GitHub Actions triggers build
4. Docusaurus generates static site
5. Deploy to Cloudflare Workers → `eventsourcing.zeroalloc.net`

### Preview Deployments
- PRs against EventSourcing trigger preview deployments
- PR comment includes preview URL
- Easy to review docs changes before merge

---

## Success Criteria

✓ Comprehensive documentation (7 sections, ~80+ pages)  
✓ Sequential learning path (beginner-friendly, advanced deep-dives)  
✓ Code examples throughout (consistent domain, runnable)  
✓ Professional website (eventsourcing.zeroalloc.net)  
✓ Consistent with ZeroAlloc ecosystem (theme, navigation, style)  
✓ Automated deployment (no manual steps)  
✓ Search functionality (full-text)  
✓ Mobile-responsive (all devices)  
✓ API reference auto-generated from XML comments  

---

## Integration Notes

- **No changes to EventSourcing code** (pure documentation)
- **Adds `/docs` folder** with markdown + examples
- **Adds `.website` integration** (new git submodule, new app config)
- **No impact on existing CI/CD** (new workflows added, existing unchanged)
- **Reuses shared theme** (no custom styling needed)

---

## Timeline & Phases

### Phase 1: Structure & Content (Weeks 1-2)
- Write all 80+ markdown pages
- Create code examples
- Set up sidebars.js

### Phase 2: Integration (Week 3)
- Create apps/docs-eventsourcing in `.website`
- Configure Docusaurus
- Set up deployment pipeline

### Phase 3: Polish & Launch (Week 4)
- Review & iterate on content
- Test all links, examples, search
- Deploy to eventsourcing.zeroalloc.net

---

## Notes

- All content written to be **maintainable and updatable** (not one-time docs)
- **API reference auto-generated** from XML comments (stays in sync with code)
- **Examples tested locally** before publishing (runnable code)
- **Docusaurus v3** (latest, modern, excellent DX)
- **pnpm + Turbo** (standard in `.website` repo, no new tooling)
- **Cloudflare Workers** (existing deployment target, proven reliable)

