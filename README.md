# JobPilot AI MVP — RAG Edition

JobPilot AI is a local-first job search and application preparation MVP. This edition adds a Retrieval-Augmented Generation (RAG) knowledge base so resume tailoring uses retrieved, user-supplied career evidence rather than relying only on a short profile summary.

## Stack
- .NET 10 / ASP.NET Core Minimal API
- SQL Server + Entity Framework Core
- OpenAI Responses API for resume tailoring
- OpenAI Embeddings API (`text-embedding-3-small`) for semantic retrieval
- SQL Server stores source documents, chunks and embedding vectors as JSON for this small MVP
- Keyword-overlap retrieval fallback when `OPENAI_API_KEY` is not configured

## Run locally
1. Extract the ZIP.
2. Check `src/JobPilot.Api/appsettings.json` and set the SQL Server connection string.
3. In PowerShell, set your key for the current terminal session:

   ```powershell
   $env:OPENAI_API_KEY="YOUR_OPENAI_API_KEY"
   ```

4. Start the API:

   ```powershell
   cd src/JobPilot.Api
   dotnet restore
   dotnet run
   ```

5. Open the local URL shown by ASP.NET Core. In Development, Swagger is available at `/swagger`.

The application uses `EnsureCreated` for the starter database and includes guarded SQL to add the two RAG tables to an earlier JobPilot MVP database. For production, replace this bootstrap with reviewed EF Core migrations.

## Add candidate knowledge
Use `POST /api/knowledge/ingest` in Swagger. Add text extracted from your current CV, project descriptions, achievements, certifications, tools, responsibilities and verified metrics. Do not upload confidential employer or client information unless you are authorised to do so.

Example request:

```json
{
  "title": "Verified CV and career achievements",
  "category": "CandidateEvidence",
  "source": "User-provided CV",
  "content": "Paste verified resume text and career facts here. Include employer, role, dates, project scope, tools, responsibilities and measurable outcomes."
}
```

The API chunks the text (900 characters with 120-character overlap), creates embeddings when the API key is available, and stores the chunks with their source metadata.

## Test retrieval
Use `POST /api/knowledge/search`:

```json
{
  "query": "Power BI dashboards, Primavera P6, SAP integration and EPC project controls",
  "topK": 5
}
```

`GET /api/knowledge` lists ingested documents.

## RAG in resume tailoring
When `POST /api/jobs/{jobId}/prepare` is called, JobPilot:
1. Builds a retrieval query from the job description and candidate target profile.
2. Retrieves the most relevant career evidence chunks.
3. Supplies the retrieved evidence to the model with strict instructions not to invent facts.
4. Requests evidence labels such as `[E1]` in the tailored resume.
5. Stores the tailored output in a Prepared application for individual review.

If there is no OpenAI key, the MVP uses keyword retrieval and a deterministic resume fallback that includes retrieved evidence. It does not perform semantic vector search in that mode.

## Approval safety
Each application must be explicitly approved for its current `ApprovalVersion`. The submission endpoint remains simulated and does not transmit applications to employer websites. The approval token is checked server-side. Any future edit to a prepared application must increment its version and clear approval.

## Important limitations before deployment
- This is a development MVP, not a production-secure public service. Add authentication/authorization, per-user data isolation, secret management, rate limiting, secure CORS, audit retention and encryption before hosting publicly.
- RAG vectors are stored as JSON and cosine-ranked in application memory. This is acceptable for a small personal knowledge base, not a large multi-user corpus. For scale, move to a vector database such as pgvector or Qdrant, or a supported SQL Server vector-search implementation.
- The current ingestion endpoint accepts text, not PDF/DOCX uploads. Add a document extraction pipeline before connecting file uploads.
- External job-board and company career portal connectors are not included yet. Use official APIs or permitted integrations and respect each site's terms. CAPTCHA/MFA must be completed by the user.
- No external application is submitted by this MVP. The user approval gate must remain mandatory when real portal adapters are added.

## Render deployment

See [README-RENDER.md](README-RENDER.md). This deployment uses Docker + Render PostgreSQL and a single-owner Basic Authentication gate. Do not use it for sensitive production data until the documented security and backup work is completed.
