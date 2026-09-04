// src/OpenEC.Monitor/Eni/EniXmlValues.cs
using System.Globalization;

namespace OpenEC.Monitor.Eni;

public static class EniXmlValues
{
    /// <summary>Parses an ENI numeric literal: decimal, or hex prefixed with '#x'.</summary>
    public static long? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex : null;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)
            ? dec : null;
    }
}
