using System.Text.RegularExpressions;

namespace NpcSoulEngine.Functions.Services;

public interface IPromptInjectionGuard
{
    bool IsSafe(string utterance, out string violationReason);
    string Sanitize(string utterance);
}

public sealed class PromptInjectionGuard : IPromptInjectionGuard
{
    private const int MaxUtteranceLength = 500;

    private static readonly (Regex Pattern, string Reason)[] Patterns =
    [
        (Compile(@"\bignore\s+(all\s+|previous\s+|prior\s+)?instructions?\b"),       "instruction override"),
        (Compile(@"\bforget\s+(everything|all|your\s+(previous|prior|system))\b"),   "memory wipe attempt"),
        (Compile(@"\byou\s+are\s+now\b"),                                             "persona override"),
        (Compile(@"\bpretend\s+(you\s+are|to\s+be)\b"),                              "persona override"),
        (Compile(@"\bact\s+as\b"),                                                    "persona override"),
        (Compile(@"\bsystem\s+prompt\b"),                                             "system probe"),
        (Compile(@"\breveal\s+(your|the)\s+(instructions?|prompt|system)\b"),        "system probe"),
        (Compile(@"\bjailbreak\b"),                                                   "jailbreak keyword"),
        (new Regex(@"\bDAN\b",          RegexOptions.Compiled),                       "jailbreak keyword"),
        (new Regex(@"<\|(?:im_start|im_end|system|endoftext)\|>", RegexOptions.Compiled), "token injection"),
        (new Regex(@"\{\{.*?\}\}",      RegexOptions.Compiled | RegexOptions.Singleline), "template injection"),
        (Compile(@"\bdisregard\s+(all\s+|your\s+)?previous\b"),                      "instruction override"),
        (Compile(@"\bnew\s+(persona|personality|character|role)\b"),                  "persona override"),
    ];

    public bool IsSafe(string utterance, out string violationReason)
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            violationReason = string.Empty;
            return true;
        }

        var normalised = NormaliseUnicode(utterance);

        foreach (var (pattern, reason) in Patterns)
        {
            if (pattern.IsMatch(normalised))
            {
                violationReason = reason;
                return false;
            }
        }

        violationReason = string.Empty;
        return true;
    }

    public string Sanitize(string utterance)
    {
        var truncated = utterance.Length > MaxUtteranceLength
            ? utterance[..MaxUtteranceLength]
            : utterance;

        var clean = new System.Text.StringBuilder(truncated.Length);
        foreach (var ch in truncated)
        {
            // Strip ASCII control chars except tab, LF, CR
            if (ch >= 0x20 || ch == '\t' || ch == '\n' || ch == '\r')
                clean.Append(ch);
        }
        return clean.ToString().Trim();
    }

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string NormaliseUnicode(string input)
    {
        var normalized = input.Normalize(System.Text.NormalizationForm.FormC);
        return normalized
            .Replace('’', '\'')  // right single quote
            .Replace('“', '"')   // left double quote
            .Replace('”', '"')   // right double quote
            .Replace('ı', 'i')   // dotless i
            .Replace('а', 'a')   // Cyrillic а
            .Replace('е', 'e')   // Cyrillic е
            .Replace('о', 'o')   // Cyrillic о
            .Replace('р', 'r')   // Cyrillic р
            .Replace('с', 'c')   // Cyrillic с
            .Replace('х', 'x');  // Cyrillic х
    }
}
