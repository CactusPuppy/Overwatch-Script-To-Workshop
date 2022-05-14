using System.Collections.Generic;
using Deltin.Deltinteger.Compiler;
using Deltin.Deltinteger.Compiler.SyntaxTree;
using Deltin.Deltinteger.Elements;

namespace Deltin.Deltinteger.Parse.Strings
{
    public class InterpolatedStringAction : IExpression
    {
        private readonly ParseInfo _parseInfo;
        private readonly string _tail;
        private readonly InterpolatedStringActionPart[] _parts;

        public InterpolatedStringAction(InterpolatedStringExpression context, ParseInfo parseInfo, Scope scope)
        {
            _parseInfo = parseInfo;
            _tail = GetPartText(context.Tail, true);

            _parts = new InterpolatedStringActionPart[context.Parts == null ? 0 : context.Parts.Count];
            for (int i = 0; i < _parts.Length; i++)
                _parts[i] = new InterpolatedStringActionPart(parseInfo.GetExpression(scope, context.Parts[i].Expression), GetPartText(context.Parts[i].Right, false));
        }

        public IWorkshopTree Parse(ActionSet actionSet)
        {
            var values = new List<IWorkshopTree>();

            if (_tail != string.Empty)
                values.Add(Element.CustomString(_tail));

            foreach (var part in _parts)
            {
                values.Add(part.Value.Parse(actionSet));
                if (part.Right != string.Empty)
                    values.Add(Element.CustomString(part.Right));
            }

            return StringElement.Join(values.ToArray());
        }

        public CodeType Type() => _parseInfo.TranslateInfo.Types.String();

        static string GetPartText(Token token, bool isStart)
        {
            switch (token.TokenType)
            {
                // Trim a string beginning with $
                case Compiler.TokenType.InterpolatedStringTail:
                    string reduce = token.Text.Substring(1).TrimStart(); // Remove the $ and the whitespace between the $ and ".
                    return reduce.Substring(1, reduce.Length - 2); // Remove the quotes.

                // Remove the quotes.
                case Compiler.TokenType.InterpolatedStringMiddle:
                    return token.Text.Substring(0, token.Text.Length - 1);

                // 'Head' will begin with $ if 'isStart' is true.
                case Compiler.TokenType.InterpolatedStringHead:
                    if (isStart) goto case Compiler.TokenType.InterpolatedStringTail;
                    else goto case Compiler.TokenType.InterpolatedStringMiddle;

                default:
                    return token.Text.RemoveQuotes();
            }
        }

        struct InterpolatedStringActionPart
        {
            public IExpression Value;
            public string Right;

            public InterpolatedStringActionPart(IExpression value, string right)
            {
                Value = value;
                Right = right;
            }
        }
    }
}