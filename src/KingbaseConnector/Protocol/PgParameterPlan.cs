using System.Globalization;
using System.Text;

namespace KingbaseConnector.Protocol;

internal sealed class PgParameterPlan
{
    const int MaxBytes = 1024 * 1024;
    const int MaxParameters = 1024;

    PgParameterPlan(string sql, List<int> indexes, bool endsTransaction)
    {
        Sql = sql;
        ParameterIndexes = indexes.AsReadOnly();
        EndsTransaction = endsTransaction;
    }

    internal string Sql { get; }
    internal IReadOnlyList<int> ParameterIndexes { get; }
    internal bool EndsTransaction { get; }

    internal static PgParameterPlan Create(string sql, IReadOnlyList<string?> parameterNames,
        KingbaseCompatibilityMode mode, bool? standardConformingStrings)
    {
        if (sql is null) throw new ArgumentNullException(nameof(sql));
        if (parameterNames is null) throw new ArgumentNullException(nameof(parameterNames));
        if (mode is not (KingbaseCompatibilityMode.PostgreSql or KingbaseCompatibilityMode.Oracle or KingbaseCompatibilityMode.MySql or KingbaseCompatibilityMode.SqlServer))
            throw new NotSupportedException("PG parameter planning requires a confirmed compatibility mode.");
        if (standardConformingStrings is null)
            throw new InvalidOperationException("PG parameter planning requires confirmed standard_conforming_strings.");
        if (sql.Length >= MaxBytes || PgFrontendMessages.CStringLength(sql) > MaxBytes || parameterNames.Count > MaxParameters)
            throw InvalidPlan();
        var names = parameterNames.Take(MaxParameters + 1).ToArray();
        if (names.Length > MaxParameters) throw InvalidPlan();
        var indexes = new List<int>();
        var used = new bool[names.Length];
        Dictionary<string, int>? named = null;
        var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var native = false;
        var terminated = false;
        bool? previousStringEscapes = null;
        var gapNewline = false;
        var output = new StringBuilder();
        var copied = 0;
        var leadingWords = new List<string>(5);
        var leading = true;
        for (var i = 0; i < sql.Length;)
        {
            var c = sql[i];
            if (c is ' ' or '\t' or '\r' or '\n' or '\f' or '\v')
            {
                gapNewline |= c is '\r' or '\n';
                i++;
                continue;
            }
            if (c == '-' && At(sql, i + 1, '-'))
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\r' && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && At(sql, i + 1, '*'))
            {
                if (mode == KingbaseCompatibilityMode.MySql && (At(sql, i + 2, '!') || (i + 3 < sql.Length && sql[i + 2] is 'm' or 'M' && sql[i + 3] == '!')))
                    throw InvalidPlan();
                i = SkipComment(sql, i);
                // PG quote continuation admits line comments, but not block comments.
                previousStringEscapes = null;
                gapNewline = false;
                continue;
            }
            if (mode == KingbaseCompatibilityMode.MySql && c == '#')
            {
                while (i < sql.Length && sql[i] is not ('\r' or '\n')) i++;
                continue;
            }
            if (terminated) throw InvalidPlan();
            if (leading && !IdentifierStart(c)) leading = false;
            if (c == ';')
            {
                terminated = true;
                i++;
                continue;
            }
            if (c == '\'')
            {
                var escapes = previousStringEscapes.HasValue && gapNewline ? previousStringEscapes.Value : !standardConformingStrings.Value;
                i = mode == KingbaseCompatibilityMode.MySql ? SkipMysqlQuote(sql, i, '\'') : SkipQuote(sql, i, '\'', escapes);
                previousStringEscapes = escapes;
                gapNewline = false;
                continue;
            }
            previousStringEscapes = null;
            gapNewline = false;
            if (mode == KingbaseCompatibilityMode.Oracle &&
                ((c is 'q' or 'Q' && At(sql, i + 1, '\'')) ||
                 (c is 'n' or 'N' && i + 2 < sql.Length && sql[i + 1] is 'q' or 'Q' && sql[i + 2] == '\'')))
            {
                i = SkipOracleQuote(sql, i + (c is 'q' or 'Q' ? 1 : 2));
                continue;
            }
            if (mode == KingbaseCompatibilityMode.SqlServer && c == '[') { i = SkipQuote(sql, i, ']', false); continue; }
            if (mode == KingbaseCompatibilityMode.MySql && c == '`') { i = SkipMysqlQuote(sql, i, '`'); continue; }
            if (mode == KingbaseCompatibilityMode.MySql && c == '"') { i = SkipMysqlQuote(sql, i, '"'); continue; }
            if (c == '"') { i = SkipQuote(sql, i, '"', false); continue; }
            if ((c is 'E' or 'e' or 'B' or 'b' or 'X' or 'x' or 'N' or 'n') && At(sql, i + 1, '\''))
            {
                var escapes = c is 'E' or 'e' || (c is 'N' or 'n' && !standardConformingStrings.Value);
                i = mode == KingbaseCompatibilityMode.MySql ? SkipMysqlQuote(sql, i + 1, '\'') : SkipQuote(sql, i + 1, '\'', escapes);
                previousStringEscapes = escapes;
                continue;
            }
            if (c is 'U' or 'u' && At(sql, i + 1, '&') && i + 2 < sql.Length && sql[i + 2] is '\'' or '"')
            {
                var quote = sql[i + 2];
                i = mode == KingbaseCompatibilityMode.MySql ? SkipMysqlQuote(sql, i + 2, quote) : SkipQuote(sql, i + 2, quote, false);
                if (quote == '\'') previousStringEscapes = false;
                continue;
            }
            if (IdentifierStart(c))
            {
                var start = i;
                do { i++; } while (i < sql.Length && IdentifierPart(sql[i]));
                if (leading && leadingWords.Count < 5) leadingWords.Add(sql.Substring(start, i - start).ToUpperInvariant());
                continue;
            }
            if (c == '$')
            {
                if (i + 1 < sql.Length && Digit(sql[i + 1]))
                {
                    if (named is not null || sql[i + 1] == '0') throw InvalidPlan();
                    native = true;
                    var position = 0;
                    i++;
                    do
                    {
                        position = position * 10 + sql[i++] - '0';
                        if (position > MaxParameters) throw InvalidPlan();
                    } while (i < sql.Length && Digit(sql[i]));
                    if (position > names.Length || (i < sql.Length && IdentifierPart(sql[i]))) throw InvalidPlan();
                    used[position - 1] = true;
                    continue;
                }
                var end = i + 1;
                if (end < sql.Length && IdentifierStart(sql[end]))
                    do { end++; } while (end < sql.Length && (IdentifierStart(sql[end]) || Digit(sql[end])));
                if (At(sql, end, '$'))
                {
                    var delimiter = sql.Substring(i, end - i + 1);
                    var closing = sql.IndexOf(delimiter, end + 1, StringComparison.Ordinal);
                    if (closing < 0) throw InvalidPlan();
                    i = closing + delimiter.Length;
                    continue;
                }
            }
            if (c == ':' && (At(sql, i + 1, ':') || At(sql, i + 1, '=')))
            {
                i += 2;
                continue;
            }
            if (mode == KingbaseCompatibilityMode.MySql && c == '@' && i + 1 < sql.Length && sql[i + 1] is '`' or '\'' or '"')
            {
                i = SkipMysqlQuote(sql, i + 1, sql[i + 1]);
                continue;
            }
            if ((c == '<' && At(sql, i + 1, '@')) || (c == '@' && i + 1 < sql.Length && Operator(sql[i + 1], mode)))
            {
                i++;
                while (i < sql.Length && Operator(sql[i], mode))
                {
                    if ((sql[i] == '/' && At(sql, i + 1, '*')) || (sql[i] == '-' && At(sql, i + 1, '-'))) break;
                    i++;
                }
                continue;
            }
            if (c is '@' or ':' && i + 1 < sql.Length && NameStart(sql[i + 1]))
            {
                if (native) throw InvalidPlan();
                named ??= IndexNames(names);
                var start = i++;
                do { i++; } while (i < sql.Length && NamePart(sql[i]));
                if (i < sql.Length && IdentifierPart(sql[i])) throw InvalidPlan();
                var name = sql.Substring(start + 1, i - start - 1);
                if (name.Length > 128 || !named.TryGetValue(name, out var index)) throw InvalidPlan();
                if (!slots.TryGetValue(name, out var slot))
                {
                    slot = indexes.Count + 1;
                    slots.Add(name, slot);
                    indexes.Add(index);
                    used[index] = true;
                }
                output.Append(sql, copied, start - copied).Append('$').Append(slot.ToString(CultureInfo.InvariantCulture));
                if (output.Length >= MaxBytes) throw InvalidPlan();
                copied = i;
                continue;
            }
            i++;
        }
        if (used.Any(value => !value)) throw InvalidPlan();
        if (native) indexes.AddRange(Enumerable.Range(0, names.Length));
        output.Append(sql, copied, sql.Length - copied);
        var planned = output.ToString();
        if (planned.Length >= MaxBytes || PgFrontendMessages.CStringLength(planned) > MaxBytes) throw InvalidPlan();
        var controlWords = leadingWords.Where(word => word is not ("WORK" or "TRANSACTION")).ToArray();
        var endsTransaction = controlWords.Length == 3 && controlWords[0] is "COMMIT" or "END" or "ABORT" or "ROLLBACK" &&
            controlWords[1] == "AND" && controlWords[2] == "CHAIN";
        if (endsTransaction && mode != KingbaseCompatibilityMode.PostgreSql)
            throw new NotSupportedException("Transaction chaining for this compatibility mode is not implemented.");
        return new PgParameterPlan(planned, indexes, endsTransaction);
    }

