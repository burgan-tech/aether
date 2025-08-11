using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.Security;

public class AetherSecurityHeadersOptions
{
    public bool UseContentSecurityPolicyHeader { get; set; }
    
    public bool UseContentSecurityPolicyScriptNonce { get; set; }
    
    public string? ContentSecurityPolicyValue { get; set; }

    public Dictionary<string, string> Headers { get; } = new();

    public List<Func<HttpContext, Task<bool>>> IgnoredScriptNonceSelectors { get; } = new();

    public List<string> IgnoredScriptNoncePaths { get; } = new();
}