using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Services;

/// <summary>
/// The marquee differentiator: take the prior month's package as the baseline and emit
/// slide-level decisions — keep / modify / add / remove — filtered through a board-level
/// materiality threshold so a CFO sees only what changed materially. Cat 19, 20.
///
/// Flow:
///   1. Resolve the prior package (explicit FK or auto-detect by org + most recent prior period).
///   2. For each prior slide: see if a matching current slide exists (by AccountCodesCsv → Subject).
///      - Both present, value moved less than the board threshold → Keep (carry forward narrative).
///      - Both present, value moved beyond the board threshold → Modify.
///      - Prior present, no current match → Remove (gentle suggestion, CFO chooses).
///   3. For each current FluxReviewGroup not represented by any prior slide AND material at
///      the board threshold → Add.
/// </summary>
public sealed class PackageDiffService(AppDbContext db)
{
    public enum SlideDecisionKind
    {
        Keep,
        Modify,
        Add,
        Remove
    }

    public sealed record SlideDecision(
        SlideDecisionKind Kind,
        // When non-null, the slide on the prior package this decision references.
        Guid? PriorSlideId,
        // When non-null, the slide on the current package this decision references.
        Guid? CurrentSlideId,
        // When non-null, the flux group on the current package this decision references.
        Guid? CurrentFluxGroupId,
        string Subject,
        decimal CurrentValue,
        decimal PriorValue,
        decimal VarianceAmount,
        decimal VariancePercent,
        string Rationale);

    public sealed record DiffResult(
        Guid PackageId,
        Guid? PriorPackageId,
        decimal BoardDollarThreshold,
        decimal BoardPercentThreshold,
        IReadOnlyList<SlideDecision> Decisions);

    public async Task<DiffResult> ComputeAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages
            .AsNoTracking()
            .Include(x => x.Slides)
            .FirstAsync(x => x.Id == packageId, cancellationToken);

        var priorPackage = await ResolvePriorPackageAsync(package, cancellationToken);

        // Load the current month's flux groups so we can pair them with prior slides and
        // surface "new material item" Adds.
        var currentFlux = await db.FluxReviewGroups
            .AsNoTracking()
            .Where(x => x.ReportPackageId == packageId)
            .ToListAsync(cancellationToken);

        var decisions = new List<SlideDecision>();

        // Map of subject (case-insensitive) → current slide, for fast pairing with prior.
        var currentSlidesBySubject = package.Slides.ToDictionary(
            s => Normalize(s.Subject),
            s => s,
            StringComparer.OrdinalIgnoreCase);

        // Map of account-code → current slide too, so accounts that drove a slide last month
        // are matched even if the subject text drifted slightly.
        var currentSlidesByAccountCode = new Dictionary<string, PackageSlide>(StringComparer.OrdinalIgnoreCase);
        foreach (var slide in package.Slides)
        {
            foreach (var code in SplitAccountCodes(slide.AccountCodesCsv))
            {
                currentSlidesByAccountCode.TryAdd(code, slide);
            }
        }

        // Step 2 — walk prior slides.
        if (priorPackage is not null)
        {
            foreach (var priorSlide in priorPackage.Slides.OrderBy(s => s.SortOrder))
            {
                var match = LookupCurrent(priorSlide, currentSlidesBySubject, currentSlidesByAccountCode);
                if (match is null)
                {
                    decisions.Add(new SlideDecision(
                        SlideDecisionKind.Remove,
                        priorSlide.Id,
                        CurrentSlideId: null,
                        CurrentFluxGroupId: null,
                        priorSlide.Subject,
                        CurrentValue: 0m,
                        priorSlide.CurrentValue,
                        VarianceAmount: -priorSlide.CurrentValue,
                        VariancePercent: -100m,
                        Rationale: $"Prior month carried '{priorSlide.Subject}' but no current package data references it; consider removing or marking N/A."));
                    continue;
                }

                var variance = match.CurrentValue - priorSlide.CurrentValue;
                var variancePercent = priorSlide.CurrentValue == 0m
                    ? (match.CurrentValue == 0m ? 0m : 100m)
                    : Math.Round(variance / Math.Abs(priorSlide.CurrentValue) * 100m, 2);

                if (IsMaterial(variance, variancePercent, package.BoardDollarThreshold, package.BoardPercentThreshold))
                {
                    decisions.Add(new SlideDecision(
                        SlideDecisionKind.Modify,
                        priorSlide.Id,
                        match.Id,
                        CurrentFluxGroupId: null,
                        match.Subject,
                        match.CurrentValue,
                        priorSlide.CurrentValue,
                        variance,
                        variancePercent,
                        Rationale: $"'{match.Subject}' moved {FormatDelta(variance)} ({variancePercent:0.0}%) vs prior — exceeds board materiality."));
                }
                else
                {
                    decisions.Add(new SlideDecision(
                        SlideDecisionKind.Keep,
                        priorSlide.Id,
                        match.Id,
                        CurrentFluxGroupId: null,
                        match.Subject,
                        match.CurrentValue,
                        priorSlide.CurrentValue,
                        variance,
                        variancePercent,
                        Rationale: $"'{match.Subject}' moved {FormatDelta(variance)} ({variancePercent:0.0}%) — under board materiality; carry prior narrative forward."));
                }
            }
        }

