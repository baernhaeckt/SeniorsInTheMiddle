using System.Diagnostics;
using System.Text;

using SeniorsInTheMiddle.Proxy.Services.Pii;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

public sealed class ReplacerService : IBodyMutationFactory
{
    /// <summary>
    /// Each JSON string value reaches the analyzer as one line, labelled with where it came
    /// from: <c>[customer.name] Hans Meier</c>. The label gives the model the structure as
    /// context without the syntax. It is never written anywhere: findings are spliced back
    /// into the original document by offset, so the keys are never taken apart to begin with.
    /// </summary>
    private const string ValueSeparator = "\n";

    private readonly TokenDetectionService _tokenDetectionService;

    private readonly IPiiServiceClient _piiClient;

    private readonly ITelemetrySink _telemetrySink;

    private readonly AnonymizerVault _vault;

    public ReplacerService(
        TokenDetectionService tokenDetectionService,
        IPiiServiceClient piiClient,
        ITelemetrySink telemetrySink,
        AnonymizerVault vault)
    {
        _tokenDetectionService = tokenDetectionService;
        _piiClient = piiClient;
        _telemetrySink = telemetrySink;
        _vault = vault;
    }

    public bool Rewrites => true;

    /// <summary>
    /// Rewrites every detected token in <paramref name="content"/> with its anonymized stand-in,
    /// as UTF-8. For tests; the proxy goes through <see cref="IBodyMutationFactory"/>.
    /// </summary>
    public async Task<MemoryStream> AnonymizeAsync(string content, CancellationToken cancellationToken)
    {
        Anonymization result = await AnonymizeWithFindingsAsync(content, NewAnonymizer(), cancellationToken);

        return new MemoryStream(Encoding.UTF8.GetBytes(result.Body));
    }

    /// <summary>
    /// The rewrite, with the spans that were actually written -- which is what the telemetry
    /// reports, since a finding that was nested in another was never replaced on its own.
    ///
    /// The findings arrive as spans over the text, and nothing about them guarantees the tidy
    /// left-to-right sequence that copying the gaps between them assumes. Presidio reports
    /// overlapping entities as a matter of course -- an address that contains a city, a URL that
    /// contains an e-mail, a name inside both -- and the same text classified twice lands two
    /// findings on one position. So the spans are ordered, and any that starts inside the one
    /// already written is dropped: the enclosing span was replaced whole, so the nested finding
    /// has nothing left to hide.
    /// </summary>
    internal async Task<Anonymization> AnonymizeWithFindingsAsync(
        string content,
        TokenAnonymizerService anonymizer,
        CancellationToken cancellationToken)
    {
        if (content.Length == 0)
            return Anonymization.Empty;

        long startedAt = Stopwatch.GetTimestamp();

        Findings findings = await FindReplacementsAsync(content, anonymizer, cancellationToken);
        List<TokenReplacement> replacements = findings.Replacements;

        double scannedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        StringBuilder result = new(content.Length);
        List<TokenReplacement> applied = [];

        int lastIndex = 0;
        foreach (TokenReplacement replacement in NonOverlapping(replacements))
        {
            result.Append(content, lastIndex, replacement.Position - lastIndex).Append(replacement.AnonymizedValue);
            lastIndex = replacement.Position + replacement.Length;
            applied.Add(replacement);
        }

        result.Append(content, lastIndex, content.Length - lastIndex);

        return new Anonymization(
            result.ToString(),
            applied,
            scannedMs,
            replacements.Count - applied.Count + findings.Unplaced,
            findings.NearMisses);
    }

