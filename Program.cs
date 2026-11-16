using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using JobPilot.Api;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrWhiteSpace(databaseUrl))
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        connectionString = $"Host={uri.Host};Port={uri.Port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true";
    }
}
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Set DATABASE_URL or ConnectionStrings__Default in Render environment variables.");
builder.Services.AddDbContext<JobPilotDb>(o => o.UseNpgsql(connectionString));
builder.Services.AddHttpClient();
builder.Services.AddScoped<RagService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var app = builder.Build();
// Single-owner Basic Authentication gate for this private MVP deployment.
// Set JOBPILOT_ACCESS_USER and JOBPILOT_ACCESS_PASSWORD in Render before publishing.
var accessUser = Environment.GetEnvironmentVariable("JOBPILOT_ACCESS_USER");
var accessPassword = Environment.GetEnvironmentVariable("JOBPILOT_ACCESS_PASSWORD");
if (app.Environment.IsProduction() && (string.IsNullOrWhiteSpace(accessUser) || string.IsNullOrWhiteSpace(accessPassword)))
    throw new InvalidOperationException("Set JOBPILOT_ACCESS_USER and JOBPILOT_ACCESS_PASSWORD before running in Production.");
app.Use(async (context, next) =>
{
    if (string.Equals(context.Request.Path.Value, "/api/health", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    var header = context.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            var separator = decoded.IndexOf(':');
            if (separator >= 0)
            {
                var suppliedUser = decoded[..separator];
                var suppliedPassword = decoded[(separator + 1)..];
                var userOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(suppliedUser), Encoding.UTF8.GetBytes(accessUser ?? ""));
                var passwordOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(suppliedPassword), Encoding.UTF8.GetBytes(accessPassword ?? ""));
                if (userOk && passwordOk) { await next(); return; }
            }
        }
        catch (FormatException) { }
    }
    context.Response.Headers.WWWAuthenticate = "Basic realm=\"JobPilot AI\", charset=\"UTF-8\"";
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
});
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

// MVP database bootstrap. In production, use reviewed EF migrations and managed secrets.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<JobPilotDb>();
    await db.Database.EnsureCreatedAsync();
    // MVP bootstrap for a new PostgreSQL database. Use reviewed EF Core migrations for future schema changes.
    if (!await db.Jobs.AnyAsync())
    {
        db.Jobs.AddRange(
            new Job { Company = "Example EPC Group", Title = "PMO / Digital Transformation Manager", Location = "India", Description = "Lead PMO governance, project controls, digital transformation, ERP integration, portfolio reporting and stakeholder management across EPC capital projects.", Source = "Demo seed", Url = "https://example.com/careers", MinExperience = 4, MaxExperience = 8, SalaryMinLpa = 18, SalaryMaxLpa = 24 },
            new Job { Company = "Example Technology Consulting", Title = "Senior Consultant – Project Controls", Location = "Bengaluru / Hybrid", Description = "Manage project planning, Primavera P6, SAP integration, Power BI dashboards, risk management and executive reporting for infrastructure programmes.", Source = "Demo seed", Url = "https://example.com/jobs", MinExperience = 3, MaxExperience = 7, SalaryMinLpa = 16, SalaryMaxLpa = 22 }
        );
        await db.SaveChangesAsync();
    }
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", product = "JobPilot AI", mode = "MVP - external submission adapter is simulated" }));

app.MapGet("/api/profile", async (JobPilotDb db) => Results.Ok(await db.CandidateProfiles.OrderByDescending(x => x.Id).FirstOrDefaultAsync() ?? CandidateProfile.Default()));
app.MapPut("/api/profile", async (CandidateProfile input, JobPilotDb db) => {
    var p = await db.CandidateProfiles.OrderByDescending(x => x.Id).FirstOrDefaultAsync();
    if (p is null) { p = input; db.CandidateProfiles.Add(p); }
    else { p.FullName=input.FullName; p.Email=input.Email; p.Phone=input.Phone; p.Summary=input.Summary; p.Skills=input.Skills; p.ExperienceYears=input.ExperienceYears; p.CurrentCtcLpa=input.CurrentCtcLpa; p.ExpectedCtcMinLpa=input.ExpectedCtcMinLpa; p.ExpectedCtcMaxLpa=input.ExpectedCtcMaxLpa; p.PreferredLocations=input.PreferredLocations; p.TargetRoles=input.TargetRoles; p.DomainKeywords=input.DomainKeywords; }
    await db.SaveChangesAsync(); return Results.Ok(p);
});


