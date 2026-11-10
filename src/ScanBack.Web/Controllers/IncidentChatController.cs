using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Web.Services;

namespace ScanBack.Web.Controllers;

[AllowAnonymous]
public sealed class IncidentChatController(AppDbContext db, IncidentSession sessions) : Controller
{
    [HttpGet("/incident/{id:guid}/chat")]
    public async Task<IActionResult> Messages(Guid id, long after = 0)
    {
        var access = await sessions.GetAsync(HttpContext, id);
        if (access is null) return NotFound();
        if (after < 0) return BadRequest();
        // First load returns the latest 100; subsequent pages catch up in order.
        var query = db.IncidentChatMessages.AsNoTracking().Where(x => x.IncidentId == id);
        var messages = after == 0
            ? (await query.OrderByDescending(x => x.Id).Take(100).ToListAsync()).OrderBy(x => x.Id).ToList()
            : await query.Where(x => x.Id > after).OrderBy(x => x.Id).Take(100).ToListAsync();
        return Json(new { messages = messages.Select(x => Project(x, access.Id)), expiresAt = access.ExpiresAt });
    }

    [HttpPost("/incident/{id:guid}/chat")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("chat")]
    [RequestSizeLimit(16_384)]
    public async Task<IActionResult> Send(Guid id, ChatInput input)
    {
        if (!ModelState.IsValid || input.ClientMessageId == Guid.Empty || string.IsNullOrWhiteSpace(input.Body)) return BadRequest();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            // Serialize per-incident sends: retries remain idempotent and the per-session cap is durable.
            await db.Incidents.FromSqlInterpolated($"SELECT * FROM [Incidents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
                .AsNoTracking().Select(x => x.Id).SingleOrDefaultAsync();
            var access = await sessions.GetAsync(HttpContext, id);
            if (access is null) return NotFound();
            var message = await db.IncidentChatMessages.AsNoTracking().SingleOrDefaultAsync(x => x.IncidentId == id
                && x.SenderAccessId == access.Id && x.ClientMessageId == input.ClientMessageId);
            if (message is null)
            {
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
                if (await db.IncidentChatMessages.CountAsync(x => x.IncidentId == id && x.SenderAccessId == access.Id && x.SentAt > cutoff) >= 20)
                {
                    Response.Headers.RetryAfter = "60";
                    return StatusCode(StatusCodes.Status429TooManyRequests);
                }
                message = new IncidentChatMessage
                {
                    IncidentId = id, SenderAccessId = access.Id, SenderRole = access.Role,
                    ClientMessageId = input.ClientMessageId, Body = input.Body.Trim()
                };
                db.IncidentChatMessages.Add(message);
                await db.SaveChangesAsync();
            }
            await transaction.CommitAsync();
            return Json(new { message = Project(message, access.Id) });
        });
    }

    private static object Project(IncidentChatMessage message, Guid accessId) => new
    {
        message.Id, message.SenderRole, message.Body, message.SentAt, isMine = message.SenderAccessId == accessId
    };
}

public sealed class ChatInput
{
    public Guid ClientMessageId { get; set; }
    [Required, StringLength(1000)] public string Body { get; set; } = "";
}
