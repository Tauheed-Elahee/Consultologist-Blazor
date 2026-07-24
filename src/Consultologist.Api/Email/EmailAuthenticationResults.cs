using System.Text.RegularExpressions;

namespace Consultologist.Api.Email;

/// <summary>
/// The DKIM/SPF floor for email intake (#158, docs/ASYNC_DELIVERY.md §2):
/// evaluated from the Authentication-Results header that Exchange Online
/// stamps on delivery. Only the FIRST such header counts — it is the one our
/// own receiving hop wrote; any later ones came with the message and are
/// attacker-controllable.
/// </summary>
internal static partial class EmailAuthenticationResults
{
    internal sealed record Verdict(string? Dmarc, string? Spf, string? Dkim)
    {
        public bool Passes =>
            string.Equals(Dmarc, "pass", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(Spf, "pass", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Dkim, "pass", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\b(spf|dkim|dmarc)=([a-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MethodResultPattern();

    internal static Verdict Evaluate(IEnumerable<GraphInternetMessageHeader> headers)
    {
        var header = headers.FirstOrDefault(h =>
            string.Equals(h.Name, "Authentication-Results", StringComparison.OrdinalIgnoreCase));

        return header == null
            ? new Verdict(null, null, null)
            : Parse(header.Value);
    }

    internal static Verdict Parse(string headerValue)
    {
        string? dmarc = null, spf = null, dkim = null;

        foreach (Match match in MethodResultPattern().Matches(headerValue))
        {
            var method = match.Groups[1].Value.ToLowerInvariant();
            var result = match.Groups[2].Value.ToLowerInvariant();

            // First occurrence of each method wins — trailing property clauses
            // (e.g. header.d=..., smtp.mailfrom=...) never match the pattern.
            switch (method)
            {
                case "dmarc": dmarc ??= result; break;
                case "spf": spf ??= result; break;
                case "dkim": dkim ??= result; break;
            }
        }

        return new Verdict(dmarc, spf, dkim);
    }
}