        // Step 3 — Adds: material flux groups in the current package whose subject/account
        // is not covered by any prior slide.
        var priorSubjects = priorPackage?.Slides
            .Select(s => Normalize(s.Subject))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var priorCodes = priorPackage?.Slides
            .SelectMany(s => SplitAccountCodes(s.AccountCodesCsv))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var flux in currentFlux)
        {
            // Only flux groups that are board-material.
            if (!IsMaterial(flux.VarianceAmount, flux.VariancePercent, package.BoardDollarThreshold, package.BoardPercentThreshold))
            {
                continue;
            }

            // Skip if a prior slide already covers this subject or any of its account codes.
            if (priorSubjects.Contains(Normalize(flux.GroupName)))
            {
                continue;
            }
            // FluxReviewGroup doesn't carry account codes directly here, but GroupKey often
            // begins with an account code segment in this codebase. Best-effort check:
            if (!string.IsNullOrWhiteSpace(flux.GroupKey) && priorCodes.Contains(flux.GroupKey))
            {
                continue;
            }

            decisions.Add(new SlideDecision(
                SlideDecisionKind.Add,
                PriorSlideId: null,
                CurrentSlideId: null,
                CurrentFluxGroupId: flux.Id,
                flux.GroupName,
                flux.CurrentAmount,
                flux.PriorAmount,
                flux.VarianceAmount,
                flux.VariancePercent,
                Rationale: $"New material item: '{flux.GroupName}' moved {FormatDelta(flux.VarianceAmount)} ({flux.VariancePercent:0.0}%) — not present in prior package."));
        }

        return new DiffResult(
            packageId,
            priorPackage?.Id,
            package.BoardDollarThreshold,
            package.BoardPercentThreshold,
            decisions);
    }

    /// <summary>
    /// Resolve the prior package using the explicit FK if set; otherwise auto-detect the
    /// most recent prior package for the same organization (by ReportingPeriod.PeriodEnd).
    /// </summary>
    private async Task<ReportPackage?> ResolvePriorPackageAsync(ReportPackage package, CancellationToken cancellationToken)
    {
        if (package.PriorPackageId is { } explicitId)
        {
            return await db.ReportPackages
                .AsNoTracking()
                .Include(x => x.Slides)
                .FirstOrDefaultAsync(x => x.Id == explicitId, cancellationToken);
        }

        var currentPeriod = await db.ReportingPeriods
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == package.ReportingPeriodId, cancellationToken);
        if (currentPeriod is null)
        {
            return null;
        }

        return await db.ReportPackages
            .AsNoTracking()
            .Include(x => x.Slides)
            .Where(x => x.OrganizationId == package.OrganizationId
                        && x.Id != package.Id
                        && x.ReportingPeriod!.PeriodEnd < currentPeriod.PeriodEnd)
            .OrderByDescending(x => x.ReportingPeriod!.PeriodEnd)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static PackageSlide? LookupCurrent(
        PackageSlide priorSlide,
        Dictionary<string, PackageSlide> currentBySubject,
        Dictionary<string, PackageSlide> currentByAccountCode)
    {
        if (currentBySubject.TryGetValue(Normalize(priorSlide.Subject), out var bySubject))
        {
            return bySubject;
        }
        foreach (var code in SplitAccountCodes(priorSlide.AccountCodesCsv))
        {
            if (currentByAccountCode.TryGetValue(code, out var byCode))
            {
                return byCode;
            }
        }
        return null;
    }

    private static bool IsMaterial(decimal variance, decimal variancePercent, decimal dollarThreshold, decimal percentThreshold)
    {
        // AND logic: both legs must trip. This is the dual-threshold pattern Closecore /
        // Numeric default to — an item must be both numerically big AND a meaningful %
        // change before it earns a spot on the board package.
        var dollarHit = dollarThreshold > 0m && Math.Abs(variance) >= dollarThreshold;
        var percentHit = percentThreshold > 0m && Math.Abs(variancePercent) >= percentThreshold;
        return dollarHit && percentHit;
    }

    private static string Normalize(string s)
        => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToUpperInvariant();

    private static IEnumerable<string> SplitAccountCodes(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            yield break;
        }
        foreach (var code in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return code;
        }
    }

    private static string FormatDelta(decimal value)
        => value >= 0m ? $"+${value:N0}" : $"-${Math.Abs(value):N0}";
}