// RAG: ingest verified candidate knowledge (CV text, project achievements, certifications, role history).
app.MapPost("/api/knowledge/ingest", async (KnowledgeIngestRequest request, JobPilotDb db, RagService rag) => {
    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { message = "Title and content are required." });
    var chunks = RagService.Chunk(request.Content, 900, 120);
    var document = new KnowledgeDocument { Title = request.Title.Trim(), Category = string.IsNullOrWhiteSpace(request.Category) ? "CandidateEvidence" : request.Category.Trim(), Source = string.IsNullOrWhiteSpace(request.Source) ? "User supplied" : request.Source.Trim(), CreatedAtUtc = DateTime.UtcNow };
    db.KnowledgeDocuments.Add(document);
    await db.SaveChangesAsync();
    foreach (var chunk in chunks) {
        var vector = await rag.EmbedAsync(chunk);
        db.KnowledgeChunks.Add(new KnowledgeChunk { DocumentId = document.Id, Content = chunk, EmbeddingJson = vector is null ? null : JsonSerializer.Serialize(vector), CreatedAtUtc = DateTime.UtcNow });
    }
    await db.SaveChangesAsync();
    return Results.Created($"/api/knowledge/{document.Id}", new { document.Id, document.Title, document.Category, chunksCreated = chunks.Count, embeddingMode = rag.EmbeddingMode });
});
app.MapGet("/api/knowledge", async (JobPilotDb db) => Results.Ok(await db.KnowledgeDocuments.OrderByDescending(x => x.CreatedAtUtc).Select(x => new { x.Id, x.Title, x.Category, x.Source, x.CreatedAtUtc, chunks = x.Chunks.Count }).ToListAsync()));
app.MapPost("/api/knowledge/search", async (KnowledgeSearchRequest request, JobPilotDb db, RagService rag) => {
    if (string.IsNullOrWhiteSpace(request.Query)) return Results.BadRequest(new { message = "Query is required." });
    var results = await rag.RetrieveAsync(db, request.Query, Math.Clamp(request.TopK, 1, 10));
    return Results.Ok(new { query = request.Query, mode = rag.EmbeddingMode, results });
});

app.MapGet("/api/jobs", async (JobPilotDb db) => Results.Ok(await db.Jobs.OrderByDescending(x => x.CreatedAtUtc).ToListAsync()));
app.MapPost("/api/jobs", async (Job input, JobPilotDb db) => { input.Id=0; input.CreatedAtUtc=DateTime.UtcNow; db.Jobs.Add(input); await db.SaveChangesAsync(); return Results.Created($"/api/jobs/{input.Id}", input); });

app.MapPost("/api/jobs/{jobId:int}/prepare", async Task<IResult> (int jobId, JobPilotDb db, IHttpClientFactory clients, IConfiguration config, RagService rag) => {
    var job = await db.Jobs.FindAsync(jobId); if (job is null) return Results.NotFound();
    var profile = await db.CandidateProfiles.OrderByDescending(x => x.Id).FirstOrDefaultAsync() ?? CandidateProfile.Default();
    var score = MatchScorer.Score(profile, job);
    var evidence = await rag.RetrieveAsync(db, $"{job.Title} {job.Description} {profile.TargetRoles} {profile.Skills}", 6);
    var tailored = await ResumeTailor.CreateAsync(profile, job, evidence, clients.CreateClient(), config);
    var application = new JobApplication { JobId=job.Id, MatchScore=score.Score, MatchExplanation=score.Explanation, TailoredResume=tailored, CoverLetter=ResumeTailor.CoverLetter(profile, job), Status="Prepared", CreatedAtUtc=DateTime.UtcNow, ApprovalVersion=1 };
    db.Applications.Add(application); await db.SaveChangesAsync();
    return Results.Created($"/api/applications/{application.Id}", application);
});

