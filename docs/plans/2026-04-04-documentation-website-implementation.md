# Documentation & Website Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create comprehensive documentation (80+ pages) and professional Docusaurus website for ZeroAlloc.EventSourcing deployed to eventsourcing.zeroalloc.net.

**Architecture:** Documentation lives in EventSourcing repo (`/docs`), pulled into `.website` monorepo as git submodule, built with Docusaurus + Turbo, deployed to Cloudflare Workers with automated pipeline.

**Tech Stack:** Markdown (content), Docusaurus 3 (site framework), pnpm/Turbo (build), Cloudflare Workers (hosting), GitHub Actions (CI/CD)

---

## Task 1: Set Up Documentation Folder Structure

**Files:**
- Create: `docs/sidebars.js`
- Create: `docs/README.md`
- Create: `docs/examples/` (directory)
- Create: `docs/assets/` (directory)

**Step 1: Create sidebars.js config**

Create file: `docs/sidebars.js`

```javascript
module.exports = {
  docsSidebar: [
    {
      type: 'category',
      label: 'Getting Started',
      items: [
        'getting-started/installation',
        'getting-started/first-aggregate',
        'getting-started/quick-start',
      ],
    },
    {
      type: 'category',
      label: 'Core Concepts',
      items: [
        'core-concepts/fundamentals',
        'core-concepts/events',
        'core-concepts/aggregates',
        'core-concepts/event-store',
        'core-concepts/snapshots',
        'core-concepts/projections',
        'core-concepts/architecture',
      ],
    },
    {
      type: 'category',
      label: 'Usage Guides',
      items: [
        'usage-guides/domain-modeling',
        'usage-guides/building-aggregates',
        'usage-guides/replay-rebuilding',
        'usage-guides/snapshots-usage',
        'usage-guides/projections-usage',
        'usage-guides/sql-adapters',
      ],
    },
    {
      type: 'category',
      label: 'Testing',
      items: [
        'testing/testing-aggregates',
        'testing/testing-events',
        'testing/testing-projections',
        'testing/integration-testing',
      ],
    },
    {
      type: 'category',
      label: 'Performance & Benchmarks',
      items: [
        'performance/characteristics',
        'performance/zero-allocation',
        'performance/optimization',
        'performance/benchmark-results',
      ],
    },
    'api-reference',
    {
      type: 'category',
      label: 'Advanced',
      items: [
        'advanced/custom-event-store',
        'advanced/custom-snapshots',
        'advanced/custom-projections',
        'advanced/plugin-architecture',
        'advanced/contributing',
      ],
    },
  ],
};
```

**Step 2: Create README.md**

Create file: `docs/README.md`

```markdown
# ZeroAlloc.EventSourcing Documentation

This directory contains the source documentation for ZeroAlloc.EventSourcing.

## Structure

- `getting-started/` - Installation and quick start guides
- `core-concepts/` - Event sourcing fundamentals and ZeroAlloc approach
- `usage-guides/` - Practical patterns and recipes
- `testing/` - Testing strategies and patterns
- `performance/` - Performance characteristics and benchmarks
- `api-reference/` - API documentation (auto-generated)
- `advanced/` - Advanced patterns and extensibility
- `examples/` - Code samples and runnable examples
- `assets/` - Images, diagrams, screenshots

## Building

These docs are built into a Docusaurus site in the `.website` repository.

See https://github.com/ZeroAlloc-Net/.website for build instructions.

## Contributing

When adding documentation:
1. Add markdown file to appropriate section
2. Update `sidebars.js` to include the page
3. Include code examples where appropriate
4. Add to `examples/` directory for code samples
```

**Step 3: Create directories**

Run: `mkdir -p docs/getting-started docs/core-concepts docs/usage-guides docs/testing docs/performance docs/advanced docs/examples docs/assets`

Expected: All directories created

**Step 4: Verify structure**

Run: `find docs -type d | head -20`

Expected: All 8 directories exist

**Step 5: Commit**

```bash
git add docs/sidebars.js docs/README.md
git commit -m "docs: set up documentation folder structure and Docusaurus config"
```

---

## Task 2: Write Getting Started - Installation Guide

**Files:**
- Create: `docs/getting-started/installation.md`

