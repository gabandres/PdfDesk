namespace InkvoltPdf.Core;

/// <summary>A rough strength hint for a new document password (length and variety, with common patterns marked down).</summary>
public static class PasswordStrength
{
    private static readonly string[] Common =
    [
        "password", "passw0rd", "123456", "12345678", "123456789", "qwerty", "abc123", "letmein", "welcome", "admin",
        "iloveyou", "monkey", "dragon", "111111", "000000", "secret", "pdf",
    ];

    /// <summary>0 (empty) to 4 (strong), with a word for it.</summary>
    public static (int Score, string Label) Rate(string password)
    {
        if (string.IsNullOrEmpty(password)) return (0, "");
        var lower = password.ToLowerInvariant();
        if (password.Length < 6 || Common.Any(c => lower == c) || password.Distinct().Count() <= 2) return (1, "Weak");

        var classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;

        // Long passphrases are strong even in one case; runs like "abcdef" or "123456" don't count for much.
        var score = password.Length >= 16 ? 4 : password.Length >= 12 ? 3 : password.Length >= 8 ? 2 : 1;
        if (classes >= 3 && password.Length >= 10) score++;
        if (classes == 1 && password.Length < 16) score--;
        if (Common.Any(c => c.Length >= 5 && lower.Contains(c)) || IsRun(lower)) score--;
        score = Math.Clamp(score, 1, 4);
        return (score, score switch { 1 => "Weak", 2 => "Fair", 3 => "Good", _ => "Strong" });
    }

    /// <summary>The whole password is one ascending or descending run ("abcdefgh", "98765432").</summary>
    private static bool IsRun(string s)
    {
        if (s.Length < 4) return false;
        var step = s[1] - s[0];
        if (Math.Abs(step) != 1) return false;
        for (var i = 2; i < s.Length; i++)
            if (s[i] - s[i - 1] != step) return false;
        return true;
    }
}
