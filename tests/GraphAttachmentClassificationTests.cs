using System.Text.Json;
using Consultologist.Api.Email;

namespace Consultologist.Api.Tests;

/// <summary>
/// #249: the rule that decides what one attachment in Graph's collection is.
///
/// It lives here rather than being reached through the processor because
/// every processor test substitutes <c>IGraphMailClient</c> wholesale — so a
/// test that fed the processor a listing would assert the processor's
/// reaction, not this rule. The inline case in particular has to be pinned
/// where it actually runs: treating a signature logo as a discrepancy would
/// bounce every signed email.
/// </summary>
public class GraphAttachmentClassificationTests
{
    private static JsonElement Item(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AFileAttachment_IsReadable()
    {
        var classified = GraphMailClient.Classify(Item(
            """{"@odata.type":"#microsoft.graph.fileAttachment","name":"n.txt","contentBytes":"aGk="}"""));

        Assert.False(classified.IsInline);
        Assert.Null(classified.UnreadableKind);
        Assert.Equal("aGk=", classified.ContentBytes);
    }

    [Fact]
    public void AnInlinePart_IsSkippedAndIsNotADiscrepancy()
    {
        // A signature logo. Both properties matter: skipped, and NOT counted
        // as unreadable — otherwise every signed email bounces.
        var classified = GraphMailClient.Classify(Item(
            """{"@odata.type":"#microsoft.graph.fileAttachment","isInline":true,"contentBytes":"aGk="}"""));

        Assert.True(classified.IsInline);
        Assert.Null(classified.UnreadableKind);
    }

    [Fact]
    public void AnInlinePartWithNoBytes_IsStillJustInline()
    {
        // Inline is checked first on purpose: an inline part that also lacks
        // contentBytes must not be reported as a discrepancy.
        var classified = GraphMailClient.Classify(Item(
            """{"@odata.type":"#microsoft.graph.itemAttachment","isInline":true}"""));

        Assert.True(classified.IsInline);
        Assert.Null(classified.UnreadableKind);
    }

    [Theory]
    [InlineData("#microsoft.graph.referenceAttachment")]
    [InlineData("#microsoft.graph.itemAttachment")]
    public void AnAttachmentWithNoBytes_IsReportedByKind(string kind)
    {
        var classified = GraphMailClient.Classify(Item($$"""{"@odata.type":"{{kind}}","name":"Smith_John.pdf"}"""));

        Assert.Equal(kind, classified.UnreadableKind);
        Assert.Null(classified.ContentBytes);
    }

    [Fact]
    public void AnAttachmentWithNoBytesAndNoType_IsReportedAsUnknown()
    {
        var classified = GraphMailClient.Classify(Item("""{"name":"n.txt"}"""));

        Assert.Equal(GraphMailClient.UnknownAttachmentKind, classified.UnreadableKind);
    }

    [Fact]
    public void ANullContentBytes_CountsAsNoBytes()
    {
        // JSON null is not a string; reading it as one would hand the caller
        // a null to base64-decode.
        var classified = GraphMailClient.Classify(Item(
            """{"@odata.type":"#microsoft.graph.fileAttachment","contentBytes":null}"""));

        Assert.Equal("#microsoft.graph.fileAttachment", classified.UnreadableKind);
        Assert.Null(classified.ContentBytes);
    }
}
