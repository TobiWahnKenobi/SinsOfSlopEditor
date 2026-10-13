using System.Globalization;

namespace SinsEditor.Core.Documents;

public enum SinsValueKind
{
    None,
    String,
    Integer,
    Float,
    Bool,
    Vector,
    /// <summary>Anything else, e.g. <c>6fffffff</c> or <c>= "Weapon"</c>.</summary>
    Other,
}

/// <summary>Helpers for reading and formatting raw value text.</summary>
public static class SinsValue
{
    public static SinsValueKind Classify(string raw)
    {
        if (raw.Length == 0) return SinsValueKind.None;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' && raw.IndexOf('"', 1) == raw.Length - 1)
            return SinsValueKind.String;
        if (raw == "TRUE" || raw == "FALSE") return SinsValueKind.Bool;
        if (raw[0] == '[') return SinsValueKind.Vector;
        if (IsInteger(raw)) return SinsValueKind.Integer;
        if (IsFloat(raw)) return SinsValueKind.Float;
        return SinsValueKind.Other;
    }

    public static bool TryGetString(string raw, out string value)
    {
        if (Classify(raw) == SinsValueKind.String)
        {
            value = raw[1..^1];
            return true;
        }
        value = "";
        return false;
    }

    public static bool TryGetInt(string raw, out int value) =>
        int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    public static bool TryGetDouble(string raw, out double value) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static bool TryGetBool(string raw, out bool value)
    {
        value = raw == "TRUE";
        return raw is "TRUE" or "FALSE";
    }

    /// <summary>Parses <c>[ 4 , 0 ]</c> style vectors into their numeric components.</summary>
    public static bool TryGetVector(string raw, out double[] values)
    {
        values = [];
        if (raw.Length < 2 || raw[0] != '[' || raw[^1] != ']') return false;
        string inner = raw[1..^1];
        if (inner.Trim().Length == 0) return true;
        // Entities separate components with commas ([ 1 , 2 ]), meshes with spaces only ([ 1 2 3 ]).
        var parts = inner.Contains(',') ? inner.Split(',') : inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!TryGetDouble(parts[i].Trim(), out result[i])) return false;
        }
        values = result;
        return true;
    }

    public static string FormatString(string value) => "\"" + value + "\"";

    public static string FormatBool(bool value) => value ? "TRUE" : "FALSE";

    public static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Six decimals, matching the files the game's tools write.</summary>
    public static string FormatFloat(double value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    private static bool IsInteger(string s)
    {
        int i = s[0] == '-' ? 1 : 0;
        if (i >= s.Length) return false;
        for (; i < s.Length; i++)
        {
            if (!char.IsAsciiDigit(s[i])) return false;
        }
        return true;
    }

    private static bool IsFloat(string s)
    {
        int i = s[0] == '-' ? 1 : 0;
        bool digitsBefore = false, digitsAfter = false;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digitsBefore = true; }
        if (i >= s.Length || s[i] != '.') return false;
        i++;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digitsAfter = true; }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            if (i == start) return false;
        }
        return i == s.Length && (digitsBefore || digitsAfter);
    }
}
