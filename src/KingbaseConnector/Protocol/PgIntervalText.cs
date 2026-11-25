using System.Globalization;
using System.Text;

namespace KingbaseConnector.Protocol;

internal static class PgIntervalText
{
    internal static KingbaseInterval Parse(string text, string? style)
    {
        if (style is not ("postgres" or "postgres_verbose" or "kingbase" or "kingbase_verbose" or "sql_standard" or "iso_8601"))
            throw new NotSupportedException("PG INTERVAL results require a confirmed supported IntervalStyle.");
        if (text.Length is 0 or > 128) throw InvalidText();
        try
        {
            return style switch
            {
                "iso_8601" => Iso(text),
                "sql_standard" => Sql(text),
                _ => Words(text, style is "postgres_verbose" or "kingbase_verbose")
            };
        }
        catch (OverflowException) { throw InvalidText(); }
    }

    internal static string Format(KingbaseInterval value)
    {
        if (value == default) return "PT0S";
        var text = new StringBuilder("P");
        if (value.Months != 0) text.Append(value.Months.ToString(CultureInfo.InvariantCulture)).Append('M');
        if (value.Days != 0) text.Append(value.Days.ToString(CultureInfo.InvariantCulture)).Append('D');
        if (value.Microseconds != 0)
        {
            text.Append('T');
            var hours = value.Microseconds / 3600000000L;
            var minutes = value.Microseconds / 60000000L % 60;
            var seconds = value.Microseconds % 60000000L / 1000000m;
            if (hours != 0) text.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('H');
            if (minutes != 0) text.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
            if (seconds != 0) text.Append(seconds.ToString("0.######", CultureInfo.InvariantCulture)).Append('S');
        }
        return text.ToString();
    }

    static KingbaseInterval Iso(string text)
    {
        if (text[0] != 'P') throw InvalidText();
        var parts = new Parts();
        var position = 1; var previous = -1; var clock = false; var hasClock = false;
        while (position < text.Length)
        {
            if (text[position] == 'T')
            {
                if (clock) throw InvalidText();
                clock = true; position++;
                if (position == text.Length) throw InvalidText();
            }
            var start = position;
            if (position < text.Length && text[position] is '+' or '-') position++;
            while (position < text.Length && (text[position] is >= '0' and <= '9' || text[position] == '.')) position++;
            if (position == text.Length) throw InvalidText();
            var rank = text[position++] switch
            {
                'Y' when !clock => 0, 'M' when !clock => 1, 'D' when !clock => 2,
                'H' when clock => 3, 'M' when clock => 4, 'S' when clock => 5,
                _ => throw InvalidText()
            };
            if (rank <= previous) throw InvalidText();
            previous = rank; hasClock |= clock;
            parts.Add(rank, Number(text.Substring(start, position - start - 1), rank == 5));
        }
        if (previous == -1 || clock && !hasClock) throw InvalidText();
        return parts.Value();
    }

    static KingbaseInterval Words(string text, bool verbose)
    {
        var ago = false;
        if (verbose)
        {
            if (!text.StartsWith("@ ", StringComparison.Ordinal)) throw InvalidText();
            text = text.Substring(2);
            ago = text.EndsWith(" ago", StringComparison.Ordinal);
            if (ago) text = text.Substring(0, text.Length - 4);
            if (text == "0") return default;
        }
        var fields = text.Split(' ');
        var parts = new Parts(); var previous = -1;
        for (var index = 0; index < fields.Length;)
        {
            var field = fields[index++];
            // Canonical verbose output carries the first component's sign only through its final "ago".
            if (verbose && previous == -1 && !Unsigned(field)) throw InvalidText();
            if (!verbose && field.IndexOf(':') >= 0)
            {
                if (index != fields.Length) throw InvalidText();
                parts.Microseconds += Clock(field);
                break;
            }
            if (index == fields.Length) throw InvalidText();
            var rank = fields[index++] switch
            {
                "year" or "years" => 0, "mon" or "mons" => 1, "day" or "days" => 2,
                "hour" or "hours" when verbose => 3, "min" or "mins" when verbose => 4,
                "sec" or "secs" when verbose => 5, _ => throw InvalidText()
            };
            if (rank <= previous) throw InvalidText();
            previous = rank;
            parts.Add(rank, Number(field, rank == 5));
        }
        if (ago) { parts.Months = -parts.Months; parts.Days = -parts.Days; parts.Microseconds = -parts.Microseconds; }
        return parts.Value();
    }

