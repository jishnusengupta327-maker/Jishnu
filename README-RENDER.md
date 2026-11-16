# JobPilot AI — Render deployment (Docker + PostgreSQL)

This package adapts the existing .NET 10 MVP from SQL Server to PostgreSQL for Render.
It uses Render's `DATABASE_URL` (postgres://...) and converts it to an Npgsql connection string at startup.

## What this package does

- Builds the ASP.NET Core .NET 10 application with Docker.
- Listens on `0.0.0.0:10000`, Render's default web-service port.
- Uses PostgreSQL through `Npgsql.EntityFrameworkCore.PostgreSQL`.
- Bootstraps a new database with EF Core `EnsureCreated` (MVP only; not a migrations strategy).
- Protects the site, static dashboard and API with HTTP Basic Authentication.
- Leaves `/api/health` public for Render health checks.
- Keeps real employer submission disabled/simulated. Per-application approval remains required for the simulated submission path.

## Prerequisites

- GitHub account.
- Render account: https://dashboard.render.com/
- OpenAI API key.

## Step 1 — Upload source to GitHub

1. Extract this ZIP.
2. Create a **private** GitHub repository named `JobPilotAI`.
3. Upload the contents of this folder (the Dockerfile and render.yaml must be at repository root).
4. Commit to the `main` branch.
5. Do not commit API keys, passwords, `.env` files, CVs, or personal documents.

## Step 2 — Create PostgreSQL on Render

1. In Render Dashboard, choose **New + → PostgreSQL**.
2. Name it `jobpilot-db`.
3. Choose a region close to the web service (Singapore or Oregon; choose the same region for both).
4. Choose a paid plan for persistent production data. Render's Free Postgres is temporary and expires after 30 days; do not use it for irreplaceable CVs or application history.
5. Create the database.
6. Open its **Info** page and copy the **Internal Database URL**. Use the internal URL only for services in the same Render region.

## Step 3 — Create the web service

Option A (recommended):
1. In Render Dashboard choose **New + → Blueprint**.
2. Connect your private GitHub repository.
3. Select the repository and branch `main`.
4. Render reads `render.yaml` and asks for the four `sync: false` values.
5. Enter:
   - `OPENAI_API_KEY`: your OpenAI API key.
   - `DATABASE_URL`: paste the PostgreSQL **Internal Database URL** from Step 2.
   - `JOBPILOT_ACCESS_USER`: choose a username (e.g. `jishnu`).
   - `JOBPILOT_ACCESS_PASSWORD`: generate a unique strong password (at least 24 characters).
6. Confirm and deploy.

Option B: create a Web Service manually, select the private repo, choose Docker runtime, set Dockerfile path `/Dockerfile`, health check `/api/health`, and add the same four environment variables plus `ASPNETCORE_ENVIRONMENT=Production`.

## Step 4 — Confirm deployment

1. Wait for the first deploy to finish successfully in the Render Events/Logs tab.
2. Open the service URL, e.g. `https://jobpilot-ai.onrender.com` (actual URL may differ if name is taken).
3. The browser should prompt for Basic Authentication. Enter the credentials from Step 3.
4. Test `https://YOUR-SERVICE.onrender.com/api/health`; it should return JSON with `status: ok`.
5. The dashboard should load at `/` after authentication.

## Step 5 — Test RAG

Use an authenticated REST client (or browser developer tools) to POST JSON to `/api/knowledge/ingest`:

```json
{
  "title": "Verified CV evidence",
  "category": "CandidateEvidence",
  "source": "User uploaded CV",
  "content": "Paste only verified CV text and project achievements here."
}
```

Then POST to `/api/knowledge/search`:

```json
{ "query": "EPC project controls Primavera P6 Power BI", "topK": 5 }
```

## Important MVP limitations

- This is a single-owner MVP protected by shared HTTP Basic Authentication, not a multi-user identity system. Do not share the password.
- The API does not yet have user/tenant isolation, role-based access, file upload, malware scanning, production migrations, backups configured by this package, or rate limiting.
- `EnsureCreated` is only suitable for a fresh MVP database. Do not use it as a production schema migration strategy. Back up before any schema evolution; add EF Core migrations before changing models.
- The current RAG implementation stores embeddings as JSON in PostgreSQL and computes cosine similarity in application memory. It is suitable only for a small personal knowledge base, not large-scale vector search.
- Actual company portal submission is NOT implemented. The submit endpoint is simulated; no application is sent to an employer.
- Render Free web services spin down after inactivity and have ephemeral local filesystems. This app stores structured data in PostgreSQL, but no CV binary upload feature is included yet.
- A public URL is not the same as a public app: the shared login gate is required. `/api/health` is intentionally public for Render's health check.

## Troubleshooting

- Build fails: inspect Render deploy logs; confirm Dockerfile and render.yaml are in the repository root and .NET 10 SDK/runtime images are available.
- App exits at startup: confirm all four environment variables are set, especially `DATABASE_URL`.
- Database connection fails: use the database's Internal Database URL and place both services in the same Render region; confirm database is available.
- Browser keeps asking for login: check `JOBPILOT_ACCESS_USER` and `JOBPILOT_ACCESS_PASSWORD`; save and redeploy after changes.
- App sleeps: expected on Free web service; upgrade to a paid web service for always-on availability.
