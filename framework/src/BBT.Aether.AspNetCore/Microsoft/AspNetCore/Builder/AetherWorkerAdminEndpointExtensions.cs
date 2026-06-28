using System;
using System.Linq;
using System.Threading;
using BBT.Aether.AspNetCore.BackgroundJob;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods that map the Aether worker slot Admin API onto an <see cref="IEndpointRouteBuilder"/>.
/// </summary>
public static class AetherWorkerAdminEndpointExtensions
{
    /// <summary>
    /// Maps two Admin API endpoints for runtime worker slot management:
    /// <list type="bullet">
    ///   <item><c>PUT  {prefix}/{workerName}/slots</c> — change <c>desired_slot_count</c></item>
    ///   <item><c>GET  {prefix}/{workerName}/status</c> — read current settings and slot state</item>
    /// </list>
    /// Authentication is not enforced by the framework — add your own authorization policy via
    /// <see cref="RouteHandlerBuilder.RequireAuthorization()"/> on the returned builder, or protect
    /// the entire prefix with a policy in your pipeline.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <param name="prefix">Route prefix (default: <c>/admin/workers</c>).</param>
    public static IEndpointRouteBuilder MapAetherWorkerAdminApi<TDbContext>(
        this IEndpointRouteBuilder app,
        string prefix = "/admin/workers")
        where TDbContext : DbContext, IHasEfCoreWorkerSettings, IHasEfCoreWorkerSlots
    {
        var group = app.MapGroup(prefix);

        // PUT /admin/workers/{workerName}/slots
        group.MapPut("{workerName}/slots", async (
            string workerName,
            UpdateWorkerSlotsRequest body,
            IWorkerSettingsStore settingsStore,
            CancellationToken ct) =>
        {
            var settings = await settingsStore.GetAsync(workerName, ct);
            if (settings == null)
                return Results.NotFound(new { error = $"Worker '{workerName}' not found in worker_settings." });

            var desired = body.DesiredSlotCount;

            if (desired < settings.MinSlotCount || desired > settings.MaxSlotCount)
                return Results.ValidationProblem(new System.Collections.Generic.Dictionary<string, string[]>
                {
                    ["desiredSlotCount"] =
                    [
                        $"Value must be between {settings.MinSlotCount} and {settings.MaxSlotCount}."
                    ]
                });

            var previous = settings.DesiredSlotCount;
            await settingsStore.UpdateDesiredSlotCountAsync(workerName, desired, updatedBy: null, ct);

            return Results.Ok(new UpdateWorkerSlotsResponse(workerName, previous, desired));
        });

        // GET /admin/workers/{workerName}/status
        group.MapGet("{workerName}/status", async (
            string workerName,
            IWorkerSettingsStore settingsStore,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            var settings = await settingsStore.GetAsync(workerName, ct);
            if (settings == null)
                return Results.NotFound(new { error = $"Worker '{workerName}' not found in worker_settings." });

            // Read slot state directly from EF Core to avoid provider dependency in this extension.
            var dbContextProvider = sp.GetRequiredService<IAetherDbContextProvider<TDbContext>>();
            var dbContext = await dbContextProvider.GetDbContextAsync(ct);

            var slots = await dbContext.WorkerSlots
                .Where(s => s.WorkerName == workerName)
                .OrderBy(s => s.SlotNo)
                .Select(s => new WorkerSlotState(s.SlotNo, s.OwnerId, s.LockedUntil, s.IsEnabled))
                .ToListAsync(ct);

            return Results.Ok(new WorkerStatusResponse(
                settings.WorkerName,
                settings.DesiredSlotCount,
                settings.MinSlotCount,
                settings.MaxSlotCount,
                slots));
        });

        return app;
    }
}
