namespace MagSid.Core.Model;

/// <summary>One token inside an ORDER_CHn: list.</summary>
public abstract record OrderToken;

/// <summary>A bare integer: play this pattern number next.</summary>
public sealed record PatternRefToken(int PatternNumber) : OrderToken;
