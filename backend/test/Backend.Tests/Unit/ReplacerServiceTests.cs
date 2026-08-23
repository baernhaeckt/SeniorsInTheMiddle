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
/// Two assumptions the slicing must not make, each pinned here as its opposite: that findings
/// never overlap, and that a reported offset always lands on the text it describes. Presidio
/// nests entities as a matter of course, and its offsets count code points where a .NET string
/// counts UTF-16 code units. Either assumption breaks on ordinary traffic -- the first as an
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

        byte[]? mutated = await Exchange(replacer, new Observer()).MutateRequestAsync(
            ReadOnlyMemory<byte>.Empty,
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNull(mutated, "Unchanged is reported as unchanged.");
        Assert.AreEqual(0, client.AnalyzeCalls);
    }

    /// <summary>
    /// What the dashboard is told about a rewrite: the spans that were actually written, as
    /// indices into the text it is shown, with the analyzer's confidence -- and not the spans
    /// that were nested inside another and never replaced on their own.
    /// </summary>
    [TestMethod]
    public async Task Findings_That_Were_Replaced_Are_Reported_With_Their_Offsets()
    {
        const string body = "Grüezi Hans Meier, mail an hans@example.ch";
        Observer observer = new();
        StubPiiService client = new([
            Finding(body, "Hans Meier", "PERSON"),
            Finding(body, "hans@example.ch", "EMAIL_ADDRESS"),
            Finding(body, "hans", "PERSON"),
        ]);

        byte[]? mutated = await Exchange(Replacer(client, []), observer).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("text/plain", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(mutated);
        Assert.AreEqual("Grüezi <PERSON>, mail an <EMAIL_ADDRESS>", Encoding.UTF8.GetString(mutated));

        Assert.IsNotNull(observer.Entities);
        Assert.AreEqual(2, observer.Entities.Count, "The name nested inside the e-mail was not replaced on its own.");

        DetectedEntity person = observer.Entities[0];
        Assert.AreEqual("PERSON", person.Kind);
        Assert.AreEqual("Hans Meier", person.Value);
        Assert.AreEqual("<PERSON>", person.Token);
        Assert.AreEqual(body.IndexOf("Hans Meier", StringComparison.Ordinal), person.Start);
        Assert.AreEqual(person.Start + "Hans Meier".Length, person.End);
        Assert.AreEqual(0.9, person.Confidence);
        Assert.AreEqual("Hans Meier", body[person.Start..person.End]);

        DetectedEntity email = observer.Entities[1];
        Assert.AreEqual("EMAIL_ADDRESS", email.Kind);
        Assert.AreEqual("hans@example.ch", body[email.Start..email.End]);
        Assert.AreNotEqual(person.Id, email.Id);
        Assert.IsTrue(observer.ScannedMs >= 0);

        Assert.IsNotNull(observer.Stats);
        Assert.AreEqual(1, observer.Stats.Suppressed, "The nested name counts as suppressed.");
        Assert.AreEqual(0, observer.Stats.NearMisses.Count);
    }

    [TestMethod]
    public async Task The_Detectors_Facts_About_A_Kind_Travel_With_The_Entity()
    {
        const string body = "Grüezi Hans Meier";
        Observer observer = new();
        StubPiiService client = new([
            Finding(body, "Hans Meier", "PERSON") with
            {
                InformationType = "Full Name",
                RiskLevel = 3,
                HipaaCategory = "Not Protected Health Information",
            },
        ]);

        await Exchange(Replacer(client, []), observer).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("text/plain", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(observer.Entities);
        DetectedEntity person = observer.Entities.Single();
        Assert.AreEqual("Full Name", person.InformationType);
        Assert.AreEqual(3, person.RiskLevel);
        Assert.AreEqual("Not Protected Health Information", person.HipaaCategory);
    }

    /// <summary>
    /// A finding under the threshold is told to the dashboard and nothing else: not replaced,
    /// not given a stand-in, not in the vault.
    /// </summary>
    [TestMethod]
    public async Task A_Near_Miss_Is_Reported_And_Not_Replaced()
    {
        const string body = "Grüezi Hans Meier aus Bern";
        Observer observer = new();
        StubPiiService client = new(
            [Finding(body, "Hans Meier", "PERSON")],
            ignored: [Finding(body, "Bern", "LOCATION") with { Score = 0.4 }]);

        byte[]? mutated = await Exchange(Replacer(client, []), observer).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("text/plain", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(mutated);
        Assert.AreEqual("Grüezi <PERSON> aus Bern", Encoding.UTF8.GetString(mutated));

        Assert.IsNotNull(observer.Stats);
        NearMiss miss = observer.Stats.NearMisses.Single();
        Assert.AreEqual("LOCATION", miss.Kind);
        Assert.AreEqual("Bern", miss.Value);
        Assert.AreEqual(0.4, miss.Confidence);
    }

    [TestMethod]
    public async Task A_Clean_Body_Still_Reports_Its_Near_Misses()
    {
        const string body = "Grüezi aus Bern";
        Observer observer = new();
        StubPiiService client = new([], ignored: [Finding(body, "Bern", "LOCATION") with { Score = 0.4 }]);

        byte[]? mutated = await Exchange(Replacer(client, []), observer).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("text/plain", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNull(mutated);
        Assert.IsNotNull(observer.Entities);
        Assert.AreEqual(0, observer.Entities.Count);
        Assert.IsNotNull(observer.Stats);
        Assert.AreEqual(1, observer.Stats.NearMisses.Count);
    }

    [TestMethod]
    public async Task A_Clean_Body_Is_Reported_As_Scanned_With_Nothing_Found()
    {
        Observer observer = new();

        byte[]? mutated = await Exchange(Replacer(new StubPiiService([]), []), observer).MutateRequestAsync(
            "{\"note\":\"nothing here\"}"u8.ToArray(),
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNull(mutated);
        Assert.IsNotNull(observer.Entities);
        Assert.AreEqual(0, observer.Entities.Count);
    }

    [TestMethod]
    public async Task A_Body_Of_Another_Media_Type_Is_Not_Read_And_Says_So()
    {
        Observer observer = new();
        StubPiiService client = new([]);

        byte[]? mutated = await Exchange(Replacer(client, []), observer).MutateRequestAsync(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            new BodyDescriptor("image/png", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNull(mutated);
        Assert.AreEqual(0, client.AnalyzeCalls);
        Assert.AreEqual("image/png not inspected", observer.PassthroughReason);
        Assert.IsNull(observer.Entities);
    }

    /// <summary>The response half puts back what the request half hid, and reports how many
    /// stand-ins it found to put back.</summary>
    [TestMethod]
    public async Task The_Response_Is_Restored_And_The_Count_Reported()
    {
        const string body = "Hans Meier und Hans Meier";
        Observer observer = new();
        ReplacerService replacer = Replacer(new StubPiiService([Finding(body, "Hans Meier", "PERSON"), Finding(body, "Hans Meier", "PERSON", occurrence: 1)]), []);
        IExchangeBodyMutation exchange = Exchange(replacer, observer);
        BodyDescriptor text = new("text/plain", Encoding.UTF8);

        await exchange.MutateRequestAsync(Encoding.UTF8.GetBytes(body), text, TestContext.CancellationTokenSource.Token);

        byte[]? restored = await exchange.MutateResponseAsync(
            "Hallo <PERSON>, nochmals <PERSON>!"u8.ToArray(),
            text,
            TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(restored);
        Assert.AreEqual("Hallo Hans Meier, nochmals Hans Meier!", Encoding.UTF8.GetString(restored));
        Assert.AreEqual("Hallo Hans Meier, nochmals Hans Meier!", observer.RestoredBody);
        Assert.AreEqual(2, observer.RestoredCount);
    }

    /// <summary>
    /// The shape that broke a chat request: spaCy, handed the raw document, reported two
    /// "persons" spanning keys, quotes and braces, and replacing them tore the JSON apart. The
    /// analyzer must never see the syntax -- only the values, one line each, labelled with
    /// their path for context. Array elements carry the array's key; nested keys are dotted.
    /// </summary>
    [TestMethod]
    public async Task Only_The_String_Values_Of_A_Json_Body_Are_Analyzed()
    {
        const string body = """{"action":"next","tz":{"name":"Europe/Zurich"},"presets":["cap:image","cap:file"],"count":3,"flag":true,"nothing":null,"empty":"","after":"x"}""";
        StubPiiService client = new([]);

        await Exchange(Replacer(client, []), new Observer()).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("[action] next\n[tz.name] Europe/Zurich\n[presets] cap:image\n[presets] cap:file\n[after] x", client.AnalyzedText);
    }

    /// <summary>
    /// A finding in a value is spliced into that value, the rest of the document is left as
    /// sent -- whitespace included -- and the offsets reported are indices into the original
    /// body, which is what the dashboard slices. The content type does not matter: the body
    /// parses, so it is JSON.
    /// </summary>
    [TestMethod]
    public async Task A_Finding_In_A_Json_Value_Is_Replaced_In_Place()
    {
        const string body = """
            {
              "customer": { "name": "Hans Meier", "company": "Acme AG" },
              "comment": "Bitte an Hans Meier liefern."
            }
            """;
        const string analyzed = "[customer.name] Hans Meier\n[customer.company] Acme AG\n[comment] Bitte an Hans Meier liefern.";
        Observer observer = new();
        StubPiiService client = new([Finding(analyzed, "Hans Meier", "PERSON"), Finding(analyzed, "Hans Meier", "PERSON", occurrence: 1)]);

        byte[]? mutated = await Exchange(Replacer(client, []), observer).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("text/plain", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(analyzed, client.AnalyzedText);
        Assert.IsNotNull(mutated);
        Assert.AreEqual(body.Replace("Hans Meier", "<PERSON>"), Encoding.UTF8.GetString(mutated));

        Assert.IsNotNull(observer.Entities);
        Assert.AreEqual(2, observer.Entities.Count);
        foreach (DetectedEntity entity in observer.Entities)
            Assert.AreEqual("Hans Meier", body[entity.Start..entity.End]);
    }

    /// <summary>
    /// A value with escapes is analysed decoded -- the model must see a line break, not a
    /// backslash and an n -- and the finding lands on the raw text, escapes and all.
    /// </summary>
    [TestMethod]
    public async Task A_Finding_After_An_Escape_Sequence_Lands_On_The_Raw_Text()
    {
        const string body = """{"msg":"Gr\u00fcezi,\nich bin Hans Meier \ud83d\ude42 aus Bern","x":1}""";
        const string analyzed = "[msg] Grüezi,\nich bin Hans Meier \U0001F642 aus Bern";
        Observer observer = new();
        StubPiiService client = new([Finding(analyzed, "Hans Meier", "PERSON"), Finding(analyzed, "Bern", "LOCATION")]);

        byte[]? mutated = await Exchange(Replacer(client, []), observer).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(analyzed, client.AnalyzedText);
        Assert.IsNotNull(mutated);
        Assert.AreEqual("""{"msg":"Gr\u00fcezi,\nich bin <PERSON> \ud83d\ude42 aus <LOCATION>","x":1}""", Encoding.UTF8.GetString(mutated));

        Assert.IsNotNull(observer.Entities);
        Assert.AreEqual("Hans Meier", body[observer.Entities[0].Start..observer.Entities[0].End]);
        Assert.AreEqual("Bern", body[observer.Entities[1].Start..observer.Entities[1].End]);
    }

    /// <summary>A stand-in is written into a JSON string, so it is escaped like one.</summary>
    [TestMethod]
    public async Task A_Stand_In_Is_Json_Escaped()
    {
        const string body = """{"addr":"Bahnhofstrasse 1"}""";
        const string standIn = "Musterweg 5\n8000 \"Zürich\"";
        StubPiiService client = new([Finding("[addr] Bahnhofstrasse 1", "Bahnhofstrasse 1", "LOCATION")], replacement: standIn);

        byte[]? mutated = await Exchange(Replacer(client, []), new Observer()).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(mutated);
        string rewritten = Encoding.UTF8.GetString(mutated);
        Assert.AreEqual("""{"addr":"Musterweg 5\n8000 \"Zürich\""}""", rewritten);
        Assert.AreEqual(standIn, System.Text.Json.JsonDocument.Parse(rewritten).RootElement.GetProperty("addr").GetString());
    }

    /// <summary>A finding the analyzer reports across two values, or over a label, is not a
    /// span of anything in the document, and is dropped rather than spliced in.</summary>
    [TestMethod]
    public async Task A_Finding_Across_Two_Json_Values_Or_Over_A_Label_Is_Dropped()
    {
        const string body = """{"a":"Hans","b":"Meier"}""";
        const string analyzed = "[a] Hans\n[b] Meier";
        List<TelemetryEvent> events = [];

        byte[]? mutated = await Exchange(Replacer(new StubPiiService([Finding(analyzed, "Hans\n[b] Meier", "PERSON"), Finding(analyzed, "[b] Meier", "PERSON")]), events), new Observer()).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNull(mutated);
        // The label-only finding is nested in the cross-value one, so the overlap rule drops
        // it first; only the outer span reaches the document check. (The stub faker hands
        // every PERSON the same stand-in, so a separate collision warning is expected too.)
        Assert.AreEqual(1, events.OfType<ProxyLog>().Count(l => l.Level == TelemetryLogLevel.Warn && l.Message.Contains("dropped")));
    }

    /// <summary>What does not parse is not JSON, whatever the header says, and is analysed as
    /// the text it is.</summary>
    [TestMethod]
    public async Task A_Body_That_Is_Not_Json_Is_Analyzed_As_Text()
    {
        const string body = "name=Hans Meier&city=Bern";
        StubPiiService client = new([Finding(body, "Hans Meier", "PERSON")]);

        byte[]? mutated = await Exchange(Replacer(client, []), new Observer()).MutateRequestAsync(
            Encoding.UTF8.GetBytes(body),
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(body, client.AnalyzedText);
        Assert.IsNotNull(mutated);
        Assert.AreEqual("name=<PERSON>&city=Bern", Encoding.UTF8.GetString(mutated));
    }

    /// <summary>
    /// The bug a person saw on a chat screen: they typed their own name, the proxy hid it, and
    /// the message drawn back into the conversation was the stand-in.
    ///
    /// It is not the answer that goes wrong -- an answer written about a fake name is expected to
    /// be about a fake name. It is the person's own message, which the client does not keep but
    /// re-reads from the server in a *later* request. A map that lived for one exchange was
    /// already gone by then, so nothing put it back.
    /// </summary>
    [TestMethod]
    public async Task A_Later_Request_Restores_What_An_Earlier_One_Hid()
    {
        const string sent = "Mein Name ist Christoph Keller, wann habe ich Geburtstag?";
        ReplacerService replacer = Replacer(Faking(sent, "Christoph Keller", "René Bauer"), []);
        BodyDescriptor text = new("text/plain", Encoding.UTF8);

        byte[]? hidden = await Exchange(replacer, new Observer()).MutateRequestAsync(
            Encoding.UTF8.GetBytes(sent),
            text,
            TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(hidden);
        StringAssert.Contains(Encoding.UTF8.GetString(hidden), "René Bauer");

        // A separate exchange, the way the conversation the client re-fetches is one.
        Observer observer = new();
        byte[]? restored = await Exchange(replacer, observer).MutateResponseAsync(
            Encoding.UTF8.GetBytes("""{"message":{"author":"user","text":"Mein Name ist René Bauer, wann habe ich Geburtstag?"}}"""),
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(restored);
        StringAssert.Contains(Encoding.UTF8.GetString(restored), "Mein Name ist Christoph Keller,");
        Assert.AreEqual(1, observer.RestoredCount);
    }

    /// <summary>
    /// The limit on the above. One device's stand-in is never put back for another, whatever the
    /// two of them happen to be reading -- a shared map is one person's real name appearing in
    /// somebody else's response.
    /// </summary>
    [TestMethod]
    public async Task Another_Client_Gets_Nothing_Put_Back()
    {
        const string sent = "Ich heisse Christoph Keller.";
        ReplacerService replacer = Replacer(Faking(sent, "Christoph Keller", "René Bauer"), []);
        BodyDescriptor text = new("text/plain", Encoding.UTF8);

        await Exchange(replacer, new Observer()).MutateRequestAsync(
            Encoding.UTF8.GetBytes(sent),
            text,
            TestContext.CancellationTokenSource.Token);

        Observer observer = new();
        byte[]? restored = await Exchange(replacer, observer, client: "Tablet|10.0.0.9").MutateResponseAsync(
            Encoding.UTF8.GetBytes("Hallo René Bauer."),
            text,
            TestContext.CancellationTokenSource.Token);

        Assert.IsNull(restored, "A stand-in was put back for a client that never hid anything.");
        Assert.AreEqual(0, observer.RestoredCount);
    }

    /// <summary>
    /// The same host, too. A stand-in registered against a chat backend must not rewrite the
    /// same run of characters on an unrelated site, where it is somebody else's name and not a
    /// stand-in for anything.
    /// </summary>
    [TestMethod]
    public async Task Another_Host_Gets_Nothing_Put_Back()
    {
        const string sent = "Ich heisse Christoph Keller.";
        ReplacerService replacer = Replacer(Faking(sent, "Christoph Keller", "René Bauer"), []);
        BodyDescriptor text = new("text/plain", Encoding.UTF8);

        await Exchange(replacer, new Observer()).MutateRequestAsync(
            Encoding.UTF8.GetBytes(sent),
            text,
            TestContext.CancellationTokenSource.Token);

        byte[]? restored = await Exchange(replacer, new Observer(), destination: "https://news.example.ch/")
            .MutateResponseAsync(
                Encoding.UTF8.GetBytes("Ein Beitrag von René Bauer."),
                text,
                TestContext.CancellationTokenSource.Token);

        Assert.IsNull(restored, "A stand-in was put back in a response from a host it was never sent to.");
    }

    /// <summary>
    /// The other half of why the chat screen stayed wrong. A Python backend writes JSON with
    /// ensure_ascii on by default, so the "René Bauer" this proxy sent comes back spelled
    /// "René Bauer" -- and a restore that searches for the characters it wrote finds
    /// nothing at all.
    /// </summary>
    [TestMethod]
    public async Task A_Stand_In_The_Origin_Escaped_Is_Still_Found()
    {
        const string sent = "Ich heisse Christoph Keller.";
        ReplacerService replacer = Replacer(Faking(sent, "Christoph Keller", "René Bauer"), []);
        IExchangeBodyMutation exchange = Exchange(replacer, new Observer());

        await exchange.MutateRequestAsync(
            Encoding.UTF8.GetBytes(sent),
            new BodyDescriptor("text/plain", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        // Built from "\\u00e9" rather than written out, so this is unmistakably the six
        // characters a Python writer emits and not an e-acute something decoded on the way in.
        string origin = "{\"text\":\"Hallo Ren" + "\\u00e9" + " Bauer!\"}";
        Assert.DoesNotContain("é", origin, "The origin body is not the escaped spelling.");

        byte[]? restored = await exchange.MutateResponseAsync(
            Encoding.UTF8.GetBytes(origin),
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(restored);
        Assert.AreEqual("""{"text":"Hallo Christoph Keller!"}""", Encoding.UTF8.GetString(restored));
    }

    /// <summary>
    /// And the direction that corrupts rather than misses: a real value spliced raw into a JSON
    /// string ends the string early and hands the client a body it cannot parse.
    /// </summary>
    [TestMethod]
    public async Task A_Real_Value_Is_Written_Back_Escaped_For_The_Body_It_Lands_In()
    {
        const string sent = """Ich heisse Hans "Hausi" Meier.""";
        ReplacerService replacer = Replacer(Faking(sent, """Hans "Hausi" Meier""", "Peter Muster"), []);
        IExchangeBodyMutation exchange = Exchange(replacer, new Observer());

        await exchange.MutateRequestAsync(
            Encoding.UTF8.GetBytes(sent),
            new BodyDescriptor("text/plain", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        byte[]? restored = await exchange.MutateResponseAsync(
            Encoding.UTF8.GetBytes("""{"text":"Hallo Peter Muster!"}"""),
            new BodyDescriptor("application/json", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(restored);
        Assert.AreEqual("""{"text":"Hallo Hans \"Hausi\" Meier!"}""", Encoding.UTF8.GetString(restored));
    }

    /// <summary>
    /// An event stream is the body a chat backend answers with, and the one body that must not
    /// be held. It is restored in the pieces it arrives in -- including when a piece ends in the
    /// middle of a stand-in, which a packet boundary does whenever it feels like it.
    /// </summary>
    [TestMethod]
    public async Task An_Event_Stream_Is_Restored_As_It_Arrives()
    {
        const string sent = "Ich heisse Christoph Keller.";
        Observer observer = new();
        ReplacerService replacer = Replacer(Faking(sent, "Christoph Keller", "René Bauer"), []);
        IExchangeBodyMutation exchange = Exchange(replacer, observer);

        await exchange.MutateRequestAsync(
            Encoding.UTF8.GetBytes(sent),
            new BodyDescriptor("text/plain", Encoding.UTF8),
            TestContext.CancellationTokenSource.Token);

        IExchangeStreamMutation? stream = exchange.CreateResponseStream(
            new BodyDescriptor("text/event-stream", Encoding.UTF8));

        Assert.IsNotNull(stream);

        // The boundary falls three characters into the stand-in, so those three are held rather
        // than written out where the next chunk can no longer reach them.
        string first = stream.Mutate("""data: {"v":"Hallo Ren""");
        Assert.AreEqual("""data: {"v":"Hallo """, first);

        string second = stream.Mutate("é Bauer\"}\n\n");
        string tail = stream.Flush();

        Assert.AreEqual("data: {\"v\":\"Hallo Christoph Keller\"}\n\n", first + second + tail);
        Assert.AreEqual(1, observer.RestoredCount);
    }

    /// <summary>A stream from a client that hid nothing is left alone rather than copied
    /// through a rewrite that could not find anything.</summary>
    [TestMethod]
    public void An_Event_Stream_With_Nothing_To_Put_Back_Is_Not_Rewritten_At_All()
        => Assert.IsNull(
            Exchange(Replacer(new StubPiiService([]), []), new Observer())
                .CreateResponseStream(new BodyDescriptor("text/event-stream", Encoding.UTF8)));

    /// <summary>An analyzer that finds <paramref name="detectedText"/> in <paramref name="content"/>,
    /// and a faker that always hands back <paramref name="standIn"/> for it.</summary>
    private static StubPiiService Faking(string content, string detectedText, string standIn)
        => new([Finding(content, detectedText, "PERSON")], standIn);

    private static IExchangeBodyMutation Exchange(
        ReplacerService replacer,
        IExchangeObserver observer,
        string client = "Laptop|127.0.0.1",
        string destination = "https://example.ch/")
        => ((IBodyMutationFactory)replacer).CreateForExchange(
            new ClientIdentity(client),
            new Uri(destination),
            observer);

    public TestContext TestContext { get; set; } = null!;

    /// <summary>A rewrite run's two outputs: the resulting body, and the events it published.</summary>
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
    {
        CollectingSink sink = new(events);

        return new ReplacerService(
            new TokenDetectionService(client),
            client,
            sink,
            new AnonymizerVault(new VaultLifetime(TimeSpan.FromHours(VaultLifetime.DefaultTtlHours), VaultLifetime.DefaultMaxClients), sink));
    }

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
    private sealed class StubPiiService(
        IReadOnlyList<PiiDetection> detections,
        string? replacement = null,
        IReadOnlyList<PiiDetection>? ignored = null) : IPiiServiceClient
    {
        public int AnalyzeCalls { get; private set; }

        public string? AnalyzedText { get; private set; }

        public bool IsEnabled => true;

        public Task<PiiAnalyzeResult> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(text);
            AnalyzeCalls++;
            AnalyzedText = text;

            return Task.FromResult(new PiiAnalyzeResult
            {
                DetectionResults = detections,
                DetectionCount = detections.Count,
                IgnoredResults = ignored ?? [],
            });
        }

        public Task<string> ReplacementTextAsync(string piiType, CancellationToken cancellationToken = default)
            => Task.FromResult(replacement ?? $"<{piiType}>");
    }

    /// <summary>Collects published events so a test can assert on what the code emitted.</summary>
    private sealed class CollectingSink(List<TelemetryEvent> events) : ITelemetrySink
    {
        public void Publish(TelemetryEvent telemetryEvent) => events.Add(telemetryEvent);
    }

    /// <summary>Records the callbacks the replacer made, so a test can assert on what it
    /// reported rather than only on the body it produced.</summary>
    private sealed class Observer : IExchangeObserver
    {
        public string? PassthroughReason { get; private set; }

        public IReadOnlyList<DetectedEntity>? Entities { get; private set; }

        public double ScannedMs { get; private set; }

        public DetectionStats? Stats { get; private set; }

        public string? RestoredBody { get; private set; }

        public int RestoredCount { get; private set; }

        public void Passthrough(string reason) => PassthroughReason = reason;

        public void Detected(IReadOnlyList<DetectedEntity> entities, DetectionStats stats)
        {
            Entities = entities;
            ScannedMs = stats.ScannedMs;
            Stats = stats;
        }

        public void ResponseBuffered() { }

        public void Restored(string responseBody, int restored)
        {
            RestoredBody = responseBody;
            RestoredCount = restored;
        }
    }
}
