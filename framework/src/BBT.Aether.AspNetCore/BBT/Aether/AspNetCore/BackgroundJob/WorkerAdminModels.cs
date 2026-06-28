using System;
using System.Collections.Generic;

namespace BBT.Aether.AspNetCore.BackgroundJob;

/// <summary>Request body for <c>PUT /admin/workers/{workerName}/slots</c>.</summary>
public sealed record UpdateWorkerSlotsRequest(int DesiredSlotCount, string? Reason = null);

/// <summary>Response body for <c>PUT /admin/workers/{workerName}/slots</c>.</summary>
public sealed record UpdateWorkerSlotsResponse(
    string WorkerName,
    int PreviousDesiredSlotCount,
    int DesiredSlotCount);

/// <summary>Individual slot state returned by the status endpoint.</summary>
public sealed record WorkerSlotState(
    int SlotNo,
    string? OwnerId,
    DateTime? LockedUntil,
    bool IsEnabled);

/// <summary>Response body for <c>GET /admin/workers/{workerName}/status</c>.</summary>
public sealed record WorkerStatusResponse(
    string WorkerName,
    int DesiredSlotCount,
    int MinSlotCount,
    int MaxSlotCount,
    IReadOnlyList<WorkerSlotState> Slots);
