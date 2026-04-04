# Local Development Setup

This guide walks you through setting up the documentation for local development.

## System Requirements

### Minimum Requirements

| Requirement | Version | Download |
|-------------|---------|----------|
| Node.js | 20.0.0 or higher | https://nodejs.org/ |
| pnpm | 8.0.0 or higher | https://pnpm.io/ |
| Git | 2.34+ | https://git-scm.com/ |
| .NET SDK | 8.0 or higher | https://dotnet.microsoft.com/ |

### Recommended

- **Operating System:** Windows 10+, macOS 11+, or Ubuntu 20.04+
- **RAM:** 4GB minimum (8GB+ recommended)
- **Disk Space:** 2GB free
- **Terminal:** PowerShell 7+ (Windows), bash/zsh (macOS/Linux)

## Quick Start (5 minutes)

### 1. Clone the Repository

```bash
# Clone with git submodules
git clone --recursive https://github.com/ZeroAlloc-Net/ZeroAlloc.Website.git

cd ZeroAlloc.Website
```

### 2. Install Dependencies

```bash
# Install Node dependencies
pnpm install

# Verify installation
node --version    # Should be v20.0.0+
pnpm --version    # Should be 8.0.0+
```

### 3. Start Documentation Server

```bash
# Start the development server
pnpm run dev --filter=docs-eventsourcing

# Open in browser
# http://localhost:3000
```

## Detailed Setup

### Step 1: Verify Prerequisites

Check that all required tools are installed:

```bash
# Check Node.js version
node --version
# Expected: v20.x.x or higher

# Check pnpm version
pnpm --version
# Expected: 8.x.x or higher

# Check Git version
git --version
# Expected: 2.34+ or higher

# Check .NET SDK (optional, for code examples)
dotnet --version
# Expected: 8.0+ or higher
```

If any version is too old, update it:

```bash
# Update Node.js using your package manager
# Windows (via winget)
winget upgrade nodejs

# macOS (via brew)
brew upgrade node

# Update pnpm
npm install -g pnpm@latest
```

### Step 2: Clone Repository

```bash
# Option A: Clone with submodules (recommended)
git clone --recursive https://github.com/ZeroAlloc-Net/ZeroAlloc.Website.git
cd ZeroAlloc.Website

# Option B: Clone and init submodules separately
git clone https://github.com/ZeroAlloc-Net/ZeroAlloc.Website.git
cd ZeroAlloc.Website
git submodule update --init --recursive
```

Verify the EventSourcing repository is cloned:

```bash
ls -la repos/eventsourcing/docs
# Should show the docs directory contents
```

### Step 3: Install Dependencies

```bash
# From the .website root directory
pnpm install

# This installs:
# - Node packages in node_modules/
# - Workspace dependencies
# - TypeScript compiler
# - Docusaurus and plugins

# Wait for installation to complete (3-5 minutes)
```

Verify installation:

```bash
pnpm list | grep docusaurus
# Should show Docusaurus packages
```

### Step 4: Start Development Server

#### Using Turbo Filter (Recommended)

```bash
# Start only the EventSourcing docs app
pnpm run dev --filter=docs-eventsourcing

# Output should show:
# [docs-eventsourcing] Docusaurus website is running at: http://localhost:3000
```

#### Using Direct Script

```bash
# Change to the docs app directory
cd apps/docs-eventsourcing

# Start the server directly
pnpm start

# Output should show:
# [success] Docusaurus website is running at: http://localhost:3000
```

### Step 5: Open Documentation

Open your browser and navigate to:

```
http://localhost:3000
```

You should see:
- The EventSourcing documentation homepage
- Navigation sidebar with all sections
- Search functionality (top right)
- Dark/light mode toggle (top right)

## Development Workflow

### Making Changes

1. **Edit documentation files:**
   ```bash
   # Files are in the submodule
   repos/eventsourcing/docs/core-concepts/fundamentals.md
   ```

2. **Save and see hot reload:**
   - Changes are automatically detected
   - Browser refreshes automatically (usually < 1 second)
   - No need to restart the dev server

3. **Changes are live instantly**

### Building Locally

To test the production build:

```bash
# Build the static site
pnpm run build --filter=docs-eventsourcing

# Output is in: apps/docs-eventsourcing/build/

# Test the production build
pnpm run serve --filter=docs-eventsourcing

# This will run the static build locally
# Default port: http://localhost:3000
```

### Testing Links

To verify all links work correctly:

```bash
# Build must succeed first
pnpm run build --filter=docs-eventsourcing

# Check for broken links (if using markdown-link-check)
npm install -g markdown-link-check
markdown-link-check apps/docs-eventsourcing/build/**/*.html
```

## Common Tasks

### View Sidebar Configuration

The sidebar structure is defined in:

```bash
cat apps/docs-eventsourcing/sidebars.ts
```

To modify navigation:
1. Edit `apps/docs-eventsourcing/sidebars.ts`
2. Server hot-reloads automatically
3. Verify navigation in browser

### Search Documentation

The search functionality requires building:

```bash
# Search requires the full build process
pnpm run build --filter=docs-eventsourcing

# Search index is generated automatically
# Located at: apps/docs-eventsourcing/build/search-index.json
```

### Add New Documentation Page

1. Create new markdown file in `repos/eventsourcing/docs/`
2. Update `apps/docs-eventsourcing/sidebars.ts` if needed
3. Server detects the new file
4. Link appears in navigation

Example:

```bash
# Create new file
touch repos/eventsourcing/docs/core-concepts/new-topic.md

# Add frontmatter
cat > repos/eventsourcing/docs/core-concepts/new-topic.md << 'EOF'
---
id: new-topic
title: New Topic
---

# New Topic

Content goes here.
EOF
```

