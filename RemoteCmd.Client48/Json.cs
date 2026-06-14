using System.Text;

namespace RemoteCmd.Client48
{
    /// <summary>
    /// Tiny JSON helper. The relay protocol uses flat objects with a handful of
    /// known keys, so a full parser is overkill; only result serialization needs
    /// rigorous escaping (command output is arbitrary text).
    /// </summary>
    internal static class Json
    {
        /// <summary>Serialize a command result: {"output":"...","exitCode":N}.</summary>
        public static string Result(string output, int exitCode)
        {
            var sb = new StringBuilder();
            sb.Append("{\"output\":");
            AppendEscaped(sb, output);
            sb.Append(",\"exitCode\":").Append(exitCode).Append('}');
            return sb.ToString();
        }

        /// <summary>Extract a string property value (handles escapes). Null if absent.</summary>
        public static string GetString(string json, string key)
        {
            var marker = "\"" + key + "\"";
            int i = json.IndexOf(marker);
            if (i < 0) return null;
            i += marker.Length;
            i = SkipToValue(json, i);
            if (i < 0 || i >= json.Length) return null;
            if (json[i] == 'n') return null; // null
            if (json[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 5 < json.Length)
                            {
                                int code = System.Convert.ToInt32(json.Substring(i + 2, 4), 16);
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    i += 2;
                }
                else if (c == '"') break;
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }

        /// <summary>Extract a numeric property value. Returns fallback if absent.</summary>
        public static long GetLong(string json, string key, long fallback)
        {
            var marker = "\"" + key + "\"";
            int i = json.IndexOf(marker);
            if (i < 0) return fallback;
            i += marker.Length;
            i = SkipToValue(json, i);
            if (i < 0 || i >= json.Length) return fallback;
            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-')) i++;
            if (i == start) return fallback;
            return long.TryParse(json.Substring(start, i - start), out var v) ? v : fallback;
        }

        private static int SkipToValue(string json, int i)
        {
            while (i < json.Length && (json[i] == ' ' || json[i] == ':' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
            return i;
        }

        private static void AppendEscaped(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