**Step 1: Write installation.md**

Create file: `docs/getting-started/installation.md`

```markdown
# Installation

Get ZeroAlloc.EventSourcing up and running in minutes.

## NuGet Package

Install the core package:

\`\`\`bash
dotnet add package ZeroAlloc.EventSourcing
\`\`\`

### Optional Packages

**For in-memory event store:**
\`\`\`bash
dotnet add package ZeroAlloc.EventSourcing.InMemory
\`\`\`

**For SQL Server or PostgreSQL:**
\`\`\`bash
dotnet add package ZeroAlloc.EventSourcing.Sql
\`\`\`

**For aggregate source generators:**
\`\`\`bash
dotnet add package ZeroAlloc.EventSourcing.Generators
\`\`\`

## Minimum Requirements

- **.NET 8+**
- **C# 12+**

## First Steps

1. Define your aggregate (see [Your First Aggregate](./first-aggregate.md))
2. Create an event store (in-memory or SQL)
3. Build your domain logic
4. Test with unit tests

## Project Structure

Recommended layout for event-sourced projects:

\`\`\`
MyProject/
├── Domain/
│   ├── Aggregates/
│   │   └── Order.cs
│   └── Events/
│       ├── OrderCreated.cs
│       ├── ItemAdded.cs
│       └── OrderShipped.cs
├── Infrastructure/
│   ├── EventStore/
│   │   └── PostgreSqlEventStore.cs
│   └── Projections/
│       └── OrderProjection.cs
└── Tests/
    ├── Domain/
    │   └── OrderAggregateTests.cs
    └── Infrastructure/
        └── EventStoreIntegrationTests.cs
\`\`\`

## Next Steps

- [Your First Aggregate](./first-aggregate.md) - Build your first aggregate in 5 minutes
- [Quick Start Example](./quick-start.md) - End-to-end working example
- [Core Concepts](../core-concepts/fundamentals.md) - Deep dive into event sourcing
```

**Step 2: Verify file created**

Run: `wc -l docs/getting-started/installation.md`

Expected: ~80 lines

**Step 3: Commit**

```bash
git add docs/getting-started/installation.md
git commit -m "docs: write Getting Started - Installation guide"
```

---

## Task 3: Write Getting Started - Your First Aggregate

**Files:**
- Create: `docs/getting-started/first-aggregate.md`

**Step 1: Write first-aggregate.md**

Create file: `docs/getting-started/first-aggregate.md`

