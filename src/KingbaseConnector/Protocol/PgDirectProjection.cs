using System.Text;

namespace KingbaseConnector.Protocol;

// Proves a direct projection; it never rewrites SQL or guesses cardinality for a complex query.
internal sealed class PgDirectProjection(string[] relation, bool only, (string Name, bool Qualified)[] sorts)
{
    internal bool Only { get; } = only;
    internal bool Matches(string catalog, string schema, string table, IEnumerable<string>? baseNames = null, IEnumerable<string>? aliases = null)
    {
        var columns = new HashSet<string>(baseNames ?? [], StringComparer.Ordinal);
        var outputs = new HashSet<string>(aliases ?? [], StringComparer.Ordinal);
        return sorts.All(sort => columns.Contains(sort.Name) || (!sort.Qualified && outputs.Contains(sort.Name))) && (relation.Length switch
        {
            1 => relation[0] == table,
            2 => relation[0] == schema && relation[1] == table,
            3 => relation[0] == catalog && relation[1] == schema && relation[2] == table,
            _ => false
        });
    }
    readonly struct Token(string text, bool identifier, bool quoted = false)
    {
        internal string Text { get; } = text;
        internal bool Identifier { get; } = identifier;
        internal bool Keyword(string value) => !quoted && Identifier && Text == value;
    }
    internal static PgDirectProjection? Parse(string sql)
    {
        var tokens = Lex(sql);
        if (tokens is null || tokens.Count == 0 || !tokens[0].Keyword("select")) return null;
        var index = 1;
        if (index < tokens.Count && (tokens[index].Keyword("all") || tokens[index].Keyword("distinct"))) index++;
        while (true)
        {
            if (index == tokens.Count || !(tokens[index].Identifier || tokens[index].Text == "*")) return null;
            index++;
            while (index < tokens.Count && tokens[index].Text == ".")
            {
                if (++index == tokens.Count || !(tokens[index].Identifier || tokens[index].Text == "*")) return null;
                index++;
            }
            if (index < tokens.Count && tokens[index].Keyword("as"))
            { if (++index == tokens.Count || !tokens[index++].Identifier) return null; }
            else if (index < tokens.Count && tokens[index].Identifier && !tokens[index].Keyword("from")) index++;
            if (index < tokens.Count && tokens[index].Text == ",") { index++; continue; }
            break;
        }
        if (index == tokens.Count || !tokens[index++].Keyword("from")) return null;
        var only = index < tokens.Count && tokens[index].Keyword("only");
        if (only) index++;
        var names = new List<string>();
        while (true)
        {
            if (index == tokens.Count || !tokens[index].Identifier || names.Count == 3) return null;
            names.Add(tokens[index++].Text);
            if (index == tokens.Count || tokens[index].Text != ".") break;
            index++;
        }
        if (index < tokens.Count && tokens[index].Text == "*") { if (only) return null; index++; }
        var clauses = new[] { "where", "order", "limit", "offset", "fetch", "for" };
        var alias = names[names.Count - 1];
        if (index < tokens.Count && tokens[index].Keyword("as"))
        { if (++index == tokens.Count || !tokens[index].Identifier) return null; alias = tokens[index++].Text; }
        else if (index < tokens.Count && tokens[index].Identifier && !clauses.Any(tokens[index].Keyword)) alias = tokens[index++].Text;
        if (index < tokens.Count && tokens[index].Text != ";" && !clauses.Any(tokens[index].Keyword)) return null;
        var order = false;
        var sorts = new List<(string, bool)>();
        var unsafeWords = new[] { "join", "apply", "union", "intersect", "except", "group", "having", "window", "lateral", "tablesample", "select", "from", "with", "values", "returning", "into" };
        for (; index < tokens.Count; index++)
        {
            if (tokens[index].Keyword("order"))
            {
                order = true;
                if (++index == tokens.Count || !tokens[index++].Keyword("by")) return null;
                while (true)
                {
                    if (index == tokens.Count) return null;
                    if (tokens[index].Identifier)
                    {
                        var name = tokens[index++].Text; var qualified = false;
                        if (index < tokens.Count && tokens[index].Text == ".")
                        {
                            if (name != alias || ++index == tokens.Count || !tokens[index].Identifier) return null;
                            qualified = true; name = tokens[index++].Text;
                        }
                        sorts.Add((name, qualified));
                    }
                    else if (tokens[index].Text.Length == 1 && tokens[index].Text[0] is >= '1' and <= '9') index++;
                    else return null;
                    if (index < tokens.Count && (tokens[index].Keyword("asc") || tokens[index].Keyword("desc"))) index++;
                    if (index < tokens.Count && tokens[index].Keyword("nulls"))
                    { if (++index == tokens.Count || !(tokens[index].Keyword("first") || tokens[index].Keyword("last"))) return null; index++; }
                    if (index < tokens.Count && tokens[index].Text == ",") { index++; continue; }
                    break;
                }
                if (index < tokens.Count && tokens[index].Text != ";" &&
                    !new[] { "limit", "offset", "fetch", "for" }.Any(tokens[index].Keyword)) return null;
                index--;
            }
            if (tokens[index].Text is "(" or ")" || (tokens[index].Text == "," && !order) || unsafeWords.Any(tokens[index].Keyword)) return null;
            if (tokens[index].Text == ";" && index != tokens.Count - 1) return null;
        }
        return new PgDirectProjection(names.ToArray(), only, sorts.ToArray());
    }

    static List<Token>? Lex(string sql)
    {
        var result = new List<Token>();
        for (var i = 0; i < sql.Length;)
        {
            if (result.Count == 4096) return null;
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            { i += 2; while (i < sql.Length && sql[i] is not ('\r' or '\n')) i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2; var depth = 1;
                while (i < sql.Length && depth != 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0) return null;
                continue;
            }
            if (c is '\'' or '"')
            {
                i++; var value = new StringBuilder(); var closed = false;
                while (i < sql.Length)
                {
                    var next = sql[i++];
                    if (next == '\\') return null;
                    if (next != c) { value.Append(next); continue; }
                    if (i < sql.Length && sql[i] == c) { value.Append(c); i++; continue; }
                    closed = true; break;
                }
                if (!closed) return null;
                result.Add(new Token(c == '"' ? value.ToString() : "#", c == '"', c == '"')); continue;
            }
            if (char.IsLetter(c) || c == '_' || c >= 128)
            {
                var start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' || sql[i] >= 128)) i++;
                var value = sql.Substring(start, i - start);
                if (value.Any(ch => ch >= 128)) return null;
                result.Add(new Token(value.ToLowerInvariant(), true)); continue;
            }
            if (c == '$' && (i + 1 == sql.Length || !char.IsDigit(sql[i + 1]))) return null;
            result.Add(new Token(c.ToString(), false)); i++;
        }
        return result;
    }
}