    /// <summary>
    /// The same for a JSON document, where only the string values are analysed.
    ///
    /// A named-entity model handed the raw document reads keys, quotes and braces as prose, and
    /// given a few hundred characters of them confidently reports a "person" that spans half
    /// the structure; replacing that tears the document apart. So the values are cut out,
    /// analysed as text, and each finding is spliced back into the value it came from -- with
    /// the stand-in JSON-escaped, and the document otherwise left char for char as the client
    /// sent it, so every offset reported onwards is still an index into that body.
    ///
    /// A body that does not parse is not JSON, and is treated as the text it is.
    /// </summary>
    internal async Task<Anonymization> AnonymizeJsonWithFindingsAsync(
        string content,
        TokenAnonymizerService anonymizer,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonStringValue>? values = JsonStringValues.Locate(content);

        if (values is null)
            return await AnonymizeWithFindingsAsync(content, anonymizer, cancellationToken);

        long startedAt = Stopwatch.GetTimestamp();

        // One analysis over every value rather than one per value: the model's cost is mostly
        // per call, and a chat request carries dozens of strings.
        StringBuilder joined = new();
        List<(JsonStringValue Value, int JoinedStart)> segments = [];

        foreach (JsonStringValue value in values)
        {
            if (value.Value.Length == 0)
                continue;

            if (joined.Length > 0)
                joined.Append(ValueSeparator);

            joined.Append('[').Append(value.Path).Append("] ");
            segments.Add((value, joined.Length));
            joined.Append(value.Value);
        }

        List<TokenReplacement> applied = [];
        StringBuilder result = new(content.Length);
        int lastIndex = 0;
        int suppressed = 0;
        IReadOnlyList<NearMiss> nearMisses = [];

        if (segments.Count > 0)
        {
            string joinedText = joined.ToString();
            Findings findings = await FindReplacementsAsync(joinedText, anonymizer, cancellationToken);
            List<TokenReplacement> replacements = findings.Replacements;
            nearMisses = findings.NearMisses;
            suppressed = findings.Unplaced;

            foreach (TokenReplacement replacement in NonOverlapping(replacements))
            {
                TokenReplacement? inDocument = ToDocumentSpan(content, segments, replacement);

                if (inDocument is null)
                {
                    // Reported across two values -- which are unrelated text that happened to
                    // be analysed together -- into a label, or off the end of a value. Nothing
                    // in the document is that span.
                    _telemetrySink.Warn(
                        $"A {replacement.Token.Classification} finding was dropped: it does not lie within a single JSON string value.");

                    continue;
                }

                result.Append(content, lastIndex, inDocument.Position - lastIndex).Append(inDocument.AnonymizedValue);
                lastIndex = inDocument.Position + inDocument.Length;
                applied.Add(inDocument);
            }

            suppressed += replacements.Count - applied.Count;
        }

        result.Append(content, lastIndex, content.Length - lastIndex);

        return new Anonymization(
            result.ToString(),
            applied,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            suppressed,
            nearMisses);
    }

    IExchangeBodyMutation IBodyMutationFactory.CreateForExchange(
        ClientIdentity client,
        Uri destination,
        IExchangeObserver observer)
        => new Exchange(this, _vault.For(client, destination, NewAnonymizer), observer);

    /// <summary>A stand-in map for one client and host -- see <see cref="TokenAnonymizerService"/>.
    /// The vault decides whether this is called at all; a client that has been here recently
    /// gets the map it already had.</summary>
    private TokenAnonymizerService NewAnonymizer() => new(_piiClient, _telemetrySink);

    /// <summary>Every placeable finding over <paramref name="text"/>, with its stand-in -- and
    /// what the detector said that is not one: near misses, and findings that sit nowhere.</summary>
    private async Task<Findings> FindReplacementsAsync(
        string text,
        TokenAnonymizerService anonymizer,
        CancellationToken cancellationToken)
    {
        TokenDetection detection = await _tokenDetectionService.DetectTokensAsync(text, cancellationToken);

        // Each distinct token costs one round trip to the service, and the client multiplexes
        // calls on its socket, so the round trips are made together rather than one after
        // another.
        (TokenDetectionResult Token, string AnonymizedValue)[] foundTokens = await Task.WhenAll(
            detection.Tokens.Select(async token =>
                (token, await anonymizer.AnonymizeTokenAsync(token.Token, cancellationToken))));

        int unplaced = 0;
        List<TokenReplacement> replacements = GetTokenReplacements(text, foundTokens, ref unplaced);

        return new Findings(replacements, detection.NearMisses, unplaced);
    }

    /// <summary>
    /// The spans in document order, without any that starts inside one already taken. Ordering
    /// by descending length at equal position is what makes this keep the outermost of two.
    /// </summary>
    private static IEnumerable<TokenReplacement> NonOverlapping(IEnumerable<TokenReplacement> replacements)
    {
        int lastIndex = 0;

        foreach (TokenReplacement replacement in replacements.OrderBy(tr => tr.Position).ThenByDescending(tr => tr.Length))
        {
            if (replacement.Position < lastIndex)
                continue;

            lastIndex = replacement.Position + replacement.Length;

            yield return replacement;
        }
    }

