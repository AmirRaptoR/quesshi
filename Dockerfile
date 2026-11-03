# One image per release: the server, the built client and production dependencies on Bun.
FROM oven/bun:1.4.2 AS build
WORKDIR /app
COPY package.json bun.lock ./
RUN bun install --frozen-lockfile
COPY . .
RUN bun run build

FROM oven/bun:1.4.2-slim
WORKDIR /app
COPY package.json bun.lock ./
RUN bun install --frozen-lockfile --production
COPY server server
COPY features features
COPY scripts scripts
COPY --from=build /app/dist dist
ENV QUESSHI_DATA_DIR=/data PORT=3000
VOLUME /data
EXPOSE 3000
USER bun
HEALTHCHECK --interval=30s --timeout=5s CMD bun -e "fetch('http://127.0.0.1:3000/api/health').then(r => process.exit(r.ok ? 0 : 1)).catch(() => process.exit(1))"
CMD ["bun", "server/main.ts"]
