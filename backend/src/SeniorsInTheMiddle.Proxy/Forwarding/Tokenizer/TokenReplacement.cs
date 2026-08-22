namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>One span of the body text and what goes in its place.</summary>
/// <param name="Token">The value found and its classification.</param>
/// <param name="AnonymizedValue">New token value.</param>
/// <param name="Position">Position of the original token, as a string index.</param>
/// <param name="Length">Length of the original token.</param>
/// <param name="Score">The analyzer's confidence, 0..1.</param>
public record TokenReplacement(Token Token, string AnonymizedValue, int Position, int Length, double Score);
