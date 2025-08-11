using System;

namespace BBT.Aether.AspNetCore.Dapr;

public class DaprCheckSidecarException(string message, Exception innerException)
    : AetherException(message, innerException);