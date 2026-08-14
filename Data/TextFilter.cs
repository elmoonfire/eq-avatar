using System;
using System.Collections.Generic;
using System.Text;

namespace EQAvatar.Spike.Data;

/// <summary>
/// A one-line search language for the consoles.
///
/// WHY NOT JUST "does the line contain this". Because the question people actually bring to a bot
/// log is never one word. It is "show me the hand-ins but not the ones that only tried", or "the
/// merge sweep OR the quest run, and nothing the app itself said". A plain Contains box makes you
/// run three searches and hold the answer in your head; that is exactly the place where a person
/// reading a failure gives up and says "it just doesn't work".
///
/// WHAT IT UNDERSTANDS
///   totem offered            two words, both must appear (adjacency means AND)
///   totem AND offered        the same thing, said out loud
///   totem OR orders          either one
///   totem NOR orders         NEITHER — the junction that has no keyboard shortcut anywhere else
///   -stopped   !stopped   NOT stopped        drop the lines that say it
///   "has been assigned"      a phrase, matched with its spaces
///   (totem OR orders) -miss  brackets, so the junction you meant is the junction you get
///   source:quest   tag:kerra                 fields, not free text
///
/// Everything is case-insensitive, including the keywords, and an unparseable query REPORTS
/// ITSELF rather than silently matching nothing — a filter that hides the whole log because you
/// left a bracket open is indistinguishable from a bot that did nothing, and that confusion costs
/// far more than the error string costs to write.
/// </summary>
public sealed class TextFilter
{
    private readonly Node? _root;

    /// <summary>Null when the query parsed. Otherwise what went wrong, in words.</summary>
    public string? Error { get; }

    /// <summary>An empty (or blank) query matches everything — no filter is not a filter.</summary>
    public bool IsEmpty => _root is null && Error is null;

    private TextFilter(Node? root, string? error) { _root = root; Error = error; }

    /// <summary>A filter that lets everything through.</summary>
    public static readonly TextFilter None = new(null, null);

