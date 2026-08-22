namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// 
/// </summary>
/// <param name="AnonymizedValue">New token value.</param>
/// <param name="Position">Position of the original token.</param>
/// <param name="Length">Length of the original token.</param>
/// <param name="PositionDelta">Delta to be applied to the position of subsequent tokens.</param>
public record TokenReplacement(string AnonymizedValue, int Position, int Length);