    /// <summary>
    /// A finding over the joined values, as the span of the document it describes -- raw
    /// offsets, with the stand-in escaped for where it is going -- or null when it does not lie
    /// within one value.
    /// </summary>
    private static TokenReplacement? ToDocumentSpan(
        string document,
        List<(JsonStringValue Value, int JoinedStart)> segments,
        TokenReplacement replacement)
    {
        int segmentIndex = LastSegmentStartingAtOrBefore(segments, replacement.Position);

        if (segmentIndex < 0)
            return null;

        (JsonStringValue value, int joinedStart) = segments[segmentIndex];
        int localStart = replacement.Position - joinedStart;
        int localEnd = localStart + replacement.Length;

        if (localStart < 0 || localEnd > value.Value.Length)
            return null;

        int rawStart;
        int rawEnd;

        if (value.IsVerbatim)
        {
            rawStart = value.RawStart + localStart;
            rawEnd = value.RawStart + localEnd;
        }
        else
        {
            int[] raw = value.RawIndices(document);
            rawStart = value.RawStart + raw[localStart];
            rawEnd = value.RawStart + raw[localEnd];
        }

        // The same writer the restore searches with, so what is spliced in here is character
        // for character what it looks for coming back -- see JsonText.
        string escaped = JsonText.Escape(replacement.AnonymizedValue, asciiOnly: false);

        return replacement with { AnonymizedValue = escaped, Position = rawStart, Length = rawEnd - rawStart };
    }

