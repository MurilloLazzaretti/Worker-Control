using System.Text;
using System.Text.RegularExpressions;

namespace WorkerControl.Service.Database;

/// <summary>
/// What a package asks to be done to the database: define an object from its script (creating
/// it, or altering it when it is already there), drop one, or run a script as it is.
/// </summary>
public sealed record ApplyRequest(string Package, int Item, string Action, string? Kind, string? Variety, string? Schema, string? Name, string? Script);

/// <summary>
/// How it went. <c>Did</c> is one of created, altered, dropped, absent (there was nothing to
/// drop) and ran.
/// </summary>
public sealed record ApplyResult(bool Ok, string? Did, string? Error, int? Batch, int? Line, IReadOnlyList<string> Messages);

/// <summary>
/// The one way anything is ever written to a database from here: the script of one item of a
/// package somebody approved. Everything else reads, and is held to it by a test.
/// </summary>
public interface IDatabaseWriter
{
    Task<ApplyResult> ApplyAsync(DatabaseConnection connection, string database, ApplyRequest request, CancellationToken stopping);
}

/// <summary>
/// The text work of applying a script, apart from the instance so that it can be tried without one.
/// </summary>
internal static partial class SqlApply
{
    /// <summary>
    /// The batches of a script: what is between lines that say only GO. A GO inside a comment
    /// or a text is part of it.
    /// </summary>
    public static List<string> Batches(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var inComment = false;
        var inText = false;

        foreach (var line in script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (!inComment && !inText && Go().IsMatch(line))
            {
                Add();
                continue;
            }
            current.Append(line).Append('\n');

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (inComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inComment = false;
                        i++;
                    }
                }
                else if (inText)
                {
                    if (c == '\'')
                        inText = false;
                }
                else if (c == '-' && next == '-')
                    break;
                else if (c == '/' && next == '*')
                {
                    inComment = true;
                    i++;
                }
                else if (c == '\'')
                    inText = true;
            }
        }
        Add();
        return batches;

        void Add()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0)
                batches.Add(text);
            current.Clear();
        }
    }

    /// <summary>
    /// The script of a view, procedure, function or trigger made to create or to alter,
    /// whichever it said. Null when the first statement is neither.
    /// </summary>
    public static string? AsCreate(string script, bool alter)
    {
        // Looked for with the comments blanked out, at the same places, so that a "create" in one is not taken.
        var bare = Comments().Replace(script, match => new string(' ', match.Length));
        var match = Header().Match(bare);
        if (!match.Success || bare[..match.Index].Trim().Length > 0)
            return null;
        var verb = match.Groups["verb"];
        return script[..verb.Index] + (alter ? "ALTER" : "CREATE") + script[(verb.Index + verb.Length)..];
    }

    public static string Drop(string kind, string? variety, string schema, string name)
    {
        var what = kind switch
        {
            "Table" => "TABLE",
            "View" => "VIEW",
            "Procedure" => "PROCEDURE",
            "Function" => "FUNCTION",
            "Type" => "TYPE",
            _ => throw new ArgumentException($"Nothing of kind {kind} can be dropped")
        };
        return $"DROP {what} {SqlScript.Name(schema, name)};";
    }

    [GeneratedRegex(@"^\s*GO\s*(\d+\s*)?(--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Go();

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"(?<verb>CREATE(\s+OR\s+ALTER)?|ALTER)\s+(PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Header();
}
