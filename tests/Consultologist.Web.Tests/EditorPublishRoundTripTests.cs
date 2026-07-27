using System.Text.Json;
using Bunit;
using Consultologist.Web.Pages;
using NSubstitute;

// Both assemblies define package records; the client's are what the editor
// sends, the server's are what validates them.
using ClientWorkflow = Consultologist.Web.Services.Workflow;
using ServerWorkflow = Consultologist.Api.Workflow;

namespace Consultologist.Web.Tests;

/// <summary>
/// The contract that actually broke for v7 (#218): what the editor composes
/// must satisfy the validator the registry runs at publish. This captures the
/// real publish payload and feeds it to the server's own validator, rather
/// than asserting on JSON shape and hoping the two agree.
/// </summary>
public class EditorPublishRoundTripTests : ClientRenderTestContext
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private async Task<ServerWorkflow.WorkflowPackageValidator.ValidationResult> PublishAndValidateAsync(
        Func<IRenderedComponent<Templates>, Task> edit,
        bool v7 = true)
    {
        var package = v7 ? EditorFixtures.V7() : EditorFixtures.V6();
        WorkflowService.GetCurrentPackageContentAsync().Returns(package);

        ClientWorkflow.WorkflowPackagePublishRequest? sent = null;
        WorkflowService
            .PublishPackageAsync(Arg.Do<ClientWorkflow.WorkflowPackagePublishRequest>(request => sent = request))
            .Returns(new ClientWorkflow.WorkflowPublishOutcome(
                new ClientWorkflow.WorkflowPackagePublishResponse("acct-1234567890ab", "v2026.07.2", "acct-1234567890ab@v2026.07.2"),
                Array.Empty<string>()));

        var page = Render<Templates>();
        await edit(page);

        page.FindAll("fluent-button").First(button => button.TextContent.Contains("Publish")).Click();

        Assert.NotNull(sent);

        var manifest = JsonSerializer.Deserialize<ServerWorkflow.WorkflowPackageManifest>(sent!.Manifest.GetRawText(), JsonOptions)!;
        var files = sent.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        return ServerWorkflow.WorkflowPackageValidator.Validate(manifest, files, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static void Navigate(IRenderedComponent<Templates> page, string label) =>
        page.FindAll("button.editor-nav__item")
            .First(button => button.TextContent.Replace("●", string.Empty).Trim() == label)
            .Click();

    [Fact]
    public async Task V7Package_EditedInputs_ComposesAValidManifest()
    {
        var result = await PublishAndValidateAsync(page =>
        {
            Navigate(page, "Inputs");
            page.Find(".add-variable__form input.node-field__input").Change("labs");
            page.Find(".add-variable__form button").Click();
            return Task.CompletedTask;
        });

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public async Task V7Package_RenamedInput_ComposesAValidManifest()
    {
        // The cascade's whole purpose: after a rename the bindings still name
        // a declared slot, so the package validates.
        var result = await PublishAndValidateAsync(page =>
        {
            Navigate(page, "Inputs");
            page.FindAll("li.declared-row")[0].QuerySelector("input.declared-row__id")!.Change("referral");
            return Task.CompletedTask;
        });

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public async Task V7Package_RelabelledDocument_KeepsTheResultsFormOnly()
    {
        var result = await PublishAndValidateAsync(page =>
        {
            Navigate(page, "Documents");
            page.FindAll("li.declared-row")[0]
                .QuerySelector("input.node-field__input:not(.declared-row__id)")!
                .Change("Consult letter");
            return Task.CompletedTask;
        });

        // Before the repair this failed with "Declare result or results, not
        // both" — the composer wrote the string result unconditionally.
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public async Task V6Package_StillComposesAValidManifest()
    {
        // Publish is gated on pending edits, so this needs a real one: the
        // editor opens on the first data item's text.
        var result = await PublishAndValidateAsync(page =>
        {
            page.Find("fluent-text-area").Change("Document the presenting illness, chronologically.");
            return Task.CompletedTask;
        }, v7: false);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }
}
