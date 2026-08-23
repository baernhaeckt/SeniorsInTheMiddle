namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// One piece of detected personal data: the literal text found, and the entity type the
/// detector gave it (<c>PERSON</c>, <c>EMAIL_ADDRESS</c>, ...).
/// </summary>
public record Token(string Value, string Classification);
