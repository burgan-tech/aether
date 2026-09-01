# Current User

`ICurrentUser` is the ambient identity of the caller: who made the request, on whose behalf, with which
roles and position. It is resolved once per request from HTTP headers and stays available anywhere in the
call stack — application services, repositories, the audit interceptor — without threading a parameter
through every method.

## Properties

| Property | Header / claim | Notes |
|----------|----------------|-------|
| `Id` | `userId` | Internal user identifier |
| `UserName` | `sub` | Subject — the identity number |
| `Name` | `given_name` | |
| `Surname` | `family_name` | |
| `Roles` | `role` | Comma **or** space separated; parsed into an array |
| `Role` | `role` | Computed: the first entry of `Roles` |
| `Position` | `position` | Organizational posting of the caller; null when absent |
| `ActorUserId` | `act_uid` | Delegation — the acting user's id |
| `ActorUserName` | `act_sub` | Delegation — the acting user's subject |
| `ConsentId` | `consent_id` | |
| `IsAuthenticated` | — | True when `UserName` is not empty |

Header names come from `AetherClaimTypes` and are settable, so a host that speaks a different header
contract can rename them at startup:

```csharp
AetherClaimTypes.Position = "x-user-position";
```

### `Role` vs `Roles`

`Roles` is the full set. `Role` is the first entry — a convenience for legacy systems that carry a single
`role` claim, where the header holds exactly one value:

```csharp
// Legacy: role: maker
CurrentUser.Role;    // "maker"
CurrentUser.Roles;   // ["maker"]

// Modern: role: maker,checker
CurrentUser.Role;    // "maker"   ← only the first
CurrentUser.Roles;   // ["maker", "checker"]
CurrentUser.IsInRole("checker");  // true
```

Never make an authorization decision off `Role` when a caller may hold several roles — use `Roles` or
`IsInRole`.

## Reading the current user

`ICurrentUser` is registered by `AddAetherCore()` and exposed as a `CurrentUser` property on
`ApplicationService` and `AetherControllerBase`:

```csharp
public class OrderAppService : ApplicationService
{
    public async Task<OrderDto> ApproveAsync(Guid id)
    {
        if (!CurrentUser.IsInRole("checker"))
            throw new BusinessException("Approval requires the checker role.");

        _logger.LogInformation(
            "Order {Id} approved by {User} at {Position}",
            id, CurrentUser.UserName, CurrentUser.Position);
        // ...
    }
}
```

## Resolution pipeline

```
HTTP request headers
  → HeaderCurrentUserResolver (ICurrentUserResolver)
      → BasicUserInfo
          → AetherCurrentUserMiddleware  →  ICurrentUser.Change(basicUserInfo)
              → AsyncLocalCurrentUserAccessor  (ambient for the rest of the request)
```

`AddAetherAspNetCore()` registers `HeaderCurrentUserResolver` and the middleware. To resolve the user from
somewhere other than headers — a JWT already validated by the host, a gateway-specific envelope —
implement `ICurrentUserResolver` and replace the registration **after** calling `AddAetherAspNetCore()`:

```csharp
services.AddAetherAspNetCore();
services.Replace(ServiceDescriptor.Transient<ICurrentUserResolver, MyJwtCurrentUserResolver>());
```

`AddAetherAspNetCore()` registers `HeaderCurrentUserResolver` with a plain `AddTransient`, so registering
yours beforehand would leave both descriptors in place and Aether's — registered last — would win.

## Setting the user outside an HTTP request

Background jobs, message consumers and resumed workflows have no ambient request, so nothing populated
`ICurrentUser`. Capture the claim headers when the work is enqueued and restore them when it runs.

Capture (inside the request):

```csharp
var payload = new ReportJobPayload(
    ReportId: reportId,
    ClaimHeaders: httpContext.Request.GetCurrentUserHeaders());   // BBT.Aether.AspNetCore

await _backgroundJobService.EnqueueAsync(
    handlerName: "report-generator",
    jobName: $"report-{reportId}",
    payload: payload,
    schedule: DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));
```

Restore (inside the job):

```csharp
using (_currentUser.ChangeFromHeaders(payload.ClaimHeaders))   // BBT.Aether.Core
{
    // CurrentUser.UserName, .Role, .Position are the original caller's here
    await _reportService.GenerateAsync(payload.ReportId);
}
// previous user restored
```

`ChangeFromHeaders` is a no-op when `headers` is null or empty — the ambient user, if any, stays in place.
Both helpers live in `CurrentUserHeaderExtensions` and work over a plain
`IReadOnlyDictionary<string, string?>`, so they carry no ASP.NET dependency.

`Change` also has a `BasicUserInfo` overload; prefer it over the positional one so call sites survive new
fields being added to the user model:

```csharp
using (_currentUser.Change(new BasicUserInfo(
    id: "42", userName: "12345678901", roles: ["maker"], position: "branch-teller")))
{
    // ...
}
```

## Forwarding the user to another service

`ToForwardHeaders()` turns the current user back into claim headers, so a downstream service resolves the
same caller. Empty values are omitted and `Roles` is joined with commas:

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
foreach (var (key, value) in _currentUser.ToForwardHeaders())
{
    request.Headers.TryAddWithoutValidation(key, value);
}
```

The dictionary is round-trip compatible with `ChangeFromHeaders`, so the same pair works for both
in-process handoff and cross-service calls.

> **Trust boundary.** These headers are the identity contract *inside* your trust boundary — the gateway is
> what authenticates the token and stamps them. Never accept them straight off the public internet.

## Related

- [Telemetry](../telemetry/README.md) — enriching traces and logs with claim headers
- [Application Services](../application-services/README.md) — the `CurrentUser` property on service bases