### Add Images/Assets

1. Place images in `repos/eventsourcing/assets/`
2. Reference from markdown:
   ```markdown
   ![Description](../assets/image.png)
   ```
3. Server automatically includes them

### Code Examples

Documentation includes .NET code examples:

```markdown
# Build Configuration

\`\`\`csharp
var builder = new EventStoreBuilder()
    .WithInMemoryStore()
    .WithSnapshots();

var eventStore = builder.Build();
\`\`\`
```

To verify code examples compile:

```bash
# Go to EventSourcing repository root
cd /c/Projects/Prive/ZeroAlloc.EventSourcing

# Build the solution
dotnet build

# This ensures examples are valid
```

## Troubleshooting

### Port 3000 Already in Use

**Problem:** `Error: EADDRINUSE: address already in use :::3000`

**Solutions:**

```bash
# Option 1: Use a different port
pnpm run start --filter=docs-eventsourcing -- --port 3001

# Option 2: Kill process using port 3000 (Linux/macOS)
kill -9 $(lsof -t -i :3000)

# Option 3: Kill process using port 3000 (Windows PowerShell)
netstat -ano | findstr :3000
taskkill /PID <PID> /F
```

### Submodule Not Found

**Problem:** `Error: repos/eventsourcing/docs: No such file or directory`

**Solution:**

```bash
# Initialize submodules
git submodule update --init --recursive

# Verify it's cloned
ls repos/eventsourcing/docs

# If still missing, manually update
cd repos/eventsourcing
git fetch
cd ..
git add repos/eventsourcing
git commit -m "chore: update submodule"
```

### Out of Memory

**Problem:** `JavaScript heap out of memory`

**Solution:**

```bash
# Increase Node.js memory limit
export NODE_OPTIONS="--max-old-space-size=4096"

# Then restart the server
pnpm run dev --filter=docs-eventsourcing
```

### Dependencies Not Installing

**Problem:** `ERR! code ERESOLVE or npm ERR! ERESOLVE unable to resolve dependency`

**Solution:**

```bash
# Clear pnpm cache
pnpm store prune

# Remove lock file and reinstall
rm pnpm-lock.yaml
pnpm install

# Or use legacy resolver
pnpm install --force
```

### Hot Reload Not Working

**Problem:** Browser doesn't refresh when files change

**Solution:**

```bash
# Restart the dev server
# Press Ctrl+C to stop
pnpm run dev --filter=docs-eventsourcing

# Clear browser cache (Ctrl+Shift+Delete)
# Hard refresh (Ctrl+Shift+R or Cmd+Shift+R)
```

### Changes Not Appearing

**Problem:** Edits to documentation don't show up

**Solutions:**

1. Verify you edited the right file:
   ```bash
   cat repos/eventsourcing/docs/path/to/file.md
   ```

2. Check for git status:
   ```bash
   cd repos/eventsourcing
   git status
   ```

3. Restart dev server:
   ```bash
   # Ctrl+C
   pnpm run dev --filter=docs-eventsourcing
   ```

## Performance Tips

### Faster Development

```bash
# Run only the EventSourcing docs (skip other apps)
pnpm run dev --filter=docs-eventsourcing

# This is faster than building everything:
pnpm run dev
```

### Reduce Build Time

```bash
# Clear Docusaurus cache
rm -rf apps/docs-eventsourcing/.docusaurus

# Then rebuild
pnpm run build --filter=docs-eventsourcing
```

### Clear Node Modules

If things get weird:

```bash
# Remove everything
rm -rf node_modules apps/*/node_modules pnpm-lock.yaml

# Reinstall
pnpm install
```

## Next Steps

1. **Explore the Documentation:**
   - Browse all sections in sidebar
   - Check that all links work
   - Test search functionality

2. **Make a Test Change:**
   - Edit `repos/eventsourcing/docs/README.md`
   - Verify hot reload works
   - Undo the change

3. **Run the Full Build:**
   - Execute `pnpm run build --filter=docs-eventsourcing`
   - Verify no errors
   - Check output size

4. **Review Contributing Guide:**
   - See `docs/advanced/contributing.md`
   - Learn documentation standards
   - Check code style guidelines

## IDE Setup

### VS Code

Recommended extensions:

1. **Markdown All in One**
   - ID: `yzhang.markdown-all-in-one`
   - Better markdown editing

2. **Markdownlint**
   - ID: `DavidAnson.vscode-markdownlint`
   - Lint markdown files

3. **Docusaurus**
   - ID: `Docusaurus.docusaurus`
   - Syntax highlighting

4. **Live Server** (optional)
   - ID: `ritwickdey.LiveServer`
   - Preview static builds

Settings for markdown:

```json
{
  "markdown.preview.breaks": true,
  "markdown.preview.linkExternal": true,
  "[markdown]": {
    "editor.defaultFormatter": "yzhang.markdown-all-in-one",
    "editor.formatOnSave": true
  }
}
```

### Visual Studio

Open the EventSourcing solution to edit code examples:

```bash
# From the EventSourcing repo root
start ZeroAlloc.EventSourcing.slnx
```

This allows you to:
- Verify code examples compile
- Test examples locally
- Keep examples up-to-date

## See Also

- [`WEBSITE_INTEGRATION.md`](./WEBSITE_INTEGRATION.md) - How docs integrate with .website monorepo
- [`DEPLOYMENT.md`](./DEPLOYMENT.md) - CI/CD pipeline and deployment process
- [Docusaurus Documentation](https://docusaurus.io/)
- [pnpm Workspaces](https://pnpm.io/workspaces)
- [Turbo Monorepo](https://turbo.build/repo)
