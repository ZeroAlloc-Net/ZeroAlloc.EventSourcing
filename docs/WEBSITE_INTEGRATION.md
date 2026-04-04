# Website Integration

This document explains how ZeroAlloc.EventSourcing documentation integrates with the centralized `.website` monorepo.

## Architecture Overview

The documentation follows the **federated documentation pattern** used across all ZeroAlloc projects:

```
ZeroAlloc.Website (monorepo)
├── repos/
│   ├── eventsourcing/           (git submodule - this repo)
│   ├── analyzers/
│   ├── collections/
│   └── ... (other projects)
└── apps/
    ├── docs-eventsourcing/       (Docusaurus app)
    ├── docs-collections/
    ├── docs-analyzers/
    └── ... (other doc apps)
```

Each project is a git submodule in `repos/`, with a corresponding Docusaurus app in `apps/` that references the submodule's `/docs` directory.

## Current Setup

### Submodule Configuration

EventSourcing is registered as a git submodule in `.website/.gitmodules`:

```gitmodule
[submodule "repos/eventsourcing"]
    path = repos/eventsourcing
    url = https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing
```

### Docusaurus Application

The `apps/docs-eventsourcing/` directory contains the Docusaurus configuration and build setup:

```
apps/docs-eventsourcing/
├── docusaurus.config.ts        # Docusaurus configuration
├── sidebars.ts                 # Sidebar navigation
├── package.json                # Dependencies and scripts
├── tsconfig.json               # TypeScript configuration
├── wrangler.jsonc              # Cloudflare Workers config
├── src/                        # Theme customization
└── README.md
```

## Building Locally

### Prerequisites

- Node.js 20.0 or higher
- pnpm 8.0 or higher
- Git (for submodule management)

### Initial Setup

```bash
# Clone the .website monorepo
cd /c/Projects/Prive/ZeroAlloc.Website

# Initialize and update git submodules
git submodule update --init --recursive

# Install dependencies
pnpm install
```

### Development Server

To run the EventSourcing documentation locally:

```bash
# From the .website root directory
pnpm run dev --filter=docs-eventsourcing

# The docs will be available at:
# http://localhost:3000
```

You can also use the Turbo `--filter` syntax to run only this app:

```bash
pnpm run start --filter=docs-eventsourcing
```

### Building for Production

```bash
# Build the static site
pnpm run build --filter=docs-eventsourcing

# Output is in: apps/docs-eventsourcing/build/
```

## Git Submodule Management

### Updating the Submodule

When the EventSourcing repository updates:

```bash
cd /c/Projects/Prive/ZeroAlloc.Website

# Update to the latest version
git submodule update --remote repos/eventsourcing

# Or manually update to a specific commit
cd repos/eventsourcing
git fetch
git checkout <commit-hash>
cd ..
git add repos/eventsourcing
git commit -m "chore: update eventsourcing submodule to <commit-hash>"
```

### Cloning with Submodules

When cloning `.website` for the first time:

```bash
git clone --recursive https://github.com/ZeroAlloc-Net/ZeroAlloc.Website.git
```

Or if already cloned:

```bash
git submodule update --init --recursive
```

## File Structure

The Docusaurus app uses a specific structure to locate docs:

```
repos/eventsourcing/          (submodule)
└── docs/
    ├── README.md              # Home page
    ├── INDEX.md               # Nav index
    ├── api-reference.md
    ├── ADOPTION_GUIDE.md
    ├── performance.md
    ├── sidebars.js            # (Docusaurus can auto-read)
    ├── core-concepts/
    │   ├── fundamentals.md
    │   ├── events.md
    │   ├── aggregates.md
    │   ├── projections.md
    │   ├── snapshots.md
    │   ├── event-store.md
    │   └── architecture.md
    ├── getting-started/
    │   ├── installation.md
    │   ├── quickstart.md
    │   ├── basic-setup.md
    │   ├── first-event.md
    │   └── deployment.md
    ├── usage-guides/
    │   └── (various guides)
    ├── advanced/
    │   ├── custom-event-store.md
    │   ├── custom-projections.md
    │   ├── custom-snapshots.md
    │   ├── plugin-architecture.md
    │   └── contributing.md
    ├── examples/
    │   └── (code examples)
    ├── performance/
    │   └── (performance guides)
    ├── testing/
    │   └── (testing guides)
    └── assets/
        └── (images, diagrams)
```

## Docusaurus Configuration

The `docusaurus.config.ts` in `apps/docs-eventsourcing/`:

```typescript
const config: Config = {
  title: 'ZeroAlloc.EventSourcing',
  tagline: 'High-performance, zero-allocation event sourcing for .NET',
  url: 'https://eventsourcing.zeroalloc.net',
  baseUrl: '/',
  staticDirectories: ['static', '../../repos/eventsourcing/assets'],
  presets: [
    [
      'classic',
      {
        docs: {
          routeBasePath: '/',
          path: '../../repos/eventsourcing/docs',
          sidebarPath: './sidebars.ts',
          exclude: ['**/README.md', '**/pre-push-review*.md', '**/plans/**'],
        },
        blog: false,
      },
    ],
  ],
};
```

Key points:
- `path: '../../repos/eventsourcing/docs'` - Points to the submodule's docs
- `staticDirectories` - Includes assets from the submodule
- `exclude` - Filters out non-documentation files
- `routeBasePath: '/'` - Root URL for the docs

## Deployment

### Cloudflare Pages

The documentation is deployed to Cloudflare Pages:

1. **Build Command**: `pnpm run build --filter=docs-eventsourcing`
2. **Build Output**: `apps/docs-eventsourcing/build`
3. **Domain**: `eventsourcing.zeroalloc.net`

The deployment is automated via GitHub Actions (see `DEPLOYMENT.md`).

### Static Site Structure

The built site includes:

```
build/
├── index.html
├── api-reference/
├── core-concepts/
├── getting-started/
├── usage-guides/
├── advanced/
├── examples/
├── performance/
├── testing/
└── assets/
```

## Troubleshooting

### Submodule Not Found

If you get errors about the eventsourcing submodule not existing:

```bash
# Ensure the submodule is initialized
git submodule update --init repos/eventsourcing

# Check the status
git submodule status
```

### Docs Not Building

If the build fails looking for the eventsourcing docs:

1. Verify the submodule is cloned: `ls repos/eventsourcing/docs`
2. Verify the path in `docusaurus.config.ts` is correct
3. Check that documentation files exist in the submodule

### Port Already in Use

If port 3000 is already in use:

```bash
# Specify a different port
pnpm run start --filter=docs-eventsourcing -- --port 3001
```

### Breaking Changes in Documentation

If documentation structure changes (new directories, renamed files), update `sidebars.ts` in `apps/docs-eventsourcing/` to reflect the new structure.

## Contributing Documentation

To contribute to the EventSourcing documentation:

1. Edit files in `/c/Projects/Prive/ZeroAlloc.EventSourcing/docs/`
2. Commit and push to the main EventSourcing repository
3. Update the submodule in `.website`:
   ```bash
   cd /c/Projects/Prive/ZeroAlloc.Website
   git submodule update --remote repos/eventsourcing
   git commit -am "chore: update eventsourcing docs"
   ```
4. The `.website` CI/CD will rebuild and deploy the updated docs

## See Also

- [`DEPLOYMENT.md`](./DEPLOYMENT.md) - CI/CD pipeline and deployment process
- [`DEVELOPMENT.md`](./DEVELOPMENT.md) - Local development setup guide
- [ZeroAlloc.Website Repository](https://github.com/ZeroAlloc-Net/ZeroAlloc.Website)
