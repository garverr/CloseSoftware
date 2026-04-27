using System.Text.Json.Nodes;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Exports;

/// <summary>
/// Export endpoints: generate PDF/Excel artifacts, fetch metadata, download files, and QA runs.
/// Migrated from Program.cs inline registrations. Cat 29.
/// </summary>
public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/exports/pdf — generate a PDF export artifact for a report package.
        app.MapPost("/api/exports/pdf", async (
            ExportRequest request,
            HttpContext http,
            AppDbContext db,
            ExportService exports,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var artifact = await exports.CreatePdfAsync(request.ReportPackageId, request.IncludeIssues, request.IncludeAppendix, ct);
            db.ExportArtifacts.Add(artifact);
            await EndpointHelpers.AuditAsync(db, http, "export.pdf", "ExportArtifact", artifact.Id, request.ReportPackageId, "Generated PDF export", "{}", System.Text.Json.JsonSerializer.Serialize(artifact), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/exports/{artifact.Id}", ExportArtifactDto.From(artifact));
        });

        // POST /api/exports/excel — generate an Excel export artifact for a report package.
        app.MapPost("/api/exports/excel", async (
            ExportRequest request,
            HttpContext http,
            AppDbContext db,
            ExportService exports,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var artifact = await exports.CreateExcelAsync(request.ReportPackageId, request.IncludeIssues, request.IncludeAppendix, ct);
            db.ExportArtifacts.Add(artifact);
            await EndpointHelpers.AuditAsync(db, http, "export.excel", "ExportArtifact", artifact.Id, request.ReportPackageId, "Generated Excel export", "{}", System.Text.Json.JsonSerializer.Serialize(artifact), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/exports/{artifact.Id}", ExportArtifactDto.From(artifact));
        });

        // GET /api/exports/{exportId:guid} — fetch export artifact metadata.
        app.MapGet("/api/exports/{exportId:guid}", async (
            Guid exportId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var artifact = await db.ExportArtifacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == exportId, ct);
            return artifact is null ? Results.NotFound() : Results.Ok(ExportArtifactDto.From(artifact));
        });

        // GET /api/exports/{exportId:guid}/download — stream the export file to the client.
        app.MapGet("/api/exports/{exportId:guid}/download", async (
            Guid exportId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var artifact = await db.ExportArtifacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == exportId, ct);
            if (artifact is null || !System.IO.File.Exists(artifact.StoragePath))
            {
                return Results.NotFound();
            }

            return Results.File(artifact.StoragePath, artifact.ContentType, artifact.FileName);
        });

        // POST /api/exports/{exportId:guid}/qa — run QA checks against a completed export artifact.
        app.MapPost("/api/exports/{exportId:guid}/qa", async (
            Guid exportId,
            CreateExportQaRequest request,
            AppDbContext db,
            ExportService exports,
            CancellationToken ct) =>
        {
            var artifact = await db.ExportArtifacts.FirstOrDefaultAsync(x => x.Id == exportId, ct);
            if (artifact is null)
            {
                return Results.NotFound();
            }

            var qa = await exports.BuildExportQaAsync(request.ReportPackageId ?? artifact.ReportPackageId, exportId, ct);
            artifact.MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { artifact.MetadataJson, qa });
            await db.SaveChangesAsync(ct);
            return Results.Ok(JsonNode.Parse(qa));
        });

        return app;
    }
}
