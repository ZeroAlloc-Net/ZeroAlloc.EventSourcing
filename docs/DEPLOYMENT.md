# Documentation Deployment Guide

This document describes the CI/CD pipeline for building and deploying the ZeroAlloc.EventSourcing documentation.

## Pipeline Overview

The documentation deployment follows an automated GitHub Actions workflow that triggers on every push to the main branch.

```
Push to main
    ↓
[GitHub Actions Workflow Triggered]
    ↓
├─ Lint & Validate Markdown
├─ Check Documentation Links
├─ Build Documentation (Docusaurus)
├─ Run Tests
└─ Deploy to Cloudflare Pages
    ↓
Live at eventsourcing.zeroalloc.net
```

## CI/CD Workflow Files

### 1. Deploy Workflow (`.github/workflows/deploy-docs.yml`)

This workflow runs on every push to the main branch and deploys the documentation.

**Triggers:**
- Push to `main` branch
- Manual trigger via GitHub Actions UI (workflow_dispatch)

**Steps:**
1. Checkout code with submodules
2. Install Node.js and pnpm
3. Install dependencies
4. Build documentation
5. Deploy to Cloudflare Pages

**Environment Variables Required:**
- `CLOUDFLARE_API_TOKEN` - Cloudflare API token for deployment
- `CLOUDFLARE_ACCOUNT_ID` - Cloudflare account ID
- `CLOUDFLARE_PROJECT_NAME` - Project name on Cloudflare Pages

**Workflow File:**

```yaml
name: Deploy Documentation

on:
  push:
    branches:
      - main
    paths:
      - 'docs/**'
      - '.github/workflows/deploy-docs.yml'
  workflow_dispatch:

jobs:
  deploy:
    runs-on: ubuntu-latest
    
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: 'pnpm'

      - name: Install pnpm
        uses: pnpm/action-setup@v2
        with:
          version: 9

      - name: Install dependencies
        run: pnpm install --frozen-lockfile

      - name: Build documentation
        run: pnpm run build --filter=docs-eventsourcing

      - name: Deploy to Cloudflare Pages
        uses: cloudflare/wrangler-action@v3
        with:
          apiToken: ${{ secrets.CLOUDFLARE_API_TOKEN }}
          accountId: ${{ secrets.CLOUDFLARE_ACCOUNT_ID }}
          secrets: |
            CLOUDFLARE_PROJECT_NAME
        env:
          CLOUDFLARE_PROJECT_NAME: ${{ secrets.CLOUDFLARE_PROJECT_NAME }}
```

### 2. Preview Workflow (`.github/workflows/docs-preview.yml`)

This workflow creates preview deployments for pull requests.

**Triggers:**
- Pull requests that modify documentation or workflow files
- Reopening PRs

**Steps:**
1. Build documentation for preview
2. Deploy to Cloudflare Pages with preview domain
3. Comment on PR with preview URL
4. Clean up preview on PR close

**Workflow File:**

```yaml
name: Documentation Preview

on:
  pull_request:
    paths:
      - 'docs/**'
      - '.github/workflows/docs-preview.yml'
    types: [opened, synchronize, reopened]

jobs:
  preview:
    runs-on: ubuntu-latest
    
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: 'pnpm'

      - name: Install pnpm
        uses: pnpm/action-setup@v2
        with:
          version: 9

      - name: Install dependencies
        run: pnpm install --frozen-lockfile

      - name: Build documentation
        run: pnpm run build --filter=docs-eventsourcing

      - name: Deploy preview
        uses: cloudflare/wrangler-action@v3
        with:
          apiToken: ${{ secrets.CLOUDFLARE_API_TOKEN }}
          accountId: ${{ secrets.CLOUDFLARE_ACCOUNT_ID }}
        id: deploy

      - name: Comment PR with preview URL
        uses: actions/github-script@v7
        with:
          script: |
            const deploymentUrl = steps.deploy.outputs.url;
            github.rest.issues.createComment({
              issue_number: context.issue.number,
              owner: context.repo.owner,
              repo: context.repo.repo,
              body: `📖 Documentation preview: [Preview](${deploymentUrl})`
            });
```

## Setup Instructions

### 1. GitHub Secrets Configuration

Add these secrets to your GitHub repository settings:

| Secret Name | Value | Description |
|-------------|-------|-------------|
| `CLOUDFLARE_API_TOKEN` | Your Cloudflare API token | Token for authenticating with Cloudflare |
| `CLOUDFLARE_ACCOUNT_ID` | Your account ID | Cloudflare account ID |
| `CLOUDFLARE_PROJECT_NAME` | Project name | Name of the project on Cloudflare Pages |

**To get these values:**

1. **Cloudflare API Token:**
   - Go to https://dash.cloudflare.com/profile/api-tokens
   - Create a new token with "Pages" permissions
   - Copy the token

2. **Cloudflare Account ID:**
   - Go to https://dash.cloudflare.com/
   - Account ID is shown in the URL or sidebar

3. **Project Name:**
   - The project name you create on Cloudflare Pages
   - e.g., "eventsourcing-docs"

### 2. Cloudflare Pages Setup

1. Go to https://pages.cloudflare.com/
2. Create a new project
3. Select "Connect to Git"
4. Authorize GitHub access
5. Select the ZeroAlloc.EventSourcing repository
6. Configure build settings:
   - **Build command:** `pnpm run build --filter=docs-eventsourcing`
   - **Build output directory:** `apps/docs-eventsourcing/build`
   - **Root directory:** (empty)
   - **Environment variables:**
     - `NODE_VERSION=20`