app.MapGet("/api/applications", async (JobPilotDb db) => await db.Applications.Include(x => x.Job).OrderByDescending(x => x.CreatedAtUtc).Select(x => new { x.Id, x.JobId, JobTitle=x.Job!.Title, Company=x.Job.Company, x.MatchScore, x.MatchExplanation, x.Status, x.CreatedAtUtc, x.ApprovedAtUtc, x.SubmittedAtUtc, x.SubmissionMode }).ToListAsync());
app.MapGet("/api/applications/{id:int}", async Task<IResult> (int id, JobPilotDb db) => { var a=await db.Applications.Include(x=>x.Job).FirstOrDefaultAsync(x=>x.Id==id); return a is null ? Results.NotFound() : Results.Ok(a); });

// Approval is bound to the current prepared version. Any future edit must increment ApprovalVersion and clear approval.
app.MapPost("/api/applications/{id:int}/approve", async Task<IResult> (int id, ApprovalRequest request, JobPilotDb db) => {
    var a=await db.Applications.FindAsync(id); if(a is null) return Results.NotFound();
    if(a.Status!="Prepared") return Results.BadRequest(new { message="Only prepared applications can be approved." });
    if(!request.Confirm || request.ApprovalVersion!=a.ApprovalVersion) return Results.BadRequest(new { message="Explicit approval for the current application version is required." });
    a.Status="Approved"; a.ApprovedAtUtc=DateTime.UtcNow; a.ApprovalToken=Guid.NewGuid().ToString("N");
    db.AuditEvents.Add(new AuditEvent { ApplicationId=id, Event="Approved", Details="User approved this exact application version.", CreatedAtUtc=DateTime.UtcNow });
    await db.SaveChangesAsync(); return Results.Ok(new { a.Id, a.Status, a.ApprovedAtUtc, a.ApprovalVersion });
});