    static Dictionary<string, int> IndexNames(string?[] names)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            if (string.IsNullOrEmpty(name)) throw InvalidPlan();
            if (name![0] is ':' or '@') name = name.Substring(1);
            if (name.Length == 0 || name.Length > 128 || !NameStart(name[0]) || name.Any(c => !NamePart(c)) || result.ContainsKey(name))
                throw InvalidPlan();
            result.Add(name, i);
        }
        return result;
    }

    static int SkipQuote(string sql, int start, char quote, bool escapes)
    {
        for (var i = start + 1; i < sql.Length; i++)
        {
            if (escapes && sql[i] == '\\') { i++; continue; }
            if (sql[i] != quote) continue;
            if (At(sql, i + 1, quote)) { i++; continue; }
            return i + 1;
        }
        throw InvalidPlan();
    }

    static int SkipOracleQuote(string sql, int quote)
    {
        if (quote + 1 >= sql.Length) throw InvalidPlan();
        var delimiter = sql[quote + 1];
        if (char.IsWhiteSpace(delimiter) || char.IsControl(delimiter) || char.IsSurrogate(delimiter) || delimiter == '\'') throw InvalidPlan();
        var closing = delimiter switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => delimiter };
        for (var i = quote + 2; i + 1 < sql.Length; i++)
            if (sql[i] == closing && sql[i + 1] == '\'') return i + 2;
        throw InvalidPlan();
    }

    static int SkipMysqlQuote(string sql, int start, char quote)
    {
        var end = SkipQuote(sql, start, quote, false);
        // Backslash behavior also depends on unconfirmed MySQL SQL-mode flags.
        for (var i = start + 1; i < end; i++) if (sql[i] == '\\') throw InvalidPlan();
        return end;
    }

    static int SkipComment(string sql, int start)
    {
        var depth = 1;
        for (var i = start + 2; i < sql.Length; i++)
        {
            if (sql[i] == '/' && At(sql, i + 1, '*')) { depth++; i++; }
            else if (sql[i] == '*' && At(sql, i + 1, '/')) { i++; if (--depth == 0) return i + 1; }
        }
        throw InvalidPlan();
    }

    static bool At(string sql, int index, char value) => index < sql.Length && sql[index] == value;
    static bool Digit(char c) => c is >= '0' and <= '9';
    static bool NameStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
    static bool NamePart(char c) => NameStart(c) || Digit(c);
    static bool IdentifierStart(char c) => NameStart(c) || c >= 128;
    static bool IdentifierPart(char c) => IdentifierStart(c) || Digit(c) || c == '$';
    static bool Operator(char c, KingbaseCompatibilityMode mode) => "+-*/<>=~!@#%^&|`?".IndexOf(c) >= 0 &&
        (mode != KingbaseCompatibilityMode.MySql || c is not ('`' or '#'));
    static ArgumentException InvalidPlan() => new("Invalid, unsupported or oversized PG SQL/parameter plan.");
}
