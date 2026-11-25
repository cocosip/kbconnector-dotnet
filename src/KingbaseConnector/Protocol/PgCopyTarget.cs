namespace KingbaseConnector.Protocol;

/// <summary>Consumes tokens from the existing SQL lexer, without interpreting quoted text.</summary>
internal sealed class PgCopyTarget
{
    int _stage;
    int _depth;
    bool _input;
    internal bool IsImport => _stage == 4 ? _input : throw Invalid();
    internal void Word(string word)
    {
        if (_stage == 4 || _depth > 0) return;
        switch (_stage)
        {
            case 0: if (word != "COPY") throw Invalid(); _stage = 1; break;
            case 1: if (word != "ONLY") _stage = 2; break;
            case 2: if (word is not ("FROM" or "TO")) throw Invalid(); _input = word == "FROM"; _stage = 3; break;
            case 3: if (word != (_input ? "STDIN" : "STDOUT")) throw Invalid(); _stage = 4; break;
        }
    }
    internal void Identifier()
    {
        if (_stage == 4 || _depth > 0) return;
        if (_stage != 1) throw Invalid();
        _stage = 2;
    }
    internal void Literal() { if (_stage != 4 && _depth == 0) throw Invalid(); }
    internal void Symbol(char symbol)
    {
        if (_stage == 4) return;
        if (symbol == '(')
        {
            if (_depth == 0 && _stage is not (1 or 2)) throw Invalid();
            _depth++; return;
        }
        if (_depth > 0) { if (symbol == ')' && --_depth == 0) _stage = 2; return; }
        if (symbol == '.' && _stage == 2) { _stage = 1; return; }
        throw Invalid();
    }
    static ArgumentException Invalid() => new("Client COPY requires one COPY statement with FROM STDIN or TO STDOUT.");
}
