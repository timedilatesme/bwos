using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Bwos
{
    /// <summary>
    /// Parser for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf,
    /// localconfig.vdf, loginusers.vdf). Values are either strings or nested dictionaries.
    /// Keys are case-insensitive because Steam is not consistent about casing.
    /// </summary>
    internal static class Vdf
    {
        public static Dictionary<string, object> ParseFile(string path)
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public static Dictionary<string, object> Parse(string text)
        {
            int pos = 0;
            return ParseBlock(text, ref pos);
        }

        private static Dictionary<string, object> ParseBlock(string text, ref int pos)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string key = NextToken(text, ref pos);
                if (key == null || key == "}") return result;
                if (key == "{") continue; // tolerate stray braces
                int save = pos;
                string value = NextToken(text, ref pos);
                if (value == null) return result;
                if (value == "{") result[key] = ParseBlock(text, ref pos);
                else if (value == "}") { pos = save; result[key] = ""; }
                else result[key] = value;
            }
        }

        /// <summary>Returns a quoted string's content, "{", "}", or null at end of input.</summary>
        private static string NextToken(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (char.IsWhiteSpace(c)) { pos++; continue; }
                if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
                {
                    while (pos < s.Length && s[pos] != '\n') pos++;
                    continue;
                }
                if (c == '[') // conditionals like [$WIN32] – skip
                {
                    while (pos < s.Length && s[pos] != ']') pos++;
                    pos++;
                    continue;
                }
                break;
            }
            if (pos >= s.Length) return null;

            char ch = s[pos];
            if (ch == '{' || ch == '}') { pos++; return ch.ToString(); }

            var sb = new StringBuilder();
            if (ch == '"')
            {
                pos++;
                while (pos < s.Length && s[pos] != '"')
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length)
                    {
                        char n = s[pos + 1];
                        if (n == 'n') sb.Append('\n');
                        else if (n == 't') sb.Append('\t');
                        else sb.Append(n);
                        pos += 2;
                        continue;
                    }
                    sb.Append(s[pos++]);
                }
                pos++; // closing quote
                return sb.ToString();
            }

            // Unquoted token
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '{' && s[pos] != '}' && s[pos] != '"')
                sb.Append(s[pos++]);
            return sb.ToString();
        }

        public static Dictionary<string, object> Child(Dictionary<string, object> d, params string[] path)
        {
            var cur = d;
            foreach (var key in path)
            {
                if (cur == null) return null;
                object v;
                if (!cur.TryGetValue(key, out v)) return null;
                cur = v as Dictionary<string, object>;
            }
            return cur;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as string;
            return null;
        }

        public static long Long(Dictionary<string, object> d, string key)
        {
            long n;
            string s = Str(d, key);
            return s != null && long.TryParse(s, out n) ? n : 0;
        }
    }
}