7. Save and deploy

### 3. Custom Domain Configuration

To use `eventsourcing.zeroalloc.net`:

1. In Cloudflare Pages project settings
2. Go to "Custom domains"
3. Add the domain `eventsourcing.zeroalloc.net`
4. Update DNS records if needed (or use Cloudflare's nameservers)

## Build Details

### Build Command

```bash
pnpm run build --filter=docs-eventsourcing
```

This command:
1. Changes to the `.website` directory
2. Runs Docusaurus build for the docs-eventsourcing app
3. Outputs static files to `apps/docs-eventsourcing/build/`

### Build Time

- **Typical build time:** 60-120 seconds
- **First build:** May take longer due to dependency installation
- **Incremental builds:** Faster when only content changes

### Build Artifacts

The build generates:

```
apps/docs-eventsourcing/build/
├── index.html
├── api-reference/
│   └── index.html
├── core-concepts/
│   └── (pages)
├── getting-started/
│   └── (pages)
├── advanced/
│   └── (pages)
├── assets/
│   ├── images/
│   ├── js/
│   └── css/
└── search-index.json
```

## Deployment Process

### Production Deployment

1. **Trigger:** Push to main branch
2. **Build:** Docusaurus builds static site
3. **Deploy:** Files uploaded to Cloudflare Pages
4. **Propagation:** CDN cache refreshed (usually < 30 seconds)
5. **Status:** Check GitHub Actions for deployment status

### Manual Deployment

To manually trigger a deployment:

1. Go to GitHub Actions
2. Select "Deploy Documentation" workflow
3. Click "Run workflow"
4. Select branch (usually main)
5. Wait for completion

### Rollback

If a deployment has issues:

1. Go to Cloudflare Pages project settings
2. View deployment history
3. Click "Rollback" on a previous working deployment
4. Or push a fix and redeploy

## Monitoring and Troubleshooting

### Check Build Status

```bash
# View recent deployments in GitHub Actions
# https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/actions

# Or in Cloudflare Pages
# https://pages.cloudflare.com/
```

### Common Issues

#### 1. Build Fails: "Cannot find module"

**Cause:** Missing dependencies or git submodule not initialized

**Solution:**
```bash
# Ensure submodules are initialized
git submodule update --init --recursive

# Check for pnpm-lock.yaml changes
git add pnpm-lock.yaml
git commit -m "chore: update dependencies"
git push
```

#### 2. Build Fails: "Documentation path not found"

**Cause:** The docs directory doesn't exist or path is incorrect

**Solution:**
- Verify docs exist in `repos/eventsourcing/docs/`
- Check path in `apps/docs-eventsourcing/docusaurus.config.ts`
- Verify submodule is cloned: `ls -la repos/eventsourcing/`

#### 3. Deployment Fails: "Invalid API token"

**Cause:** Cloudflare API token is invalid or expired

**Solution:**
1. Go to GitHub repository settings
2. Update `CLOUDFLARE_API_TOKEN` secret with new token
3. Trigger workflow again

#### 4. Build Succeeds but Site Shows Old Content

**Cause:** CDN cache not refreshed

**Solution:**
- Wait a few minutes (CDN usually refreshes within 30 seconds)
- Clear browser cache (Ctrl+Shift+Delete)
- Manually purge cache in Cloudflare dashboard

### View Build Logs

1. **GitHub Actions:**
   - Go to repository Actions tab
   - Click on failed workflow
   - Expand job steps to see detailed logs

2. **Cloudflare Pages:**
   - Go to project settings
   - View Deployments tab
   - Click on a deployment to see build logs

## Performance Considerations

### Build Optimization

- **Incremental builds:** Only rebuild changed content
- **Link validation:** Run locally before pushing
- **Image optimization:** Use compressed formats
- **CSS/JS minification:** Handled by Docusaurus automatically

### CDN Caching

- **Static assets:** Cached for 1 year (far-future expiry)
- **HTML files:** Cached for 5 minutes (allows quick updates)
- **API responses:** Not cached (if applicable)

### Monitoring Build Size

Check build size in Cloudflare Pages:
1. Go to project settings
2. View deployment details
3. Check "Total Upload Size"
4. Typical size: 5-15 MB

## Security

### API Token Rotation

- **Schedule:** Rotate tokens every 90 days
- **Process:**
  1. Create new token in Cloudflare
  2. Update GitHub secret
  3. Test with manual workflow run
  4. Delete old token

### Deployment Protection

The workflow uses:
- Protected branches (main requires PR reviews)
- Secrets management (tokens not exposed in logs)
- Environment variables for sensitive data
- Read-only API permissions where possible

## Continuous Improvement

### Monitoring Metrics

Track these metrics in GitHub Actions:
- **Build success rate:** Should be > 99%
- **Build duration:** Typically 60-120 seconds
- **Deployment frequency:** Multiple times per day (on-demand)

### Optimization Ideas

- Cache node_modules in GitHub Actions
- Use shallow git clones for faster checkouts
- Parallel builds for multiple doc apps
- Automated link checking in CI
- Scheduled content freshness checks

## See Also

- [`WEBSITE_INTEGRATION.md`](./WEBSITE_INTEGRATION.md) - How docs integrate with .website
- [`DEVELOPMENT.md`](./DEVELOPMENT.md) - Local development setup
- [Cloudflare Pages Documentation](https://developers.cloudflare.com/pages/)
- [GitHub Actions Documentation](https://docs.github.com/en/actions)
