#nullable enable
using System;

namespace GoalDrivenAI.Language
{
    // Json.NET accepts JavaScript extensions. Reject those before loading the bounded DOM.
    internal sealed class StrictJson
    {
        private readonly string source;
        private int offset;
        private StrictJson(string source) { this.source = source; }
        internal static void Validate(string source)
        {
            var parser = new StrictJson(source);
            parser.Value(0); parser.White();
            if (parser.offset != source.Length) Invalid();
        }
        private char Peek => offset < source.Length ? source[offset] : '\0';
        private void White()
        { while (Peek == ' ' || Peek == '\t' || Peek == '\r' || Peek == '\n') offset++; }
        private bool Take(char c)
        {
            White();
            if (Peek != c) return false;
            offset++; return true;
        }
        private void Need(char c) { if (!Take(c)) Invalid(); }
        private void Value(int depth)
        {
            if (depth > 16) Invalid();
            White();
            if (Peek == '{')
            {
                offset++;
                if (Take('}')) return;
                do { White(); Quoted(); Need(':'); Value(depth + 1); } while (Take(','));
                Need('}'); return;
            }
            if (Peek == '[')
            {
                offset++;
                if (Take(']')) return;
                do { Value(depth + 1); } while (Take(','));
                Need(']'); return;
            }
            if (Peek == '"') { Quoted(); return; }
            if (Peek == 't') { Literal("true"); return; }
            if (Peek == 'f') { Literal("false"); return; }
            if (Peek == 'n') { Literal("null"); return; }
            if (Peek == '-') offset++;
            if (Peek == '0') offset++;
            else
            {
                if (Peek < '1' || Peek > '9') Invalid();
                Digits();
            }
            if (Peek == '.') { offset++; RequireDigits(); }
            if (Peek == 'e' || Peek == 'E')
            {
                offset++;
                if (Peek == '+' || Peek == '-') offset++;
                RequireDigits();
            }
        }
        private void Digits() { while (Peek >= '0' && Peek <= '9') offset++; }
        private void RequireDigits()
        { if (Peek < '0' || Peek > '9') Invalid(); Digits(); }
        private void Literal(string value)
        {
            if (offset + value.Length > source.Length ||
                string.CompareOrdinal(source, offset, value, 0, value.Length) != 0) Invalid();
            offset += value.Length;
        }
        private void Quoted()
        {
            if (Peek != '"') Invalid();
            offset++;
            while (Peek != '"')
            {
                if (Peek < ' ') Invalid();
                if (Peek == '\\')
                {
                    offset++;
                    if (Peek == 'u')
                    {
                        offset++;
                        for (int i = 0; i < 4; i++)
                        {
                            char c = Peek;
                            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) Invalid();
                            offset++;
                        }
                        continue;
                    }
                    if ("\"\\/bfnrt".IndexOf(Peek) < 0 || Peek == '\0') Invalid();
                }
                offset++;
            }
            offset++;
        }
        private static void Invalid() => throw new ArgumentException("Strict JSON required.");
    }
}