```markdown
# Your First Aggregate

Build your first event-sourced aggregate in 5 minutes.

## What You'll Build

An `Order` aggregate that:
- Creates orders with items
- Tracks order status (pending, confirmed, shipped)
- Applies events to maintain state

## Step 1: Define Events

Events represent things that happened in your domain.

\`\`\`csharp
using ZeroAlloc.EventSourcing;

namespace MyProject.Domain.Events;

public record OrderCreated(OrderId OrderId, CustomerId CustomerId, decimal Amount)
{
    public static EventType EventType => new("order-created");
}

public record ItemAdded(string ItemId, decimal Price)
{
    public static EventType EventType => new("item-added");
}

public record OrderShipped
{
    public static EventType EventType => new("order-shipped");
}
\`\`\`

## Step 2: Define State

State is the current condition of your aggregate.

\`\`\`csharp
namespace MyProject.Domain;

public struct OrderState
{
    public OrderId OrderId { get; set; }
    public CustomerId CustomerId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Pending";
    public List<(string ItemId, decimal Price)> Items { get; set; } = [];
}
\`\`\`

## Step 3: Define Your Aggregate

Aggregates contain behavior and apply events.

\`\`\`csharp
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;

namespace MyProject.Domain.Aggregates;

public class Order : IAggregate<OrderState>
{
    private OrderState _state = new();
    
    public StreamId StreamId { get; private set; }
    public OrderState State => _state;
    public long Version { get; private set; }
    public IReadOnlyList<object> UncommittedEvents { get; } = new List<object>();

    // Factory method for new orders
    public static Order CreateOrder(OrderId id, CustomerId customerId, decimal amount)
    {
        var order = new Order();
        order.Raise(new OrderCreated(id, customerId, amount));
        return order;
    }

    // Add item to order
    public void AddItem(string itemId, decimal price)
    {
        Raise(new ItemAdded(itemId, price));
    }

    // Ship the order
    public void Ship()
    {
        if (_state.Status != "Confirmed")
            throw new InvalidOperationException("Can only ship confirmed orders");
        
        Raise(new OrderShipped());
    }

    // Apply events to state
    public void ApplyEvent(object @event)
    {
        switch (@event)
        {
            case OrderCreated oc:
                _state.OrderId = oc.OrderId;
                _state.CustomerId = oc.CustomerId;
                _state.Amount = oc.Amount;
                _state.Status = "Pending";
                break;
            
            case ItemAdded ia:
                _state.Items.Add((ia.ItemId, ia.Price));
                break;
            
            case OrderShipped:
                _state.Status = "Shipped";
                break;
        }
    }

    private void Raise(object @event)
    {
        ApplyEvent(@event);
        ((List<object>)UncommittedEvents).Add(@event);
        Version++;
    }
}
\`\`\`

## Step 4: Save and Load

Persist your aggregate with an event store.

\`\`\`csharp
// Save
var order = Order.CreateOrder(new OrderId(123), new CustomerId(456), 1000m);
order.AddItem("ITEM-001", 500m);

var eventStore = new InMemoryEventStore();
var streamId = new StreamId($"order-{order.State.OrderId.Value}");
await eventStore.AppendAsync(streamId, /* events */);

// Load
var loadedOrder = new Order();
await foreach (var @event in eventStore.ReadAsync(streamId))
{
    loadedOrder.ApplyEvent(@event);
}
\`\`\`

## Next Steps

- [Quick Start Example](./quick-start.md) - Full working example
- [Core Concepts: Aggregates](../core-concepts/aggregates.md) - Deeper dive
- [Usage Guide: Domain Modeling](../usage-guides/domain-modeling.md) - Advanced patterns
```

**Step 2: Verify file created**

Run: `wc -l docs/getting-started/first-aggregate.md`

Expected: ~150 lines

**Step 3: Commit**

```bash
git add docs/getting-started/first-aggregate.md
git commit -m "docs: write Getting Started - Your First Aggregate"
```

---

## Task 4: Write Getting Started - Quick Start Example

**Files:**
- Create: `docs/getting-started/quick-start.md`

**Step 1: Write quick-start.md**

Create file: `docs/getting-started/quick-start.md`

```markdown
# Quick Start Example

A complete, runnable example from start to finish.

## The Scenario

We'll build a simple order management system:
1. Create an order
2. Add items
3. Confirm the order
4. Ship it

## Complete Code

\`\`\`csharp
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Aggregates;

// Events
public record OrderCreated(int OrderId, decimal Amount);
public record OrderConfirmed;
public record OrderShipped;

// State
public struct OrderState
{
    public int OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Created";
}

// Aggregate
public class Order : IAggregate<OrderState>
{
    private OrderState _state = new();
    public OrderState State => _state;
    public StreamId StreamId { get; set; }
    public long Version { get; private set; }
    public IReadOnlyList<object> UncommittedEvents { get; } = new List<object>();

    public static Order Create(int id, decimal amount)
    {
        var order = new Order();
        order.Raise(new OrderCreated(id, amount));
        return order;
    }

    public void Confirm() => Raise(new OrderConfirmed());
    public void Ship() => Raise(new OrderShipped());

    public void ApplyEvent(object @event)
    {
        switch (@event)
        {
            case OrderCreated oc:
                _state = new OrderState { OrderId = oc.OrderId, Amount = oc.Amount };
                break;
            case OrderConfirmed:
                _state.Status = "Confirmed";
                break;
            case OrderShipped:
                _state.Status = "Shipped";
                break;
        }
    }

    private void Raise(object @event)
    {
        ApplyEvent(@event);
        ((List<object>)UncommittedEvents).Add(@event);
        Version++;
    }
}

// Main
var eventStore = new InMemoryEventStore();
var streamId = new StreamId("order-123");

// Create and persist
var order = Order.Create(123, 1000m);
order.Confirm();
order.Ship();

// In a real app, you'd append to event store
Console.WriteLine($"Order {order.State.OrderId}: {order.State.Status}");
// Output: Order 123: Shipped
\`\`\`

## Run This Example

1. Create a new console app: \`dotnet new console -n EventSourcingQuickStart\`
2. Add package: \`dotnet add package ZeroAlloc.EventSourcing.InMemory\`
3. Copy the code above into \`Program.cs\`
4. Run: \`dotnet run\`

## What Happened

1. **Created events** representing state changes
2. **Defined aggregate state** with OrderState struct
3. **Implemented aggregate** with command methods (Create, Confirm, Ship)
4. **Applied events** to build current state
5. **Tested** locally with in-memory event store

## Key Takeaways

- Events are immutable records of what happened
- Aggregates apply events to maintain state
- Event sourcing gives you an audit trail
- Start simple, add complexity as needed

## Next Steps

- [Installation](./installation.md) - Other package options
- [Core Concepts: Events](../core-concepts/events.md) - Deep dive
- [Testing](../testing/testing-aggregates.md) - Test your aggregates
```

