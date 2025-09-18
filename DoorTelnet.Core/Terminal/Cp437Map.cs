namespace DoorTelnet.Core.Terminal;

/// <summary>
/// Provides a CP437 to Unicode mapping for bytes 0-255.
/// Includes custom mappings for box drawing and block characters.
/// </summary>
public static class Cp437Map
{
    private static readonly char[] _map = Build();
    private static readonly char[] _asciiMap = BuildAsciiCompatible();

    // Set this to true if Unicode block characters don't display properly
    public static bool UseAsciiCompatibleMode { get; set; } = false;

    private static char[] Build()
    {
        var arr = new char[256];
        for (int i = 0; i < 256; i++) arr[i] = (char)i; // base Latin-1 fallback

        // Common CP437 overrides (faces, suits, arrows etc.)
        arr[0x01] = '?'; arr[0x02] = '?'; arr[0x03] = '?'; arr[0x04] = '?'; arr[0x05] = '?'; arr[0x06] = '?';
        arr[0x0E] = '?'; arr[0x0F] = '?'; arr[0x10] = '?'; arr[0x11] = '?'; arr[0x12] = '?'; arr[0x13] = '?'; arr[0x14] = '¶';
        arr[0x15] = '§'; arr[0x16] = '?'; arr[0x17] = '?'; arr[0x18] = '?'; arr[0x19] = '?'; arr[0x1A] = '?';
        // NOTE: 0x1B is ESC and must remain '\x1B' for ANSI sequences; do NOT override.
        arr[0x1C] = '?'; arr[0x1D] = '?'; arr[0x1E] = '?'; arr[0x1F] = '?'; arr[0x7F] = '?';

        // Shading / block - Use more console-compatible Unicode alternatives
        arr[0xB0] = '\u2591'; // Light shade ? 
        arr[0xB1] = '\u2592'; // Medium shade ?
        arr[0xB2] = '\u2593'; // Dark shade ?
        arr[0xDB] = '\u2588'; // Full block ?
        arr[0xDC] = '\u2584'; // Lower half block ?
        arr[0xDD] = '\u258C'; // Left half block ?
        arr[0xDE] = '\u2590'; // Right half block ?
        arr[0xDF] = '\u2580'; // Upper half block ?

        // Single/Double line box drawing and junctions (full BBS set)
        arr[0xB3] = '?'; arr[0xB4] = '?'; arr[0xB5] = '?'; arr[0xB6] = '?'; arr[0xB7] = '?'; arr[0xB8] = '?';
        arr[0xB9] = '?'; arr[0xBA] = '?'; arr[0xBB] = '?'; arr[0xBC] = '?'; arr[0xBD] = '?'; arr[0xBE] = '?';
        arr[0xBF] = '?'; arr[0xC0] = '?'; arr[0xC1] = '?'; arr[0xC2] = '?'; arr[0xC3] = '?'; arr[0xC4] = '?'; arr[0xC5] = '?';
        arr[0xC6] = '?'; arr[0xC7] = '?'; arr[0xC8] = '?'; arr[0xC9] = '?'; arr[0xCA] = '?'; arr[0xCB] = '?'; arr[0xCC] = '?';
        arr[0xCD] = '?'; arr[0xCE] = '?'; arr[0xCF] = '?'; arr[0xD0] = '?'; arr[0xD1] = '?'; arr[0xD2] = '?';
        arr[0xD3] = '?'; arr[0xD4] = '?'; arr[0xD5] = '?'; arr[0xD6] = '?'; arr[0xD7] = '?'; arr[0xD8] = '?'; arr[0xD9] = '?'; arr[0xDA] = '?';

        return arr;
    }

    private static char[] BuildAsciiCompatible()
    {
        var arr = new char[256];
        for (int i = 0; i < 256; i++) arr[i] = (char)i; // base Latin-1 fallback

        // Common CP437 overrides - simplified ASCII versions
        arr[0x01] = ':'; arr[0x02] = ':'; arr[0x03] = '<'; arr[0x04] = '<'; arr[0x05] = '+'; arr[0x06] = '+';
        arr[0x0E] = '~'; arr[0x0F] = '*'; arr[0x10] = '>'; arr[0x11] = '<'; arr[0x12] = '|'; arr[0x13] = '!'; arr[0x14] = '¶';
        arr[0x15] = '§'; arr[0x16] = '-'; arr[0x17] = '|'; arr[0x18] = '^'; arr[0x19] = 'v'; arr[0x1A] = '>';
        // NOTE: 0x1B is ESC and must remain '\x1B' for ANSI sequences; do NOT override.
        arr[0x1C] = '+'; arr[0x1D] = '-'; arr[0x1E] = '^'; arr[0x1F] = 'v'; arr[0x7F] = 'A';

        // Shading / block - ASCII compatible alternatives
        arr[0xB0] = '.'; // Light shade
        arr[0xB1] = ':'; // Medium shade  
        arr[0xB2] = '#'; // Dark shade
        arr[0xDB] = '#'; // Full block
        arr[0xDC] = '='; // Lower half block
        arr[0xDD] = '['; // Left half block
        arr[0xDE] = ']'; // Right half block
        arr[0xDF] = '-'; // Upper half block

        // Single/Double line box drawing - ASCII alternatives
        arr[0xB3] = '|'; arr[0xB4] = '+'; arr[0xB5] = '+'; arr[0xB6] = '+'; arr[0xB7] = '+'; arr[0xB8] = '+';
        arr[0xB9] = '+'; arr[0xBA] = '|'; arr[0xBB] = '+'; arr[0xBC] = '+'; arr[0xBD] = '+'; arr[0xBE] = '+';
        arr[0xBF] = '+'; arr[0xC0] = '+'; arr[0xC1] = '+'; arr[0xC2] = '+'; arr[0xC3] = '+'; arr[0xC4] = '-'; arr[0xC5] = '+';
        arr[0xC6] = '+'; arr[0xC7] = '+'; arr[0xC8] = '+'; arr[0xC9] = '+'; arr[0xCA] = '+'; arr[0xCB] = '+'; arr[0xCC] = '+';
        arr[0xCD] = '='; arr[0xCE] = '+'; arr[0xCF] = '+'; arr[0xD0] = '+'; arr[0xD1] = '+'; arr[0xD2] = '+';
        arr[0xD3] = '+'; arr[0xD4] = '+'; arr[0xD5] = '+'; arr[0xD6] = '+'; arr[0xD7] = '+'; arr[0xD8] = '+'; arr[0xD9] = '+'; arr[0xDA] = '+';

        return arr;
    }

    /// <summary>
    /// Converts a CP437 byte to a Unicode char.
    /// </summary>
    public static char ToChar(byte b) => UseAsciiCompatibleMode ? _asciiMap[b] : _map[b];
}
