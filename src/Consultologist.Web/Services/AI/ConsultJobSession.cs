namespace Consultologist.Web.Services.AI;

public sealed record ConsultJobBlock(string Id, string Name);

/// <summary>
/// What Consults needs to rebuild its run view for a job started in this tab:
/// the job record carries no draft text, so the draft (and the exact block
/// roster the run was submitted with) live only here.
/// </summary>
public sealed record ConsultJobMemento(
    string JobId,
    string Draft,
    string? WorkflowPackageRef,
    IReadOnlyList<ConsultJobBlock> Blocks);

/// <summary>
/// #207: per-tab memory of the most recently submitted consult job, so
/// navigating away from Consults mid-run and returning re-attaches instead of
/// forgetting. Scoped DI in Blazor WASM = one instance per tab session; a new
/// submission overwrites it and navigation never clears it (that's the point).
/// </summary>
public sealed class ConsultJobSession
{
    public ConsultJobMemento? Current { get; set; }
}
