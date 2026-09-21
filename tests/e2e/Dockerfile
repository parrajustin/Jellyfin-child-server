# Playwright runner for the end-to-end suite. Built by docker-compose.yml (service "e2e").
# The browsers bundled in this image match @playwright/test 1.63.0 in package.json.
FROM mcr.microsoft.com/playwright:v1.63.0-noble

WORKDIR /e2e

COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY . .

ENV CI=1

CMD ["npx", "playwright", "test"]