    static KingbaseInterval Sql(string text)
    {
        var fields = text.Split(' ');
        var parts = new Parts();
        if (fields.Length == 3)
        {
            if (!Signed(fields[0]) || !Signed(fields[1]) || !Signed(fields[2])) throw InvalidText();
            parts.Months = YearMonth(fields[0]);
            parts.Days = Number(fields[1], false);
            parts.Microseconds = Clock(fields[2]);
        }
        else if (fields.Length == 2)
        {
            parts.Days = Number(fields[0], false);
            parts.Microseconds = Clock(fields[1]);
            // SQL uniform day/time output has one sign in front of the entire group.
            if (fields[0][0] == '-' && !Signed(fields[1])) parts.Microseconds = -parts.Microseconds;
        }
        else if (fields.Length == 1)
        {
            if (text.IndexOf(':') >= 0) parts.Microseconds = Clock(text);
            else if (text.IndexOf('-', Signed(text) ? 1 : 0) >= 0) parts.Months = YearMonth(text);
            else if (text != "0") throw InvalidText();
        }
        else throw InvalidText();
        return parts.Value();
    }

    static decimal YearMonth(string text)
    {
        var negative = text[0] == '-';
        if (Signed(text)) text = text.Substring(1);
        var fields = text.Split('-');
        if (fields.Length != 2 || !Unsigned(fields[0]) || !Unsigned(fields[1])) throw InvalidText();
        var years = Number(fields[0], false); var months = Number(fields[1], false);
        if (months > 11) throw InvalidText();
        return (years * 12 + months) * (negative ? -1 : 1);
    }

    static decimal Clock(string text)
    {
        var negative = text.Length != 0 && text[0] == '-';
        if (Signed(text)) text = text.Substring(1);
        var fields = text.Split(':');
        if (fields.Length != 3 || !Unsigned(fields[0]) || !Unsigned(fields[1]) || !Unsigned(fields[2]) || fields[1].Length != 2) throw InvalidText();
        var hours = Number(fields[0], false); var minutes = Number(fields[1], false); var seconds = Number(fields[2], true);
        if (minutes > 59 || seconds >= 60) throw InvalidText();
        return (hours * 3600000000m + minutes * 60000000m + seconds * 1000000m) * (negative ? -1 : 1);
    }

    static bool Signed(string text) => text.Length != 0 && text[0] is '+' or '-';
    static bool Unsigned(string text) => text.Length != 0 && text[0] is >= '0' and <= '9';

    static decimal Number(string text, bool fraction)
    {
        var position = Signed(text) ? 1 : 0;
        var first = position;
        while (position < text.Length && text[position] is >= '0' and <= '9') position++;
        if (position == first || position - first > 19) throw InvalidText();
        if (fraction && position < text.Length && text[position] == '.')
        {
            first = ++position;
            while (position < text.Length && text[position] is >= '0' and <= '9') position++;
            if (position == first || position - first > 6) throw InvalidText();
        }
        if (position != text.Length || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value)) throw InvalidText();
        return value;
    }

    struct Parts
    {
        internal decimal Months, Days, Microseconds;
        internal void Add(int rank, decimal value)
        {
            switch (rank)
            {
                case 0: Months += value * 12; break;
                case 1: Months += value; break;
                case 2: Days += value; break;
                case 3: Microseconds += value * 3600000000m; break;
                case 4: Microseconds += value * 60000000m; break;
                case 5: Microseconds += value * 1000000m; break;
            }
        }
        internal KingbaseInterval Value()
        {
            if (Months < int.MinValue || Months > int.MaxValue || Days < int.MinValue || Days > int.MaxValue ||
                Microseconds < long.MinValue || Microseconds > long.MaxValue) throw InvalidText();
            return new((int)Months, (int)Days, (long)Microseconds);
        }
    }
    static FormatException InvalidText() => new("Invalid or out-of-range PG INTERVAL text.");
}
