FROM node:24-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN --mount=type=secret,id=trusted_ca \
    if [ -f /run/secrets/trusted_ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/trusted_ca; fi; \
    npm ci --no-audit --no-fund
COPY index.html tsconfig.json vite.config.ts ./
COPY public ./public
COPY src ./src
RUN npm run build

FROM node:24-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production PORT=3001 POTOK_DB_PATH=/app/data/potok.sqlite
COPY --chown=node:node package*.json ./
RUN --mount=type=secret,id=trusted_ca \
    if [ -f /run/secrets/trusted_ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/trusted_ca; fi; \
    npm ci --omit=dev --no-audit --no-fund && mkdir -p /app/data && chown node:node /app/data
COPY --from=build --chown=node:node /app/dist ./dist
COPY --chown=node:node server ./server
USER node
EXPOSE 3001
CMD ["node", "server/index.js"]
