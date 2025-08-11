using Microsoft.Extensions.Logging;

namespace BBT.Aether.HttpClient;

static internal class EventIdConstants
{
    readonly static internal EventId HttpServiceCallerEventId = new(32034, "HttpServiceCaller");
    readonly static internal EventId HttpServiceCallerFailureEventId = new(32035, "HttpServiceCallerFailure");
}