using System;
using System.Globalization;
using System.Text;

namespace Kzm.Core
{
    /// <summary>
    /// Converts drawing text to plain text before numbers are searched.
    /// TEXT keeps backslashes literally and only has %% control codes;
    /// MTEXT contents additionally carry formatting codes (\H, \f, {…}, \S…;).
    /// </summary>
    public static class TextCleaner
    {
        /// <summary>Plain text of a single-line TEXT (TextString).</summary>
        public static string CleanText(string textString)
        {
            if (string.IsNullOrEmpty(textString)) return string.Empty;
            var output = new StringBuilder(textString.Length);
            int i = 0;
            while (i < textString.Length)
            {
                if (TryControlCode(textString, ref i, output)) continue;
                output.Append(textString[i]);
                i++;
            }
            return output.ToString();
        }

        /// <summary>Plain text of MTEXT contents (MText.Contents).</summary>
        public static string CleanMText(string contents)
        {
            if (string.IsNullOrEmpty(contents)) return string.Empty;
            var output = new StringBuilder(contents.Length);
            string s = contents;
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char code = s[i + 1];
                    switch (code)
                    {
                        case '\\':
                        case '{':
                        case '}':
                            output.Append(code);
                            i += 2;
                            continue;
                        case 'P':
                        case 'N':
                        case 'X':
                            // Paragraph, column break, dimension second line.
                            output.Append('\n');
                            i += 2;
                            continue;
                        case '~':
                            output.Append(' ');
                            i += 2;
                            continue;
                        case 'L':
                        case 'l':
                        case 'O':
                        case 'o':
                        case 'K':
                        case 'k':
                            // Underline / overline / strike-through toggles.
                            i += 2;
                            continue;
                        case 'A':
                        case 'C':
                        case 'c':
                        case 'F':
                        case 'f':
                        case 'H':
                        case 'Q':
                        case 'T':
                        case 'W':
                        case 'p':
                            // Codes with a parameter terminated by ';'.
                            i = SkipParameter(s, i + 2);
                            continue;
                        case 'S':
                        {
                            // Stacked text «\Sverх^низ;», «\S1/2;», «\S1#2;»: keep both parts
                            // separated by a space, so two numbers stay two numbers.
                            int end = s.IndexOf(';', i + 2);
                            string stacked = end < 0 ? s.Substring(i + 2) : s.Substring(i + 2, end - i - 2);
                            output.Append(' ');
                            foreach (char part in stacked)
                                output.Append(part == '^' || part == '/' || part == '#' ? ' ' : part);
                            output.Append(' ');
                            i = end < 0 ? s.Length : end + 1;
                            continue;
                        }
                        case 'U':
                            if (i + 6 < s.Length && s[i + 2] == '+' &&
                                int.TryParse(s.Substring(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int unicode))
                            {
                                output.Append((char)unicode);
                                i += 7;
                                continue;
                            }
                            break;
                        case 'M':
                            // \M+nXXXX: legacy multibyte character; not a digit in any code page used for heights.
                            if (i + 7 < s.Length && s[i + 2] == '+')
                            {
                                output.Append('?');
                                i += 8;
                                continue;
                            }
                            break;
                    }
                    // Unknown code: AutoCAD prints the character after the backslash.
                    output.Append(code);
                    i += 2;
                    continue;
                }
                if (c == '{' || c == '}')
                {
                    i++;
                    continue;
                }
                if (TryControlCode(s, ref i, output)) continue;
                output.Append(c);
                i++;
            }
            return output.ToString();
        }

        private static int SkipParameter(string s, int start)
        {
            int end = s.IndexOf(';', start);
            return end < 0 ? s.Length : end + 1;
        }

        /// <summary>%%d, %%p, %%c, %%%, %%u, %%o, %%k, %%nnn.</summary>
        private static bool TryControlCode(string s, ref int i, StringBuilder output)
        {
            if (s[i] != '%' || i + 2 >= s.Length || s[i + 1] != '%') return false;
            char code = char.ToLowerInvariant(s[i + 2]);
            switch (code)
            {
                case 'd': output.Append('°'); i += 3; return true;
                case 'p': output.Append('±'); i += 3; return true;
                case 'c': output.Append('⌀'); i += 3; return true;
                case '%': output.Append('%'); i += 3; return true;
                case 'u':
                case 'o':
                case 'k':
                    i += 3;
                    return true;
            }
            if (i + 4 < s.Length && char.IsDigit(s[i + 2]) && char.IsDigit(s[i + 3]) && char.IsDigit(s[i + 4]))
            {
                int number = int.Parse(s.Substring(i + 2, 3), CultureInfo.InvariantCulture);
                output.Append((char)number);
                i += 5;
                return true;
            }
            return false;
        }
    }
}