**Step 2: Verify file created**

Run: `wc -l docs/getting-started/quick-start.md`

Expected: ~120 lines

**Step 3: Commit**

```bash
git add docs/getting-started/quick-start.md
git commit -m "docs: write Getting Started - Quick Start Example"
```

---

## Task 5: Write Core Concepts - Fundamentals

**Files:**
- Create: `docs/core-concepts/fundamentals.md`

**Step 1: Write fundamentals.md**

Create file: `docs/core-concepts/fundamentals.md`

```markdown
# Event Sourcing Fundamentals

Event sourcing is an architectural pattern where you model your application by storing a sequence of events.

## What Is Event Sourcing?

Instead of storing the current state in a database, event sourcing stores every change that happens as an immutable event.

### Traditional Approach

\`\`\`
User {
  id: 1,
  name: "Alice",
  email: "alice@example.com",
  status: "active"
}
\`\`\`

**Problem:** When you update a field, the old value is lost. You don't know *what changed* or *when*.

### Event Sourcing Approach

\`\`\`
Events:
1. UserCreated(id=1, name="Alice", email="alice@old.com")
2. EmailChanged(id=1, newEmail="alice@example.com")
3. StatusChanged(id=1, newStatus="active")
\`\`\`

**Benefits:**
- Complete audit trail
- Temporal queries ("what was the state on date X?")
- Event replay and rebuild
- Time-travel debugging

## Core Concepts

### Events

Events are immutable records of domain facts that have occurred.

- Represent past tense: "OrderCreated", "PaymentProcessed"
- Contain only data that changed
- Never updated or deleted
- Globally ordered

Example:
\`\`\`csharp
public record OrderCreated(OrderId OrderId, CustomerId CustomerId, decimal Amount);
\`\`\`

### Aggregates

Aggregates are domain objects that maintain consistency boundaries.

- Apply events to build their state
- Command methods trigger behavior
- Emit new events as a result
- Ensure domain invariants

Example:
\`\`\`csharp
public class Order : IAggregate<OrderState>
{
    public void CreateOrder(OrderId id, CustomerId customerId, decimal amount)
    {
        Raise(new OrderCreated(id, customerId, amount));
    }
}
\`\`\`

### Event Store

The event store is the single source of truth.

- Appends events (immutable)
- Reads events by stream
- Guarantees ordering
- Enables replay

### State Rebuilding

From events, you rebuild the current state by replaying.

\`\`\`
Start: empty state
Apply OrderCreated → state = {status: "created"}
Apply ItemAdded → state = {items: [item1]}
Apply OrderConfirmed → state = {status: "confirmed"}
End: current state
\`\`\`

## Why Event Sourcing?

### Auditability
Every change is recorded. You know exactly what happened and when.

### Scalability
Read models (projections) can scale independently from writes.

### Temporal Queries
"Show me all orders from last month" is trivial—you have all events.

### Recovery
Rebuild state from events if data corruption occurs.

### Testing
Aggregate behavior is easy to test: given events, verify new events.

## When to Use Event Sourcing

**Good fit:**
- Financial systems (audit trail required)
- Collaborative apps (need temporal queries)
- Complex domain logic
- High-scale reads (read models)

**Not ideal:**
- Simple CRUD apps
- Real-time analytics (high throughput)
- Unstructured data

## ZeroAlloc.EventSourcing

ZeroAlloc.EventSourcing provides:

- **IAggregate**: Interface for implementing aggregates
- **IEventStore**: Abstraction for event persistence
- **Generators**: Source generation for optimal performance
- **Zero allocations**: Memory-efficient by design
- **SQL support**: PostgreSQL, SQL Server adapters
- **Snapshots**: Optimize aggregate loading
- **Projections**: Build read models

## Next Steps

- [Events](./events.md) - Deep dive into events
- [Aggregates](./aggregates.md) - Aggregate patterns
- [Event Store](./event-store.md) - Storage and retrieval
```