    private static int LastSegmentStartingAtOrBefore(List<(JsonStringValue Value, int JoinedStart)> segments, int position)
    {
        int low = 0;
        int high = segments.Count - 1;
        int found = -1;

        while (low <= high)
        {
            int mid = (low + high) / 2;

            if (segments[mid].JoinedStart <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// The findings as spans that are known to sit on the text they describe. One whose span
    /// cannot be placed is dropped rather than applied at the reported offset: replacing the
    /// wrong run of characters corrupts the body without hiding anything.
    /// </summary>
    private List<TokenReplacement> GetTokenReplacements(
        string content,
        IEnumerable<(TokenDetectionResult Token, string AnonymizedValue)> anonymizedTokens,
        ref int unplaced)
    {
        CodePointOffsetMap offsets = CodePointOffsetMap.For(content);
        List<TokenReplacement> replacements = [];

        foreach (var (tokenDetectionResult, anonymizedValue) in anonymizedTokens)
        {
            string detectedText = tokenDetectionResult.Token.Value;

            // Nothing to cover, and a zero-length span would still take part in the ordering and
            // the overlap decisions around it.
            if (detectedText.Length == 0)
                continue;

            foreach (TokenOccurrence occurrence in tokenDetectionResult.Occurrences)
            {
                int position = PositionOf(content, detectedText, offsets.ToStringIndex(occurrence.Position));

                if (position < 0)
                {
                    unplaced++;
                    _telemetrySink.Warn(
                        $"A {tokenDetectionResult.Token.Classification} finding was dropped: its text is not at the reported position {occurrence.Position}, nor anywhere else in the body.");

                    continue;
                }

                replacements.Add(new TokenReplacement(
                    tokenDetectionResult.Token,
                    anonymizedValue,
                    position,
                    detectedText.Length,
                    occurrence.Score,
                    tokenDetectionResult.Facts));
            }
        }

        return replacements;
    }

    /// <summary>
    /// Where <paramref name="detectedText"/> actually sits, given the offset the service
    /// reported for it, or -1 when it sits nowhere.
    ///
    /// The reported offset is checked rather than trusted. It is computed on the other side of a
    /// process boundary, over a copy of the text, and one that does not land on the text it
    /// claims to describe is a fact about this body -- not something to discover after the wrong
    /// characters have already been replaced.
    /// </summary>
    private static int PositionOf(string content, string detectedText, int reportedPosition)
    {
        if (reportedPosition >= 0
            && reportedPosition + detectedText.Length <= content.Length
            && content.AsSpan(reportedPosition, detectedText.Length).SequenceEqual(detectedText))
        {
            return reportedPosition;
        }

        // Off, so the next occurrence at or after it is the best remaining guess -- and then the
        // first one anywhere, for an offset that pointed past the last of them.
        int searchFrom = Math.Clamp(reportedPosition, 0, content.Length);
        int found = content.IndexOf(detectedText, searchFrom, StringComparison.Ordinal);

        return found >= 0 ? found : content.IndexOf(detectedText, StringComparison.Ordinal);
    }

    private static bool IsTextual(string? contentType)
        => contentType is not null
           && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("text", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How the text in a body of this type is spelled, which decides what a stand-in looks like
    /// coming back and how the real value must be written in its place -- see
    /// <see cref="BodySyntax"/>.
    ///
    /// An event stream counts as JSON. The framing around each frame is plain text, but the
    /// payload inside <c>data:</c> is JSON in every chat protocol this proxy has met, and the
    /// escaped spellings only ever match text that really was written by a JSON writer.
    ///
    /// Input:  "application/json"   -&gt; Json
    /// Input:  "text/event-stream"  -&gt; Json
    /// Input:  "text/html"          -&gt; Text
    /// </summary>
    private static BodySyntax SyntaxOf(string? contentType)
        => contentType is not null
           && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            ? BodySyntax.Json
            : BodySyntax.Text;

    /// <summary>The rewritten body text, the spans that were written, and how long the scan took --
    /// with what was found and not written: <paramref name="Suppressed"/> findings that were
    /// nested in another or could not be placed, and the near misses under the threshold.</summary>
    internal sealed record Anonymization(
        string Body,
        IReadOnlyList<TokenReplacement> Applied,
        double ScannedMs,
        int Suppressed,
        IReadOnlyList<NearMiss> NearMisses)
    {
        public static readonly Anonymization Empty = new(string.Empty, [], 0, 0, []);
    }

    private sealed record Findings(List<TokenReplacement> Replacements, IReadOnlyList<NearMiss> NearMisses, int Unplaced);

    /// <summary>
    /// One request and its response. It owns the stand-in map for that one exchange, which is
    /// what lets the response half put back exactly what the request half hid and nothing else.
    /// </summary>
    private sealed class Exchange(
        ReplacerService replacer,
        TokenAnonymizerService anonymizer,
        IExchangeObserver observer) : IExchangeBodyMutation
    {
        public async ValueTask<byte[]?> MutateRequestAsync(
            ReadOnlyMemory<byte> body,
            BodyDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            if (!IsTextual(descriptor.ContentType))
            {
                observer.Passthrough($"{descriptor.ContentType ?? "no content type"} not inspected");

                return null;
            }

            string content = descriptor.Encoding.GetString(body.Span);

            // The detection service rejects empty text, and a body with nothing in it has
            // nothing to hide either way.
            if (content.Length == 0)
            {
                observer.Detected([], DetectionStats.None);

                return null;
            }

            // Decoded once, here; the trace takes this text rather than decoding the bytes again.
            observer.RequestText(content);

            // Tried as JSON first, whatever the content type says -- chat backends post JSON
            // under all sorts of declarations -- and treated as text when it does not parse.
            Anonymization result = await replacer.AnonymizeJsonWithFindingsAsync(content, anonymizer, cancellationToken);

            observer.Detected(
                Entities(result.Applied),
                new DetectionStats(result.ScannedMs, result.Suppressed, result.NearMisses));

            // Unchanged is reported as such: every header the client sent still describes
            // these bytes, and the transformer keeps them only when told nothing moved.
            if (result.Applied.Count == 0)
                return null;

            observer.RewrittenText(result.Body);

            return descriptor.Encoding.GetBytes(result.Body);
        }

        public async ValueTask<byte[]?> MutateResponseAsync(
            ReadOnlyMemory<byte> body,
            BodyDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            if (!IsTextual(descriptor.ContentType))
                return null;

            string content = descriptor.Encoding.GetString(body.Span);

            if (content.Length == 0)
                return null;

            observer.ResponseText(content);

            (string restoredContent, int restored) = await anonymizer.DeanonymizeTokenAsync(
                content,
                cancellationToken,
                SyntaxOf(descriptor.ContentType));

            observer.Restored(restoredContent, restored);

            return restored == 0 ? null : descriptor.Encoding.GetBytes(restoredContent);
        }

        /// <summary>
        /// The restore for a body that arrives in pieces, or null when this client has hidden
        /// nothing on this host and every byte would only be copied through unchanged.
        ///
        /// "Nothing hidden" is read now rather than per chunk on purpose. The request half of
        /// this exchange has already run by the time a response reaches here, so the map is as
        /// full as this exchange is going to make it; a map that fills later belongs to a later
        /// exchange, which asks this question again.
        /// </summary>
        public IExchangeStreamMutation? CreateResponseStream(BodyDescriptor descriptor)
            => anonymizer.HasStandIns
                ? new StreamRestore(anonymizer, observer, SyntaxOf(descriptor.ContentType))
                : null;

        /// <summary>
        /// The restore, applied to an event stream as it arrives.
        ///
        /// It gives the same answer as the whole-body restore for any stand-in that arrives in
        /// one piece, wherever the chunk boundaries happen to fall: what could still be the
        /// start of one is held back and reconsidered with the next chunk -- see
        /// <see cref="TokenAnonymizerService.HoldBack"/>.
        ///
        /// What it cannot do is reassemble a stand-in the origin never sends in one piece. A
        /// chat backend streams its answer a token at a time, so a name in the *answer* arrives
        /// as several frames with JSON framing between them, and no amount of buffering here
        /// makes those one string; that would take a reader for each site's own delta format.
        /// The message the client echoes back -- which is the one the person reading the screen
        /// recognises as theirs -- arrives whole, and that is the one this puts right.
        /// </summary>
        private sealed class StreamRestore(
            TokenAnonymizerService anonymizer,
            IExchangeObserver observer,
            BodySyntax syntax) : IExchangeStreamMutation
        {
            private readonly StringBuilder _tokenized = new();

            private readonly StringBuilder _restored = new();

            private string _held = string.Empty;

            private int _count;

            public string Mutate(string chunk)
            {
                if (chunk.Length == 0)
                    return string.Empty;

                Record(_tokenized, chunk);

                string text = _held.Length == 0 ? chunk : _held + chunk;
                int hold = anonymizer.HoldBack(text, syntax);

                _held = hold == 0 ? string.Empty : text[^hold..];

                return hold == text.Length ? string.Empty : Restore(text[..^hold]);
            }

            public string Flush()
            {
                string tail = _held.Length == 0 ? string.Empty : Restore(_held);
                _held = string.Empty;

                // Before the restore is reported: the trace publishes what the origin sent and
                // what the client got together, and the first of the two is this.
                observer.ResponseText(_tokenized.ToString());
                observer.Restored(_restored.ToString(), _count);

                return tail;
            }

            private string Restore(string text)
            {
                (string restored, int count) = anonymizer.Deanonymize(text, syntax);
                _count += count;

                Record(_restored, restored);

                return restored;
            }

            /// <summary>
            /// Keeps what the dashboard shows, and no more of it. A stream can run for as long
            /// as someone keeps typing, and the event carrying it is capped anyway; growing a
            /// buffer past that cap would hold a conversation in memory to display its first
            /// page.
            /// </summary>
            private static void Record(StringBuilder buffer, string text)
            {
                int room = ExchangeTrace.MaxBodyChars - buffer.Length;

                if (room > 0)
                    buffer.Append(text, 0, Math.Min(room, text.Length));
            }
        }

        private static DetectedEntity[] Entities(IReadOnlyList<TokenReplacement> applied)
        {
            DetectedEntity[] entities = new DetectedEntity[applied.Count];

            for (int index = 0; index < applied.Count; index++)
            {
                TokenReplacement span = applied[index];

                entities[index] = new DetectedEntity(
                    $"e{index + 1}",
                    span.Token.Classification,
                    span.Token.Value,
                    span.AnonymizedValue,
                    span.Position,
                    span.Position + span.Length,
                    Math.Clamp(span.Score, 0, 1),
                    span.Facts.InformationType,
                    span.Facts.RiskLevel,
                    span.Facts.HipaaCategory);
            }

            return entities;
        }
    }
}
