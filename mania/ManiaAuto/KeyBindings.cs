using System.Globalization;

namespace LocalManiaAuto;

internal sealed record VirtualKeySpec(string Name, ushort VirtualKey);

internal static class KeyBindings
{
    private static readonly IReadOnlyDictionary<string, ushort> NamedKeys =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["BACKSPACE"] = 0x08,
            ["TAB"] = 0x09,
            ["ENTER"] = 0x0D,
            ["RETURN"] = 0x0D,
            ["SPACE"] = 0x20,
            ["SPACEBAR"] = 0x20,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22,
            ["END"] = 0x23,
            ["HOME"] = 0x24,
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,
            ["INSERT"] = 0x2D,
            ["DELETE"] = 0x2E,
            ["LSHIFT"] = 0xA0,
            ["RSHIFT"] = 0xA1,
            ["LCTRL"] = 0xA2,
            ["RCTRL"] = 0xA3,
            ["LALT"] = 0xA4,
            ["RALT"] = 0xA5,
            ["SEMICOLON"] = 0xBA,
            ["OEM1"] = 0xBA,
            ["PLUS"] = 0xBB,
            ["EQUALS"] = 0xBB,
            ["COMMA"] = 0xBC,
            ["MINUS"] = 0xBD,
            ["PERIOD"] = 0xBE,
            ["DOT"] = 0xBE,
            ["SLASH"] = 0xBF,
            ["BACKTICK"] = 0xC0,
            ["LBRACKET"] = 0xDB,
            ["BACKSLASH"] = 0xDC,
            ["RBRACKET"] = 0xDD,
            ["QUOTE"] = 0xDE,
        };

    private static readonly IReadOnlyDictionary<int, string> DefaultLayouts =
        new Dictionary<int, string>
        {
            [1] = "SPACE",
            [2] = "F,J",
            [3] = "F,SPACE,J",
            [4] = "D,F,J,K",
            [5] = "D,F,SPACE,J,K",
            [6] = "S,D,F,J,K,L",
            [7] = "S,D,F,SPACE,J,K,L",
            [8] = "A,S,D,F,J,K,L,SEMICOLON",
            [9] = "A,S,D,F,SPACE,J,K,L,SEMICOLON",
        };

    public static string? GetDefaultLayout(int keyCount)
        => DefaultLayouts.TryGetValue(keyCount, out string? layout) ? layout : null;

    public static IReadOnlyList<VirtualKeySpec> Parse(string text, int expectedCount)
    {
        string[] tokens = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != expectedCount)
        {
            throw new ArgumentException($"--keys supplied {tokens.Length} keys, but the beatmap is {expectedCount}K.", nameof(text));
        }

        var result = new List<VirtualKeySpec>(tokens.Length);
        var seen = new HashSet<ushort>();
        foreach (string token in tokens)
        {
            VirtualKeySpec key = ParseOne(token);
            if (!seen.Add(key.VirtualKey))
            {
                throw new ArgumentException($"--keys cannot repeat {key.Name}.", nameof(text));
            }
            result.Add(key);
        }
        return result;
    }

    private static VirtualKeySpec ParseOne(string token)
    {
        string normalized = token.Trim().ToUpperInvariant();
        if (normalized.Length == 1)
        {
            char character = normalized[0];
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return new VirtualKeySpec(normalized, character);
            }

            if (TryParsePunctuation(character, out ushort punctuationKey))
            {
                return new VirtualKeySpec(normalized, punctuationKey);
            }
        }

        if (normalized.StartsWith("NUMPAD", StringComparison.Ordinal)
            && normalized.Length == 7
            && normalized[6] is >= '0' and <= '9')
        {
            return new VirtualKeySpec(normalized, checked((ushort)(0x60 + normalized[6] - '0')));
        }

        if (normalized.StartsWith('F')
            && int.TryParse(normalized[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            return new VirtualKeySpec(normalized, checked((ushort)(0x70 + functionNumber - 1)));
        }

        if (NamedKeys.TryGetValue(normalized, out ushort namedKey))
        {
            return new VirtualKeySpec(normalized, namedKey);
        }

        string hex = normalized.StartsWith("0X", StringComparison.Ordinal) ? normalized[2..] : string.Empty;
        if (hex.Length > 0
            && ushort.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort rawKey)
            && rawKey is > 0 and < 0xFF)
        {
            return new VirtualKeySpec($"0x{rawKey:X2}", rawKey);
        }

        throw new ArgumentException(
            $"Unknown key '{token}'. Use A-Z, 0-9, F1-F24, NUMPAD0-9, SPACE, an arrow key, or a 0xNN virtual-key code.",
            nameof(token));
    }

    private static bool TryParsePunctuation(char character, out ushort virtualKey)
    {
        virtualKey = character switch
        {
            ';' => 0xBA,
            '=' => 0xBB,
            ',' => 0xBC,
            '-' => 0xBD,
            '.' => 0xBE,
            '/' => 0xBF,
            '`' => 0xC0,
            '[' => 0xDB,
            '\\' => 0xDC,
            ']' => 0xDD,
            '\'' => 0xDE,
            _ => 0,
        };
        return virtualKey != 0;
    }
}
