using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BBT.Aether.AspNetCore.Security;

public static class AetherSecurityHeaderNonceHelper
{
    public static string GetScriptNonce(this IHtmlHelper htmlHelper)
    {
        if (htmlHelper.ViewContext.HttpContext.Items.TryGetValue(AetherAspNetCoreConsts.ScriptNonceKey, out var nonce) && nonce is string nonceString && !string.IsNullOrEmpty(nonceString))
        {
            return nonceString;
        }

        return string.Empty;
    }
    
    public static IHtmlContent GetScriptNonceAttribute(this IHtmlHelper htmlHelper)
    {
        var nonce = htmlHelper.GetScriptNonce();
        return nonce == string.Empty ? HtmlString.Empty : new HtmlString($"nonce=\"{nonce}\"");
    }
}