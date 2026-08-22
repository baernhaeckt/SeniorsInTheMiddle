using System.Text;

using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;
using SeniorsInTheMiddle.Proxy.Services.Pii;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace Backend.Tests.Unit;

/// <summary>
/// What the replacer does with findings that do not arrive as a tidy left-to-right sequence of
/// spans -- which, from a real analyzer over a real body, is most of them.
///
/// Two assumptions used to be built into the slicing and are pinned here as the opposite:
/// findings never overlap, and a reported offset always lands on the text it describes. Presidio
/// nests entities as a matter of course, and its offsets count code points where a .NET string
/// counts UTF-16 code units, so both were wrong on ordinary traffic -- the first as a
/// <see cref="ArgumentOutOfRangeException"/> from a negative slice length, the second as a
/// replacement quietly sliding off the PII it was meant to cover.
/// </summary>
[TestClass]
public class ReplacerServiceTests
{
    /// <summary>
    /// The shape that took production down: a long URL, with an e-mail and a name inside it,
    /// all three reported as findings. Replacing the URL puts the write position past the two
    /// nested spans, and copying "the gap" to either of them is a negative length.
    /// </summary>
    [TestMethod]
    public async Task Findings_Nested_In_A_Longer_One_Do_Not_Tear_The_Body_Apart()
    {
        const string body = """{"callback":"https://api.bank.ch/v1/notify?user=max.muster@bank.ch&token=abc","note":"ok"}""";

        Anonymized result = await AnonymizeAsync(
            body,
            Finding(body, "https://api.bank.ch/v1/notify?user=max.muster@bank.ch&token=abc", "URL"),
            Finding(body, "max.muster@bank.ch", "EMAIL_ADDRESS"),
            Finding(body, "max.muster", "PERSON"));

        Assert.AreEqual("""{"callback":"<URL>","note":"ok"}""", result.Body);
    }

    /// <summary>
    /// The same run of text classified twice puts two findings on one position. Neither is
    /// nested in the other, so nothing about the spans themselves says which to apply.
    /// </summary>
    [TestMethod]
    public async Task Text_Classified_Twice_Is_Replaced_Once()
    {
        const string body = "Absender: Bern";

        Anonymized result = await AnonymizeAsync(
            body,
            Finding(body, "Bern", "LOCATION"),
            Finding(body, "Bern", "PERSON"));

        Assert.AreEqual("Absender: <LOCATION>", result.Body);
    }

    /// <summary>Findings that genuinely do not overlap are all applied, which is what the
    /// overlap rule must not cost.</summary>
    [TestMethod]
    public async Task Separate_Findings_Are_All_Replaced()
    {
        const string body = "Hans Meier, hans@meier.ch, 079 123 45 67";

        Anonymized result = await AnonymizeAsync(
            body,
            Finding(body, "Hans Meier", "PERSON"),
            Finding(body, "hans@meier.ch", "EMAIL_ADDRESS"),
            Finding(body, "079 123 45 67", "PHONE_NUMBER"));

        Assert.AreEqual("<PERSON>, <EMAIL_ADDRESS>, <PHONE_NUMBER>", result.Body);
    }

    /// <summary>One finding, several occurrences: the detection service groups them into a
    /// single token carrying every position.</summary>
    [TestMethod]
    public async Task Every_Occurrence_Of_One_Finding_Is_Replaced()
    {
        const string body = "Meier zahlt, Meier wartet, Meier fragt";

        Anonymized result = await AnonymizeAsync(
            body,
            Finding(body, "Meier", "PERSON"),
            Finding(body, "Meier", "PERSON", occurrence: 1),
            Finding(body, "Meier", "PERSON", occurrence: 2));

        Assert.AreEqual("<PERSON> zahlt, <PERSON> wartet, <PERSON> fragt", result.Body);
    }

    /// <summary>
    /// An emoji earlier in the body is one code point to the analyzer and two chars to .NET, so
    /// every offset after it is short by one. Reading the finding at its reported offset would
    /// take " Hans Meie" -- leaving the "r" behind and eating the space in front.
    /// </summary>
    [TestMethod]
    public async Task Offsets_After_A_Surrogate_Pair_Still_Land_On_The_Finding()
    {
        const string body = "Kunde \U0001F642 Hans Meier \U0001F642 meldet sich";

        Anonymized result = await AnonymizeAsync(body, Finding(body, "Hans Meier", "PERSON"));

        Assert.AreEqual("Kunde \U0001F642 <PERSON> \U0001F642 meldet sich", result.Body);
    }

    /// <summary>An offset that does not describe the text it came with is not applied where it
    /// points; the text itself decides.</summary>
    [TestMethod]
    public async Task A_Finding_Reported_At_The_Wrong_Offset_Is_Placed_By_Its_Text()
    {
        const string body = "Konto von Hans Meier";

        Anonymized result = await AnonymizeAsync(body, At(body, "Hans Meier", "PERSON", startPosition: 0));

        Assert.AreEqual("Konto von <PERSON>", result.Body);
    }