// Safe MVP: no third-party application is submitted. This endpoint demonstrates the protected gate and records a simulated result.
app.MapPost("/api/applications/{id:int}/submit", async Task<IResult> (int id, SubmitRequest request, JobPilotDb db) => {
    var a=await db.Applications.FindAsync(id); if(a is null) return Results.NotFound();
    if(a.Status!="Approved" || string.IsNullOrWhiteSpace(a.ApprovalToken)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(request.ApprovalToken!=a.ApprovalToken) return Results.BadRequest(new { message="Approval token mismatch." });
    a.Status="SubmissionReady"; a.SubmissionMode="SIMULATED - connect an approved company portal adapter to enable real submission";
    db.AuditEvents.Add(new AuditEvent { ApplicationId=id, Event="SubmissionGatePassed", Details="Approval verified. No external application was transmitted in this MVP.", CreatedAtUtc=DateTime.UtcNow });
    await db.SaveChangesAsync(); return Results.Ok(new { a.Id, a.Status, a.SubmissionMode, message="Approval verified. External submission adapter is not configured; no application was sent." });
});

app.MapGet("/api/audit", async (JobPilotDb db) => Results.Ok(await db.AuditEvents.OrderByDescending(x=>x.CreatedAtUtc).Take(200).ToListAsync()));
app.MapGet("/api/dashboard", async (JobPilotDb db) => Results.Ok(new { jobs=await db.Jobs.CountAsync(), prepared=await db.Applications.CountAsync(x=>x.Status=="Prepared"), approved=await db.Applications.CountAsync(x=>x.Status=="Approved"), submitted=await db.Applications.CountAsync(x=>x.Status=="Submitted"), submissionReady=await db.Applications.CountAsync(x=>x.Status=="SubmissionReady") }));
app.Run();

namespace JobPilot.Api {
public sealed class JobPilotDb(DbContextOptions<JobPilotDb> options) : DbContext(options) {
    public DbSet<Job> Jobs => Set<Job>(); public DbSet<CandidateProfile> CandidateProfiles => Set<CandidateProfile>(); public DbSet<JobApplication> Applications => Set<JobApplication>(); public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>(); public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>(); public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    protected override void OnModelCreating(ModelBuilder b) { b.Entity<Job>().HasIndex(x=>new{x.Company,x.Title,x.Url}).IsUnique(false); b.Entity<JobApplication>().HasOne(x=>x.Job).WithMany().HasForeignKey(x=>x.JobId); b.Entity<AuditEvent>().HasOne<JobApplication>().WithMany().HasForeignKey(x=>x.ApplicationId); b.Entity<KnowledgeChunk>().HasOne(x=>x.Document).WithMany(x=>x.Chunks).HasForeignKey(x=>x.DocumentId).OnDelete(DeleteBehavior.Cascade); }
}
public sealed class Job { public int Id {get;set;} public string Company {get;set;}=""; public string Title {get;set;}=""; public string Location {get;set;}="India"; public string Description {get;set;}=""; public string Source {get;set;}="Manual"; public string Url {get;set;}=""; public int MinExperience {get;set;} public int MaxExperience {get;set;} public decimal SalaryMinLpa {get;set;} public decimal SalaryMaxLpa {get;set;} public DateTime CreatedAtUtc {get;set;}=DateTime.UtcNow; }
public sealed class CandidateProfile { public int Id {get;set;} public string FullName {get;set;}="Jishnu Sengupta"; public string Email {get;set;}=""; public string Phone {get;set;}=""; public string Summary {get;set;}="Project management professional experienced in PMO, EPC capital projects, project controls and digital transformation."; public string Skills {get;set;}="PMO, EPC, Project Controls, Primavera P6, MS Project, Power BI, SAP, ERP, EVM, SQL, Business Analysis, AI Automation"; public decimal ExperienceYears {get;set;}=4.5m; public decimal CurrentCtcLpa {get;set;}=9; public decimal ExpectedCtcMinLpa {get;set;}=18; public decimal ExpectedCtcMaxLpa {get;set;}=22; public string PreferredLocations {get;set;}="India - All locations"; public string TargetRoles {get;set;}="PMO, Digital Transformation, Project Controls, Technology Consulting, Portfolio Management"; public string DomainKeywords {get;set;}="Construction, EPC, Infrastructure, Capital Projects, Engineering";
 public static CandidateProfile Default()=>new(); }
public sealed class JobApplication { public int Id {get;set;} public int JobId {get;set;} public Job? Job {get;set;} public int MatchScore {get;set;} public string MatchExplanation {get;set;}=""; public string TailoredResume {get;set;}=""; public string CoverLetter {get;set;}=""; public string Status {get;set;}="Prepared"; public int ApprovalVersion {get;set;}=1; public string? ApprovalToken {get;set;} public DateTime CreatedAtUtc {get;set;} public DateTime? ApprovedAtUtc {get;set;} public DateTime? SubmittedAtUtc {get;set;} public string? SubmissionMode {get;set;} }
public sealed class AuditEvent { public int Id {get;set;} public int ApplicationId {get;set;} public string Event {get;set;}=""; public string Details {get;set;}=""; public DateTime CreatedAtUtc {get;set;} }
public sealed record ApprovalRequest(bool Confirm, int ApprovalVersion);
public sealed record SubmitRequest(string ApprovalToken);
public static class MatchScorer {
 public static (int Score,string Explanation) Score(CandidateProfile p,Job j) { var profile=(p.Skills+" "+p.TargetRoles+" "+p.DomainKeywords+" "+p.Summary).ToLowerInvariant(); var terms=new[]{"pmo","epc","construction","project controls","primavera","power bi","sap","erp","digital transformation","portfolio","capital projects","business analysis","risk","earned value","scheduling"}; var matched=terms.Where(t=>j.Description.ToLowerInvariant().Contains(t)||j.Title.ToLowerInvariant().Contains(t)).Where(t=>profile.Contains(t)).Distinct().ToList(); var skill=Math.Min(65,matched.Count*8); var exp=(p.ExperienceYears>=j.MinExperience && p.ExperienceYears<=j.MaxExperience+2)?20: p.ExperienceYears>=j.MinExperience-1?12:4; var salary=(j.SalaryMaxLpa==0 || j.SalaryMaxLpa>=p.ExpectedCtcMinLpa)?15:5; var score=Math.Min(99,skill+exp+salary); return (score,$"Matched keywords: {(matched.Count==0?"none detected":string.Join(", ",matched))}. Experience fit: {exp}/20. Salary range fit: {salary}/15. This is a screening estimate, not a hiring prediction."); }
}
public static class ResumeTailor {
 public static async Task<string> CreateAsync(CandidateProfile p,Job j,IReadOnlyList<RetrievedEvidence> evidence,HttpClient http,IConfiguration config) {
  var key=Environment.GetEnvironmentVariable("OPENAI_API_KEY");
  if(string.IsNullOrWhiteSpace(key)) return FallbackResume(p,j,evidence)+"\n\n[AI tailoring is using the local fallback. Set OPENAI_API_KEY to enable model-generated tailoring.]";
  var evidenceText = evidence.Count == 0 ? "No retrieved candidate evidence is available. Do not invent experience; clearly flag missing evidence." : string.Join("\n", evidence.Select((e,i)=>$"[E{i+1}] {e.Title} ({e.Category}): {e.Content}"));
  var prompt=$"You are a careful resume tailoring assistant. Use retrieved candidate evidence as the source of truth. Never invent employers, dates, metrics, certifications, skills or responsibilities. Only claim facts supported by the candidate profile or retrieved evidence. If evidence is missing, label it as a gap. Preserve truthful scope and seniority. Return ATS-friendly plain text and cite evidence with [E1] labels. Candidate profile: {JsonSerializer.Serialize(p)}. Job: {JsonSerializer.Serialize(j)}. Retrieved evidence:\n{evidenceText}";
  var payload=JsonSerializer.Serialize(new { model=config["OpenAI:Model"]??"gpt-4.1-mini", input=prompt });
  using var req=new HttpRequestMessage(HttpMethod.Post,"https://api.openai.com/v1/responses"); req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key); req.Content=new StringContent(payload,Encoding.UTF8,"application/json");
  try { using var res=await http.SendAsync(req); var body=await res.Content.ReadAsStringAsync(); res.EnsureSuccessStatusCode(); using var doc=JsonDocument.Parse(body); var sb=new StringBuilder(); if(doc.RootElement.TryGetProperty("output",out var output)) foreach(var item in output.EnumerateArray()) if(item.TryGetProperty("content",out var content)) foreach(var part in content.EnumerateArray()) if(part.TryGetProperty("text",out var text)) sb.AppendLine(text.GetString()); return string.IsNullOrWhiteSpace(sb.ToString())?FallbackResume(p,j,evidence):sb.ToString(); }
  catch { return FallbackResume(p,j,evidence)+"\n\n[AI service unavailable; local fallback used.]"; }
 }
 public static string FallbackResume(CandidateProfile p,Job j,IReadOnlyList<RetrievedEvidence> evidence) {
  var facts=evidence.Count==0 ? "No verified supporting evidence has been ingested yet." : string.Join("\n",evidence.Select((e,i)=>$"[E{i+1}] {e.Title}: {e.Content}"));
  return $"{p.FullName}\n{j.Title} | Target company: {j.Company}\n\nPROFESSIONAL SUMMARY\n{p.Summary}\n\nCORE SKILLS\n{p.Skills}\n\nRETRIEVED VERIFIED CAREER EVIDENCE\n{facts}\n\nNOTE: Only use claims supported by the candidate's source documents; review before external use.";
 }
 public static string CoverLetter(CandidateProfile p,Job j)=>$"Dear Hiring Team,\n\nI am interested in the {j.Title} opportunity at {j.Company}. My experience across {p.DomainKeywords}, supported by {p.Skills}, aligns with the role's focus on project delivery, governance and transformation.\n\nI would welcome the opportunity to discuss how my experience can contribute to your team.\n\nRegards,\n{p.FullName}";
}


public sealed class KnowledgeDocument {
 public int Id {get;set;} public string Title {get;set;}=""; public string Category {get;set;}="CandidateEvidence"; public string Source {get;set;}=""; public DateTime CreatedAtUtc {get;set;} public List<KnowledgeChunk> Chunks {get;set;}=new();
}
public sealed class KnowledgeChunk {
 public int Id {get;set;} public int DocumentId {get;set;} public KnowledgeDocument? Document {get;set;} public string Content {get;set;}=""; public string? EmbeddingJson {get;set;} public DateTime CreatedAtUtc {get;set;}
}
public sealed record KnowledgeIngestRequest(string Title,string Content,string? Category,string? Source);
public sealed record KnowledgeSearchRequest(string Query,int TopK=5);
public sealed record RetrievedEvidence(int ChunkId,string Title,string Category,string Content,double Score);

public sealed class RagService(IHttpClientFactory clients, IConfiguration config) {
 public string EmbeddingMode => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) ? "keyword-fallback (configure OPENAI_API_KEY for vector embeddings)" : (config["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small");
 public async Task<float[]?> EmbedAsync(string text) {
  var key=Environment.GetEnvironmentVariable("OPENAI_API_KEY"); if(string.IsNullOrWhiteSpace(key)) return null;
  try {
   var payload=JsonSerializer.Serialize(new { model=config["OpenAI:EmbeddingModel"]??"text-embedding-3-small", input=text });
   using var req=new HttpRequestMessage(HttpMethod.Post,"https://api.openai.com/v1/embeddings");
   req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key); req.Content=new StringContent(payload,Encoding.UTF8,"application/json");
   using var res=await clients.CreateClient().SendAsync(req); if(!res.IsSuccessStatusCode) return null;
   using var doc=JsonDocument.Parse(await res.Content.ReadAsStringAsync());
   return doc.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
  } catch { return null; }
 }
 public async Task<List<RetrievedEvidence>> RetrieveAsync(JobPilotDb db,string query,int topK) {
  var rows=await db.KnowledgeChunks.Include(x=>x.Document).ToListAsync();
  if(rows.Count==0) return new();
  var qv=await EmbedAsync(query); var qterms=Terms(query);
  return rows.Select(c=>{
    double score=0;
    if(qv is not null && !string.IsNullOrWhiteSpace(c.EmbeddingJson)) {
      try { var v=JsonSerializer.Deserialize<float[]>(c.EmbeddingJson); if(v is not null && v.Length==qv.Length) score=Cosine(qv,v); } catch {}
    } else {
      var terms=Terms(c.Content); score=(double)qterms.Intersect(terms).Count()/Math.Max(1,qterms.Count);
    }
    return new RetrievedEvidence(c.Id,c.Document?.Title??"Untitled",c.Document?.Category??"Evidence",c.Content,Math.Round(score,4));
  }).OrderByDescending(x=>x.Score).Take(topK).ToList();
 }
 public static List<string> Chunk(string text,int size,int overlap) {
  var clean=Regex.Replace(text.Trim(),@"\s+"," "); var result=new List<string>(); if(clean.Length==0)return result;
  for(int start=0;start<clean.Length;start+=size-overlap) { var len=Math.Min(size,clean.Length-start); result.Add(clean.Substring(start,len)); if(start+len>=clean.Length)break; }
  return result;
 }
 static HashSet<string> Terms(string s)=>Regex.Matches(s.ToLowerInvariant(),@"[a-z0-9+#.]{2,}").Cast<Match>().Select(m=>m.Value).Where(x=>!new[]{"the","and","for","with","from","that","this","are","you","your","our","will","have","has","was","were","into","across"}.Contains(x)).ToHashSet();
 static double Cosine(float[] a,float[] b) { double dot=0,aa=0,bb=0; for(int i=0;i<a.Length;i++){dot+=a[i]*b[i];aa+=a[i]*a[i];bb+=b[i]*b[i];} return aa==0||bb==0?0:dot/(Math.Sqrt(aa)*Math.Sqrt(bb)); }
}
}
