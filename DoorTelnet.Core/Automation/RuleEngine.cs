using System.Text.RegularExpressions;

namespace DoorTelnet.Core.Automation;

/// <summary>
/// Simple regex rule engine. Allows registering callbacks invoked when patterns match the current screen text.
/// </summary>
public class RuleEngine
{
    private readonly List<Rule> _rules = new();

    public void Add(string pattern, Action<Match> callback)
    {
        var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.Multiline);
        _rules.Add(new Rule(regex, callback));
    }

    public void Evaluate(string screenText)
    {
        foreach (var r in _rules)
        {
            var m = r.Regex.Match(screenText);
            if (m.Success)
            {
                try { r.Callback(m); } catch { /* swallow */ }
            }
        }
    }

    private record Rule(Regex Regex, Action<Match> Callback);
}
