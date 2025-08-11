using System;

namespace BBT.Aether.AspNetCore.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class IgnoreAetherSecurityHeaderAttribute: Attribute
{
    
}