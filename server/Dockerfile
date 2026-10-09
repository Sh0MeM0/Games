FROM ghcr.io/astral-sh/uv:0.9.4 AS uv
FROM python:3.12-slim-bookworm
COPY --from=uv /uv /usr/local/bin/uv
WORKDIR /app
ENV UV_PROJECT_ENVIRONMENT=/app/.venv PATH="/app/.venv/bin:$PATH" PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
COPY pyproject.toml uv.lock ./
RUN --mount=type=secret,id=build_ca \
    if [ -s /run/secrets/build_ca ]; then SSL_CERT_FILE=/run/secrets/build_ca uv sync --frozen --no-dev; else uv sync --frozen --no-dev; fi \
    && useradd --uid 10001 --create-home archive && mkdir -p /data/files && chown -R archive:archive /data
COPY --chown=archive:archive app ./app
USER archive
EXPOSE 8000
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health',timeout=4)"
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--proxy-headers", "--forwarded-allow-ips", "*"]