    /// <summary>
    /// A finding whose text is nowhere in the body cannot be placed at all. Replacing something
    /// at the reported offset anyway would corrupt the body without hiding anything, so it is
    /// dropped -- and said out loud, because a service reporting findings over text this proxy
    /// never sent is not a small thing.
    /// </summary>
    [TestMethod]
    public async Task A_Finding_That_Is_Nowhere_Is_Dropped_And_Reported()
    {
        const string body = "Nichts zu sehen";

        Anonymized result = await AnonymizeAsync(body, At(body, "Hans Meier", "PERSON", startPosition: 3));

        Assert.AreEqual(body, result.Body);

        ProxyLog warning = result.Events.OfType<ProxyLog>().Single();
        Assert.AreEqual(TelemetryLogLevel.Warn, warning.Level);
        StringAssert.Contains(warning.Message, "PERSON");
    }

    /// <summary>The analyzer refuses empty text, so a body with nothing in it must not reach
    /// it -- a chunked request that sends no bytes is an ordinary request, not an error.</summary>
    [TestMethod]
    public async Task An_Empty_Body_Is_Not_Sent_To_The_Analyzer()
    {
        StubPiiService client = new([]);
        ReplacerService replacer = Replacer(client, []);

        byte[]? mutated = await ((IExchangeBodyMutation)replacer).MutateRequestAsync(
            ReadOnlyMemory<byte>.Empty,
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(0, mutated?.Length);
        Assert.AreEqual(0, client.AnalyzeCalls);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed record Anonymized(string Body, IReadOnlyList<TelemetryEvent> Events);

    private async Task<Anonymized> AnonymizeAsync(string content, params PiiDetection[] detections)
    {
        List<TelemetryEvent> events = [];
        StubPiiService client = new(detections);

        using MemoryStream anonymized = await Replacer(client, events)
            .AnonymizeAsync(content, TestContext.CancellationTokenSource.Token);

        using StreamReader reader = new(anonymized, Encoding.UTF8);

        return new Anonymized(await reader.ReadToEndAsync(), events);
    }

    private static ReplacerService Replacer(StubPiiService client, List<TelemetryEvent> events)
        => new(new TokenDetectionService(client), new TokenAnonymizerService(client), new CollectingSink(events));

    /// <summary>A finding over text that is actually in the body, with the offset the analyzer
    /// would report for it -- counted in code points, as the python side counts.</summary>
    private static PiiDetection Finding(string content, string detectedText, string entityType, int occurrence = 0)
    {
        int index = -1;
        for (int found = 0; found <= occurrence; found++)
        {
            index = content.IndexOf(detectedText, index + 1, StringComparison.Ordinal);
            Assert.IsTrue(index >= 0, $"'{detectedText}' does not occur {occurrence + 1} time(s) in the test body.");
        }

        return At(content, detectedText, entityType, CodePointsIn(content, index));
    }

    /// <summary>A finding at an offset of the test's choosing, for the ones the analyzer gets
    /// wrong.</summary>
    private static PiiDetection At(string content, string detectedText, string entityType, int startPosition)
        => new()
        {
            InformationType = "TEST",
            EntityType = entityType,
            Score = 0.9,
            StartPosition = startPosition,
            EndPosition = startPosition + CodePointsIn(detectedText, detectedText.Length),
            DetectedText = detectedText,
            RiskLevel = 3,
            HipaaCategory = "NON_PHI",
        };

    /// <summary>Code points in the first <paramref name="length"/> chars, which is what python
    /// would have counted over the same text.</summary>
    private static int CodePointsIn(string text, int length)
    {
        int codePoints = 0;

        for (int index = 0; index < length; index++)
        {
            if (!char.IsLowSurrogate(text[index]))
                codePoints++;
        }

        return codePoints;
    }

    /// <summary>The analyzer, reduced to the findings a test hands it. The guard against empty
    /// text is the real client's, and is here so that a caller that stops respecting it fails
    /// a test rather than a request.</summary>
    private sealed class StubPiiService(IReadOnlyList<PiiDetection> detections) : IPiiServiceClient
    {
        public int AnalyzeCalls { get; private set; }

        public bool IsEnabled => true;

        public Task<PiiAnalyzeResult> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(text);
            AnalyzeCalls++;

            return Task.FromResult(new PiiAnalyzeResult
            {
                DetectionResults = detections,
                DetectionCount = detections.Count,
            });
        }

        public Task<string> ReplacementTextAsync(string piiType, CancellationToken cancellationToken = default)
            => Task.FromResult($"<{piiType}>");
    }

    private sealed class CollectingSink(List<TelemetryEvent> events) : ITelemetrySink
    {
        public void Publish(TelemetryEvent telemetryEvent) => events.Add(telemetryEvent);
    }
}
