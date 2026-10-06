using System.Text.Json;
using JobAgent.Api.Data;
using JobAgent.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobAgent.Api.Services.Answers;

/// <summary>
/// Answer memory search and update. Reads the profile's stored answers, runs each page question through the
/// normalizer, matcher and safety validator, and keeps the memory current as the user answers or changes answers.
/// </summary>
public sealed class AnswerMemoryService(AppDbContext db)
{
    // A competing answer this close to the best match's score counts as a conflict (the user must choose).
    private const int ConflictWindow = 10;

    public async Task<ResolveResponse> ResolveAsync(Guid profileId, ResolveRequest request, CancellationToken ct)
    {
        var records = await db.AnswerMemories.AsNoTracking().Where(x => x.CandidateProfileId == profileId && x.Status == "Preferred").ToListAsync(ct);
        var profile = await db.CandidateProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == profileId, ct);
        var results = request.Questions.Select(q =>
        {
            var r = Resolve(q, records, DateTimeOffset.UtcNow);
            // Nothing in memory: the attached resume may still support an answer, always as a suggestion to confirm.
            if (r.Status != "ask" || r.Confidence != 0 || profile is null) return r;
            var intent = QuestionIntentClassifier.Classify(q.Text);
            if (intent.Policy == ReusePolicy.Never || intent.Intent is "eeo" or "legal_attestation" or "salary") return r;
            return ResumeAnswerSource.Suggest(q, intent, profile) is { } s
                ? r with { Status = "confirm", Confidence = 70, Answer = s.Answer, MatchedQuestion = "Your resume", Reason = s.Reason }
                : r;
        }).ToList();
        return new ResolveResponse(results);
    }

    /// <summary>Pure resolution of one question against the stored records (no database access).</summary>
    public static Resolution Resolve(QuestionInput q, IReadOnlyList<AnswerMemory> records, DateTimeOffset now)
    {
        var intent = QuestionIntentClassifier.Classify(q.Text);
        if (intent.Policy == ReusePolicy.Never) return new(q.Id, "skip", 0, null, null, null, intent.Intent, intent.Reason, []);

        var matches = AnswerMatcher.Match(q.Text, records).Where(m => m.Score >= 60).ToList();
        if (matches.Count == 0) return new(q.Id, "ask", 0, null, null, null, intent.Intent, "New question: no earlier answer to reuse.", []);

        var best = matches[0];
        var inverted = QuestionNormalizer.IsInverted(q.Text);
        var candidates = matches.GroupBy(m => AnswerMatcher.Canonical(m.Record)).Select(g => g.First()).Take(4)
            .Select(m => new Candidate(m.Record.Id, m.Record.OriginalQuestion, AnswerMatcher.AnswerFor(m.Record, inverted), m.Record.Status == "Preferred", m.Record.UpdatedAt, m.Record.TimesUsed))
            .ToList();

        // Earlier answers that disagree and match about equally well: never pick one for the user.
        var rivals = matches.Where(m => m.Score >= SafetyValidator.MediumConfidence && best.Score - m.Score <= ConflictWindow
                                        && AnswerMatcher.Canonical(m.Record) != AnswerMatcher.Canonical(best.Record)).ToList();
        if (rivals.Count > 0)
            return new(q.Id, "conflict", best.Score, null, best.Record.OriginalQuestion, best.Record.Id, intent.Intent,
                "Your earlier answers to this question disagree. Choose the one to keep.", candidates);

        var decision = SafetyValidator.Decide(q, intent, best, now);
        return new(q.Id, decision.Status, best.Score, decision.Answer, best.Record.OriginalQuestion, best.Record.Id, intent.Intent, decision.Reason, candidates);
    }

    public async Task RecordUseAsync(Guid profileId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        await db.AnswerMemories.Where(x => x.CandidateProfileId == profileId && list.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TimesUsed, x => x.TimesUsed + 1).SetProperty(x => x.LastUsedAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>Stores an answer the user gave. A changed answer replaces the old preferred one for the same question.</summary>
    public async Task<SaveResult> SaveAsync(Guid profileId, SaveRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Question) || string.IsNullOrWhiteSpace(req.Answer))
            return new(Guid.Empty, false, false, null, true, "Nothing to save.");
        var intent = QuestionIntentClassifier.Classify(req.Question);
        if (intent.Policy == ReusePolicy.Never) return new(Guid.Empty, false, false, null, true, intent.Reason);

        var normalized = QuestionNormalizer.Normalize(req.Question);
        var inverted = QuestionNormalizer.IsInverted(req.Question);
        var answer = req.Answer.Trim();
        var canonical = AnswerText.Norm(inverted && AnswerText.YesNo(answer) is not null ? AnswerText.Flip(answer) : answer);

        var same = await db.AnswerMemories.Where(x => x.CandidateProfileId == profileId && x.Status == "Preferred" &&
            (intent.Intent != "" ? x.Intent == intent.Intent && x.Subject == intent.Subject : x.NormalizedQuestion == normalized)).ToListAsync(ct);

        var disagreeing = same.Where(x => AnswerMatcher.Canonical(x) != canonical).ToList();
        var identical = same.FirstOrDefault(x => AnswerText.Norm(x.OriginalQuestion) == AnswerText.Norm(req.Question));
        var previous = disagreeing.FirstOrDefault()?.Answer;
        foreach (var old in disagreeing) { old.Status = "Superseded"; old.UpdatedAt = DateTimeOffset.UtcNow; }

        if (identical is not null && !disagreeing.Contains(identical))
        {
            identical.Answer = answer;
            identical.UpdatedAt = DateTimeOffset.UtcNow;
            identical.LastUsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return new(identical.Id, false, previous is not null, previous, false, previous is null ? "Already remembered." : "Updated: this is now your preferred answer.");
        }

        var record = new AnswerMemory
        {
            CandidateProfileId = profileId, OriginalQuestion = req.Question.Trim(), NormalizedQuestion = normalized,
            Intent = intent.Intent, Subject = intent.Subject, Inverted = inverted, Answer = answer,
            AnswerType = req.Type is { Length: > 0 } t ? t : AnswerText.YesNo(answer) is not null ? "yesno" : "text",
            OptionsJson = JsonSerializer.Serialize(req.Options ?? []),
            SourceCompany = req.Company ?? "", SourceUrl = req.SourceUrl ?? "", TimesUsed = 1,
        };
        db.AnswerMemories.Add(record);
        await db.SaveChangesAsync(ct);
        return new(record.Id, true, previous is not null, previous, false, previous is null ? "Saved." : "Updated: this is now your preferred answer.");
    }

    /// <summary>Makes one stored answer the preferred one (used to settle a conflict) and retires the others for that question.</summary>
    public async Task<bool> PreferAsync(Guid profileId, Guid id, CancellationToken ct)
    {
        var rec = await db.AnswerMemories.FirstOrDefaultAsync(x => x.Id == id && x.CandidateProfileId == profileId, ct);
        if (rec is null) return false;
        var others = await db.AnswerMemories.Where(x => x.CandidateProfileId == profileId && x.Id != id && x.Status == "Preferred" &&
            (rec.Intent != "" ? x.Intent == rec.Intent && x.Subject == rec.Subject : x.NormalizedQuestion == rec.NormalizedQuestion)).ToListAsync(ct);
        foreach (var other in others.Where(o => AnswerMatcher.Canonical(o) != AnswerMatcher.Canonical(rec))) other.Status = "Superseded";
        rec.Status = "Preferred";
        rec.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Final check before submitting: lists what came from memory, what still needs confirming, and what is unanswered.</summary>
    public async Task<ValidationReport> ValidateAsync(Guid profileId, ValidateRequest req, CancellationToken ct)
    {
        var records = await db.AnswerMemories.AsNoTracking().Where(x => x.CandidateProfileId == profileId && x.Status == "Preferred").ToListAsync(ct);
        var fromMemory = new List<ValidationLine>();
        var confirm = new List<ValidationLine>();
        var unanswered = new List<ValidationLine>();
        var conflicts = new List<ValidationLine>();
        var now = DateTimeOffset.UtcNow;

        foreach (var item in req.Items)
        {
            var q = new QuestionInput(item.Id, item.Text, item.Type, item.Options, null, item.Required);
            ValidationLine Line(string note) => new(item.Id, item.Text, item.Answer, note);
            var hasAnswer = !string.IsNullOrWhiteSpace(item.Answer);

            if (hasAnswer && item.Origin is "user" or "confirmed" or "existing") continue; // answered or approved by the user
            var r = Resolve(q, records, now);

            if (r.Status == "conflict") { conflicts.Add(Line(r.Reason)); continue; }
            if (!hasAnswer || item.Origin == "empty") { unanswered.Add(Line(item.Required ? "Required: needs your answer." : "Not answered.")); continue; }

            // Filled automatically: still has to be a high-confidence match for what is in the field.
            var stillAuto = item.Origin == "memory" && r.Status == "auto" && r.Confidence >= SafetyValidator.HighConfidence
                            && AnswerText.Norm(r.Answer) == AnswerText.Norm(item.Answer);
            if (stillAuto) fromMemory.Add(Line($"Matched {r.Confidence}%: {r.Reason}"));
            else confirm.Add(Line(r.Status == "auto" ? "The answer in the field no longer matches your memory." : r.Reason));
        }

        var requiredIds = req.Items.Where(i => i.Required).Select(i => i.Id).ToHashSet();
        var blocking = unanswered.Any(u => requiredIds.Contains(u.Id)) || confirm.Count > 0 || conflicts.Count > 0;
        return new ValidationReport(fromMemory, confirm, unanswered, conflicts, !blocking);
    }
}