    public static TextFilter Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return None;
        try
        {
            var p = new Parser(Tokenize(query));
            Node n = p.ParseOr();
            p.ExpectEnd();
            return new TextFilter(n, null);
        }
        catch (FilterException ex) { return new TextFilter(null, ex.Message); }
    }

    /// <summary>
    /// Does this line survive the filter? A query that failed to parse lets everything through:
    /// the error is shown next to the box, and half-typed text must not blank the console while
    /// the user is still typing the rest of it.
    /// </summary>
    public bool Matches(string text, string source = "", string tag = "")
        => _root is null || _root.Eval(text ?? "", source ?? "", tag ?? "");

    // ---------------------------------------------------------------- tree

    private abstract class Node { public abstract bool Eval(string text, string source, string tag); }

    private sealed class TermNode : Node
    {
        private readonly string _field, _needle;
        public TermNode(string field, string needle) { _field = field; _needle = needle; }
        public override bool Eval(string text, string source, string tag)
        {
            // The bare-word case tests the three fields SEPARATELY rather than searching one
            // concatenation of them. Joining them would invent matches that straddle the seam (a
            // line ending "...a" in front of source "pp" would answer to "app"), and it would
            // allocate a fresh string for every line on every keystroke.
            return _field switch
            {
                "source" => Has(source),
                "tag" => Has(tag),
                "text" => Has(text),
                _ => Has(text) || Has(source) || Has(tag),
            };

            bool Has(string hay) => hay.IndexOf(_needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    private sealed class NotNode : Node
    {
        private readonly Node _inner;
        public NotNode(Node inner) { _inner = inner; }
        public override bool Eval(string t, string s, string g) => !_inner.Eval(t, s, g);
    }

    private sealed class AndNode : Node
    {
        private readonly Node _a, _b;
        public AndNode(Node a, Node b) { _a = a; _b = b; }
        public override bool Eval(string t, string s, string g) => _a.Eval(t, s, g) && _b.Eval(t, s, g);
    }

    private sealed class OrNode : Node
    {
        private readonly Node _a, _b;
        public OrNode(Node a, Node b) { _a = a; _b = b; }
        public override bool Eval(string t, string s, string g) => _a.Eval(t, s, g) || _b.Eval(t, s, g);
    }

    // ---------------------------------------------------------------- tokens

    private enum Kind { Word, Phrase, LParen, RParen, And, Or, Nor, Not, End }

    private readonly record struct Token(Kind Kind, string Text);

    private sealed class FilterException : Exception
    {
        public FilterException(string m) : base(m) { }
    }

    /// <summary>
    /// The most words one query may contain.
    ///
    /// This is the real stack guard. Bracket depth is bounded separately, but N words in a row
    /// build a tree nested N deep — parsed with a loop, so the parser never notices, and then
    /// WALKED RECURSIVELY on every line of the console. Paste a paragraph into the search box and
    /// the process dies of a stack overflow, which .NET does not let anyone catch or write down.
    /// Capping the token count bounds both shapes in the one place they both come from.
    /// </summary>
    private const int MaxTokens = 400;

    private static List<Token> Tokenize(string q)
    {
        var outp = new List<Token>();
        int i = 0;
        while (i < q.Length)
        {
            if (outp.Count >= MaxTokens)
                throw new FilterException($"that's more than {MaxTokens} words to search for at once");
            char c = q[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { outp.Add(new Token(Kind.LParen, "(")); i++; continue; }
            if (c == ')') { outp.Add(new Token(Kind.RParen, ")")); i++; continue; }
            if (c == '"' || c == '“' || c == '”')
            {
                // Smart quotes count: these queries get pasted out of chat windows and documents,
                // where a straight quote is not what you get.
                i++;
                var sb = new StringBuilder();
                while (i < q.Length && q[i] != '"' && q[i] != '“' && q[i] != '”') sb.Append(q[i++]);
                if (i < q.Length) i++;                       // the closing quote (missing one is forgiven)
                outp.Add(new Token(Kind.Phrase, sb.ToString()));
                continue;
            }
            if (c == '&')
            {
                i++; if (i < q.Length && q[i] == '&') i++;
                outp.Add(new Token(Kind.And, "AND")); continue;
            }
            if (c == '|')
            {
                i++; if (i < q.Length && q[i] == '|') i++;
                outp.Add(new Token(Kind.Or, "OR")); continue;
            }
            if (c == '!')
            {
                i++; outp.Add(new Token(Kind.Not, "NOT")); continue;
            }

            int start = i;
            while (i < q.Length && !char.IsWhiteSpace(q[i]) && q[i] != '(' && q[i] != ')'
                   && q[i] != '&' && q[i] != '|' && q[i] != '"') i++;
            string w = q.Substring(start, i - start);

            if (w.Equals("and", StringComparison.OrdinalIgnoreCase)) { outp.Add(new Token(Kind.And, "AND")); continue; }
            if (w.Equals("or", StringComparison.OrdinalIgnoreCase)) { outp.Add(new Token(Kind.Or, "OR")); continue; }
            if (w.Equals("nor", StringComparison.OrdinalIgnoreCase)) { outp.Add(new Token(Kind.Nor, "NOR")); continue; }
            if (w.Equals("not", StringComparison.OrdinalIgnoreCase)) { outp.Add(new Token(Kind.Not, "NOT")); continue; }

            // A leading minus negates — but only when something follows it, so a lone "-" and a
            // line about "- 12 hp" are still searchable as themselves.
            if (w.Length > 1 && w[0] == '-')
            {
                outp.Add(new Token(Kind.Not, "NOT"));
                w = w.Substring(1);
            }
            outp.Add(new Token(Kind.Word, w));
        }
        outp.Add(new Token(Kind.End, ""));
        return outp;
    }

    // ---------------------------------------------------------------- parser

    private sealed class Parser
    {
        private readonly List<Token> _t;
        private int _i;
        public Parser(List<Token> t) { _t = t; }

        private Token Peek => _t[_i];
        private Token Take() => _t[_i++];

        /// <summary>
        /// OR and NOR bind loosest, and read left to right: "a NOR b" is "neither a nor b".
        ///
        /// A CHAIN of NORs is gathered and negated ONCE, which is the only reading that matches
        /// the word. Negating each junction as it came — Not(Or(Not(Or(a,b)), c)) — quietly means
        /// "(a OR b) AND NOT c", so "stopped NOR paused NOR failed", the obvious way to hide three
        /// things, would have SHOWN the first two. A wrong answer that looks plausible is exactly
        /// the failure this class is written to avoid.
        /// </summary>
        public Node ParseOr()
        {
            Node left = ParseAnd();
            while (Peek.Kind is Kind.Or or Kind.Nor)
            {
                if (Peek.Kind == Kind.Or) { Take(); left = new OrNode(left, ParseAnd()); continue; }
                Node acc = left;
                while (Peek.Kind == Kind.Nor) { Take(); acc = new OrNode(acc, ParseAnd()); }
                left = new NotNode(acc);
            }
            return left;
        }

        private Node ParseAnd()
        {
            Node left = ParseUnary();
            while (true)
            {
                if (Peek.Kind == Kind.And) { Take(); }
                else if (Peek.Kind is not (Kind.Word or Kind.Phrase or Kind.LParen or Kind.Not)) break;
                left = new AndNode(left, ParseUnary());
            }
            return left;
        }

        /// <summary>Brackets recurse, and a stack overflow is the one failure this app cannot catch
        /// and report — .NET kills the process outright, mid-run, with nothing written down. Sixty
        /// levels is far past any query a person types and far short of the stack.</summary>
        private int _depth;
        private const int MaxDepth = 60;

        private Node ParseUnary()
        {
            // The increment goes INSIDE the try so the matching decrement always runs. Nothing
            // catches inside the parser today, so a leak couldn't bite — but the next person to add
            // error recovery would inherit a counter that only ever climbs, and a search box that
            // starts refusing perfectly good queries is a bug with no visible cause.
            _depth++;
            try
            {
                if (_depth > MaxDepth) throw new FilterException("too many brackets to follow");
                return ParseUnaryCore();
            }
            finally { _depth--; }
        }

        private Node ParseUnaryCore()
        {
            switch (Peek.Kind)
            {
                case Kind.Not:
                    Take();
                    return new NotNode(ParseUnary());
                case Kind.LParen:
                {
                    Take();
                    Node inner = ParseOr();
                    if (Peek.Kind != Kind.RParen) throw new FilterException("a bracket is still open");
                    Take();
                    return inner;
                }
                case Kind.Word:
                    return TermFrom(Take().Text, allowField: true);
                case Kind.Phrase:
                    return TermFrom(Take().Text, allowField: false);
                case Kind.End:
                    throw new FilterException("the query stops early — something is missing after the last word");
                default:
                    throw new FilterException($"\"{Peek.Text}\" needs something to join");
            }
        }

        private static Node TermFrom(string w, bool allowField)
        {
            if (allowField)
            {
                int colon = w.IndexOf(':');
                if (colon > 0 && colon < w.Length - 1)
                {
                    string field = w.Substring(0, colon).ToLowerInvariant();
                    string rest = w.Substring(colon + 1);
                    if (field is "source" or "src" or "from") return new TermNode("source", rest);
                    if (field is "tag") return new TermNode("tag", rest);
                    if (field is "text" or "msg" or "line") return new TermNode("text", rest);
                }
            }
            return new TermNode("any", w);
        }

        public void ExpectEnd()
        {
            if (Peek.Kind != Kind.End)
                throw new FilterException(Peek.Kind == Kind.RParen
                    ? "there is a closing bracket with nothing open"
                    : $"couldn't read the rest from \"{Peek.Text}\"");
        }
    }
}
