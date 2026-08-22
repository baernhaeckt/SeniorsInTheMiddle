using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins the narrowing that keeps a site usable while the part of it that carries a prompt is still
/// read. Getting this wrong in one direction costs a prompt that went unscanned; in the other it
/// corrupts bodies that were never anyone's business, which is how a page stops loading at all.
/// </summary>
[TestClass]
public class InspectionScopeTests
{
    private static InspectionScope For(string host, params string[] paths)
    {
        ConfigurationBuilder builder = new();

        builder.AddInMemoryCollection(paths
            .Select((path, index) =>
                new KeyValuePair<string, string?>($"Proxy:InspectOnly:{host}:{index}", path)));

        return new InspectionScope(builder.Build(), NullLogger<InspectionScope>.Instance);
    }

    private static InspectionScope Unconfigured()
        => new(new ConfigurationBuilder().Build(), NullLogger<InspectionScope>.Instance);

    [TestMethod]
    public void A_Host_With_No_Entry_Is_Inspected_Everywhere()
    {
        InspectionScope scope = Unconfigured();

        Assert.IsTrue(scope.Allows(new Uri("https://example.com/anything")));
        Assert.IsTrue(scope.Allows(new Uri("https://chatgpt.com/backend-api/conversation")));
    }

    [TestMethod]
    public void A_Scoped_Host_Inspects_The_Named_Paths()
    {
        InspectionScope scope = For("chatgpt.com", "/backend-api/conversation");

        Assert.IsTrue(scope.Allows(new Uri("https://chatgpt.com/backend-api/conversation")));

        // The prefix covers what hangs off it: one conversation, and the query a send carries.
        Assert.IsTrue(scope.Allows(new Uri("https://chatgpt.com/backend-api/conversation/abc-123")));
        Assert.IsTrue(scope.Allows(new Uri("https://chatgpt.com/backend-api/conversation?stream=1")));
    }

    /// <summary>
    /// The whole point: the application around the chat endpoint is left alone. Every one of these
    /// was being scanned and rewritten before, and the challenge under /cdn-cgi/ is what turned a
    /// page load into a redirect loop.
    /// </summary>
    [TestMethod]
    public void A_Scoped_Host_Leaves_The_Rest_Of_Its_Origin_Alone()
    {
        InspectionScope scope = For("chatgpt.com", "/backend-api/conversation");

        Assert.IsFalse(scope.Allows(new Uri("https://chatgpt.com/")));
        Assert.IsFalse(scope.Allows(new Uri("https://chatgpt.com/cdn-cgi/challenge-platform/h/g/jsd/r/0x1")));
        Assert.IsFalse(scope.Allows(new Uri("https://chatgpt.com/backend-api/settings/beta_features")));
        Assert.IsFalse(scope.Allows(new Uri("https://chatgpt.com/ces/v1/projects/oai/events")));
    }

    [TestMethod]
    public void A_Scoped_Host_Covers_Its_Subdomains()
    {
        InspectionScope scope = For("chatgpt.com", "/backend-api/conversation");

        Assert.IsTrue(scope.Allows(new Uri("https://ab.chatgpt.com/backend-api/conversation")));
        Assert.IsFalse(scope.Allows(new Uri("https://ab.chatgpt.com/v1/rgstr")));
    }

    /// <summary>A rule written for one domain must not reach a neighbour who merely ends the same.</summary>
    [TestMethod]
    public void A_Scoped_Host_Does_Not_Reach_A_Neighbouring_Domain()
    {
        InspectionScope scope = For("chatgpt.com", "/backend-api/conversation");

        Assert.IsTrue(scope.Allows(new Uri("https://notchatgpt.com/anything")));
    }

    [TestMethod]
    public void Several_Paths_Are_All_Inspected()
    {
        InspectionScope scope = For("chatgpt.com", "/backend-api/conversation", "/backend-alpha/conversation");

        Assert.IsTrue(scope.Allows(new Uri("https://chatgpt.com/backend-api/conversation")));
        Assert.IsTrue(scope.Allows(new Uri("https://chatgpt.com/backend-alpha/conversation")));
        Assert.IsFalse(scope.Allows(new Uri("https://chatgpt.com/backend-api/me")));
    }
}