**Step 2: Verify file created**

Run: `wc -l docs/core-concepts/fundamentals.md`

Expected: ~140 lines

**Step 3: Commit**

```bash
git add docs/core-concepts/fundamentals.md
git commit -m "docs: write Core Concepts - Fundamentals"
```

---

## Task 6: Remaining Documentation (Structured Overview)

Due to length constraints, the remaining tasks follow the same pattern:

**Task 6.1 - 6.6: Write remaining Core Concepts pages**
- `docs/core-concepts/events.md` (events deep-dive)
- `docs/core-concepts/aggregates.md` (aggregate patterns)
- `docs/core-concepts/event-store.md` (storage)
- `docs/core-concepts/snapshots.md` (optimization)
- `docs/core-concepts/projections.md` (read models)
- `docs/core-concepts/architecture.md` (design decisions)

**Pattern for each:**
1. Create markdown file with comprehensive content
2. Include code examples
3. Reference getting-started and other sections
4. Commit with `docs: write [Section] - [Topic]`

**Task 7.1 - 7.6: Write Usage Guides**
- `docs/usage-guides/domain-modeling.md`
- `docs/usage-guides/building-aggregates.md`
- `docs/usage-guides/replay-rebuilding.md`
- `docs/usage-guides/snapshots-usage.md`
- `docs/usage-guides/projections-usage.md`
- `docs/usage-guides/sql-adapters.md`

**Task 8.1 - 8.4: Write Testing**
- `docs/testing/testing-aggregates.md`
- `docs/testing/testing-events.md`
- `docs/testing/testing-projections.md`
- `docs/testing/integration-testing.md`

**Task 9.1 - 9.4: Write Performance & Benchmarks**
- `docs/performance/characteristics.md`
- `docs/performance/zero-allocation.md`
- `docs/performance/optimization.md`
- `docs/performance/benchmark-results.md` (pull from existing benchmark data)

**Task 10: Create API Reference Skeleton**
- Create `docs/api-reference.md`
- Note that this will be auto-generated from XML comments in code
- Create structure for linking to API docs

**Task 11.1 - 11.5: Write Advanced**
- `docs/advanced/custom-event-store.md`
- `docs/advanced/custom-snapshots.md`
- `docs/advanced/custom-projections.md`
- `docs/advanced/plugin-architecture.md`
- `docs/advanced/contributing.md`

---

## Task 12: Create Code Examples Directory

**Files:**
- Create: `docs/examples/01-getting-started/` (directory)
- Create: Example code files

**Step 1: Create structure**

```bash
mkdir -p docs/examples/01-getting-started
mkdir -p docs/examples/02-domain-modeling
mkdir -p docs/examples/03-testing
mkdir -p docs/examples/04-advanced
```

**Step 2: Create getting-started examples**

File: `docs/examples/01-getting-started/CreateFirstAggregate.cs`

```csharp
// Complete working example from Quick Start
// Copy from docs/getting-started/quick-start.md
```

**Step 3: Commit**

```bash
git add docs/examples/
git commit -m "docs: add code examples directory structure"
```

---

## Task 13: Set Up `.website` Repository Integration

**Files:**
- In `.website` repo: Create `apps/docs-eventsourcing/`
- Modify: `.website` repo structure

**Step 1: Coordinate with `.website` repo**

This task requires changes to the `.website` monorepo, which is external to EventSourcing.

