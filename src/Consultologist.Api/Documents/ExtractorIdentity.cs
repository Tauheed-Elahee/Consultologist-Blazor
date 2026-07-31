using System.Reflection;

namespace Consultologist.Api.Documents;

/// <summary>
/// Names the library that read a document, in a form someone can read
/// (#253, docs/DOCUMENT_INPUT.md § 7).
///
/// Shared rather than repeated in each extractor: both used to take
/// <see cref="AssemblyInformationalVersionAttribute"/> verbatim, which was
/// tolerable for PdfPig — <c>0.1.15+f131f642…</c>, version plus one commit
/// — and not for DocumentFormat.OpenXml, which publishes GitVersion's full
/// spread as <c>3.5.1+Branch.main.Sha.&lt;sha&gt;.&lt;sha&gt;</c>. That
/// landed the commit twice, behind a branch name, in the one field whose
/// purpose is that a clinician or reviewer can read what produced a
/// consult. A third format would have inherited the same problem.
///
/// The commit survives on purpose. § 7 records that a pinned pre-1.0
/// library can change its extracted output across versions, which makes
/// extractor identity provenance-affecting: <c>openxml/3.5.1</c> alone
/// would not distinguish two builds that read the same file differently.
/// Precision is the argument for keeping a commit, not for keeping it
/// twice.
/// </summary>
internal static class ExtractorIdentity
{
    /// <summary>
    /// Enough of a commit to identify it, short enough to read beside a
    /// version.
    /// </summary>
    private const int ShortCommitLength = 8;

    /// <summary>
    /// How long a hex token must be before it is believed to be a commit.
    ///
    /// The floor is the load-bearing part of the rule, not a detail. Build
    /// metadata is dot-separated and mostly *not* commits — OpenXml's is
    /// largely a branch name — so a short floor would read a branch called
    /// <c>deadbee</c> as a commit and record it as provenance. A SHA-1 is
    /// 40 characters and a SHA-256 is 64, so both packages in use clear
    /// this comfortably and nothing shorter can be mistaken for one.
    /// </summary>
    private const int MinimumCommitLength = 32;

    internal static string For(string name, Assembly assembly) =>
        Format(
            name,
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);

    /// <summary>
    /// The rule, separated from the reflection so it can be tested against
    /// real version strings without fabricating assemblies.
    /// </summary>
    internal static string Format(string name, string? informationalVersion, Version? assemblyVersion)
    {
        var raw = string.IsNullOrWhiteSpace(informationalVersion)
            ? assemblyVersion?.ToString()
            : informationalVersion;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return $"{name}/unknown";
        }

        var separator = raw.IndexOf('+');

        if (separator < 0)
        {
            return $"{name}/{raw}";
        }

        var version = raw[..separator];
        var commit = ShortCommit(raw[(separator + 1)..]);

        // No recoverable commit means the package published none, which is
        // worth saying plainly rather than padding with the metadata that
        // was there. The version is pinned exactly, so it still identifies
        // the build.
        return commit == null
            ? $"{name}/{version}"
            : $"{name}/{version}+{commit}";
    }

    private static string? ShortCommit(string metadata)
    {
        foreach (var token in metadata.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= MinimumCommitLength && token.All(Uri.IsHexDigit))
            {
                return token[..ShortCommitLength].ToLowerInvariant();
            }
        }

        return null;
    }
}
