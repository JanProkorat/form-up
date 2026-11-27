# Playwright runner container for the dockerised QA harness.
#
# Built FROM mcr.microsoft.com/playwright (chromium + node already installed).
# The image bakes web/'s node_modules + the e2e test files in at build time;
# rebuild the image (or `scripts/test-env up --build`) to pick up spec edits.
# Bind-mounting the host source would have to fight the host/container
# node_modules architecture mismatch (esbuild + sharp binaries) — easier
# to rebuild than to bind-mount the right subset.
#
# Build context = repo root, so the COPY paths reach into web/ and
# qa-playwright/.
#
# Network behaviour: only this container reads the qa-net DNS for `web`
# and `api`. Specs target http://web:5173.

# Keep in step with @playwright/test in web/package.json (pinned exactly).
FROM mcr.microsoft.com/playwright:v1.61.1-jammy

WORKDIR /work/web

# named-flow-runner.sh parses flows.json with jq, which the playwright base
# image doesn't ship. Install it early (own layer) so spec edits don't bust
# this cache layer.
RUN apt-get update \
  && apt-get install -y --no-install-recommends jq \
  && rm -rf /var/lib/apt/lists/*

# Layer the web dependency closure first so spec edits don't bust the deps cache.
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund

# Test files + the configs they import.
COPY web/playwright.config.ts ./
COPY web/vite.config.e2e.ts ./
COPY web/tsconfig.json ./
COPY web/tsconfig.e2e.json ./
COPY web/tsconfig.app.json ./
COPY web/tsconfig.node.json ./
COPY web/tests ./tests
# The SPA src tree is needed because some tests import from @/lib/* etc.
COPY web/src ./src

# Runner + flow registry land in /work alongside the web tree.
COPY qa-playwright/named-flow-runner.sh /work/named-flow-runner.sh
COPY qa-playwright/flows.json /work/flows.json
RUN chmod +x /work/named-flow-runner.sh

# Default baseURL for the test browser context.
ENV PLAYWRIGHT_BASE_URL=http://web:5173
ENV PLAYWRIGHT_IN_CONTAINER=true
ENV E2E_API_URL=https://api:8080
ENV CI=true

# Keep the container idle so scripts/test-env run can `exec` into it.
ENTRYPOINT []
CMD ["sleep", "infinity"]