**Prerequisites:**
- Access to ZeroAlloc-Net/.website repo
- Understanding of Docusaurus + Turbo setup in that repo
- Ability to add git submodule

**Steps:**
1. In `.website` repo: Add EventSourcing as git submodule: `git submodule add https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing repos/eventsourcing`
2. Create `apps/docs-eventsourcing/package.json` (copy from `apps/docs-mediator/`)
3. Create `apps/docs-eventsourcing/docusaurus.config.js` with EventSourcing config
4. Update `.website` root `turbo.json` to include new docs-eventsourcing app
5. Test: `pnpm run dev --filter=docs-eventsourcing`
6. Commit to `.website` repo

**Step 2: Update EventSourcing docs sidebars.js if needed**

Ensure `docs/sidebars.js` is correctly formatted for Docusaurus.

---

## Task 14: Set Up GitHub Actions Deployment

**Files:**
- Create: `.github/workflows/docs-deploy.yml` (in EventSourcing repo)
- Modify: `.website` repo workflows (coordinate)

**Step 1: Create docs-deploy.yml**

Create file: `.github/workflows/docs-deploy.yml`

```yaml
name: Documentation Deploy

on:
  push:
    branches:
      - main
    paths:
      - 'docs/**'
      - '.github/workflows/docs-deploy.yml'

jobs:
  trigger-website-build:
    runs-on: ubuntu-latest
    steps:
      - name: Trigger .website repo build
        run: |
          # This triggers a build in the .website repo when docs change
          # Coordinate with .website repo for webhook/dispatch setup
          echo "Docs updated, triggering .website build..."
```

**Step 2: Coordinate with `.website` repo**

Set up either:
- GitHub Actions workflow dispatch trigger
- Or webhook from EventSourcing to `.website`

This allows automatic rebuilds when docs are committed.

**Step 3: Commit**

```bash
git add .github/workflows/docs-deploy.yml
git commit -m "ci: add documentation deployment workflow"
```

---

## Task 15: Test Local Development Setup

**Files:**
- Test: Local Docusaurus setup

**Step 1: Clone both repos locally**

```bash
git clone https://github.com/ZeroAlloc-Net/.website
cd .website
git submodule update --init repos/eventsourcing
```

**Step 2: Install dependencies**

```bash
cd .website
pnpm install
```

**Step 3: Start dev server**

```bash
pnpm run dev --filter=docs-eventsourcing
```

Expected: Docusaurus dev server starts on localhost:3000

**Step 4: Verify all docs load**

- Click through all sections in sidebar
- Verify links work
- Check code examples display correctly
- Test search functionality

**Step 5: Test navigation**

- Breadcrumbs show correctly
- Prev/Next buttons work
- Table of contents jumps to sections

---

## Task 16: Final Review & Polish

**Step 1: Review all markdown files**

- Spelling and grammar check
- Link verification (all cross-references work)
- Code examples are valid C#
- Images and diagrams display

**Step 2: Test on mobile**

- Resize browser to mobile width
- Verify responsive layout
- Check all buttons are clickable

**Step 3: Verify deployment**

- Push to main branch
- Wait for GitHub Actions to complete
- Verify site deployed to `eventsourcing.zeroalloc.net`
- Test site on production URL

**Step 4: Final commit**

```bash
git add docs/
git commit -m "docs: documentation site complete and deployed"
```

---

## Summary

- **~80+ markdown pages** documenting all aspects of event sourcing
- **7 main sections** (Getting Started, Core Concepts, Usage, Testing, Performance, API, Advanced)
- **Code examples** throughout
- **Sequential learning path** for beginners and advanced users
- **Professional Docusaurus site** deployed to `eventsourcing.zeroalloc.net`
- **Integrated with `.website` monorepo** for consistency across ZeroAlloc ecosystem
- **Automated deployment pipeline** from EventSourcing commits

---

Plan complete and saved to `docs/plans/2026-04-04-documentation-website-implementation.md`.

**Two execution options:**

**1. Subagent-Driven (this session)** — I dispatch fresh subagent per task, review between tasks, fast iteration

**2. Parallel Session (separate)** — Open new session with executing-plans, batch execution with checkpoints

Which approach?