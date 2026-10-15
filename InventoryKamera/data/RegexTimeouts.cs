using System;

namespace InventoryKamera
{
    /// <summary>
    /// Shared match timeout applied to every regular expression in the codebase
    /// (SonarCloud S6444 / ReDoS hardening). The timeout only fires on pathological
    /// backtracking (>2s on the short UI/OCR strings scanned here, which practically
    /// never happens), so scanner OCR and navigation behavior is unchanged.
    /// </summary>
    internal static class RegexTimeouts
    {
        internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    }
}
