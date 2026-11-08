using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 零依赖 PDF 文本提取器（替代 iTextSharp 的 PdfTextExtractor）。
///
/// 背景：iTextSharp 会被 VPet 的代码插件检测拦下（DLL 内嵌 .afm/.lng 等无法解析的资源），
/// 因此改为自己解析：定位对象 → 解压内容流（FlateDecode）→ 遍历文本操作符
/// （Tj/TJ/'/"）→ 用字体的 /ToUnicode CMap 还原 Unicode。
/// 覆盖常见 PDF（Word/WPS/浏览器导出）；对扫描件（纯图片）没有文本可提取。
/// </summary>
public static class PdfTextReader
{
    /// <summary>读取 PDF 文本，按页返回。</summary>
    public static string ReadText(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        // Latin-1 保证 1 字节 = 1 字符，便于按字节偏移解析
        string raw = Encoding.Latin1.GetString(data);

        var objects = ParseObjects(raw);
        ExpandObjectStreams(raw, objects);
        var pages = CollectPages(raw, objects);

        var sb = new StringBuilder();
        int pageNo = 0;

        foreach (var pageNum in pages)
        {
            pageNo++;
            string pageObj = objects.TryGetValue(pageNum, out var po) ? po.Dict : "";
            string text = ExtractPageText(raw, objects, pageObj);
            sb.AppendLine($"=== 第 {pageNo} 页 ===");
            if (string.IsNullOrWhiteSpace(text))
                sb.AppendLine("（本页没有可提取的文本层，可能是扫描图片）");
            else
                sb.AppendLine(text.TrimEnd());
            sb.AppendLine();
        }

        if (pageNo == 0) sb.AppendLine("（未解析到页面，PDF 结构可能不受支持）");
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 展开 PDF 1.5+ 的对象流（/Type /ObjStm）：里面按 "对象号 偏移" 存放被压缩的对象字典，
    /// 不解开的话就拿不到页面的 /Contents、/Resources、/ToUnicode 等引用。
    /// </summary>
    private static void ExpandObjectStreams(string raw, Dictionary<int, PdfObject> objects)
    {
        var streams = objects.Values
            .Where(o => Regex.IsMatch(o.Dict, @"/Type\s*/ObjStm"))
            .ToList();

        foreach (var stm in streams)
        {
            var data = GetDecodedStream(raw, objects, stm);
            if (data == null) continue;

            var nMatch = Regex.Match(stm.Dict, @"/N\s+(\d+)");
            var fMatch = Regex.Match(stm.Dict, @"/First\s+(\d+)");
            if (!nMatch.Success || !fMatch.Success) continue;
            if (!int.TryParse(nMatch.Groups[1].Value, out int count) ||
                !int.TryParse(fMatch.Groups[1].Value, out int first))
                continue;

            string text = Encoding.Latin1.GetString(data);
            if (first <= 0 || first >= text.Length) continue;

            var nums = Regex.Matches(text.Substring(0, first), @"\d+")
                .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToList();

            for (int i = 0; i + 1 < nums.Count && i / 2 < count; i += 2)
            {
                int objNum = nums[i];
                int start = first + nums[i + 1];
                int end = (i + 3 < nums.Count) ? first + nums[i + 3] : text.Length;
                if (start < 0 || start >= text.Length) continue;
                if (end > text.Length || end <= start) end = text.Length;

                if (objects.ContainsKey(objNum)) continue;
                objects[objNum] = new PdfObject
                {
                    Num = objNum,
                    Dict = text.Substring(start, end - start).Trim()
                };
            }
        }
    }

    // ================= 对象解析 =================

    private sealed class PdfObject
    {
        public string Dict = "";
        public int Num;
        public int StreamStart = -1;
        public int StreamEnd = -1;
    }

    private static readonly Regex ObjHeader = new(@"(?<num>\d+)\s+\d+\s+obj\b", RegexOptions.Compiled);

    private static Dictionary<int, PdfObject> ParseObjects(string raw)
    {
        var map = new Dictionary<int, PdfObject>();
        int pos = 0;

        while (pos < raw.Length)
        {
            var m = ObjHeader.Match(raw, pos);
            if (!m.Success) break;

            int num = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
            int bodyStart = m.Index + m.Length;

            int endObj = raw.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
            int streamKw = raw.IndexOf("stream", bodyStart, StringComparison.Ordinal);

            var obj = new PdfObject { Num = num };

            if (streamKw >= 0 && (endObj < 0 || streamKw < endObj))
            {
                obj.Dict = raw.Substring(bodyStart, streamKw - bodyStart);

                int dataStart = streamKw + "stream".Length;
                if (dataStart < raw.Length && raw[dataStart] == '\r') dataStart++;
                if (dataStart < raw.Length && raw[dataStart] == '\n') dataStart++;

                int dataEnd = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                if (dataEnd < 0) dataEnd = raw.Length;

                obj.StreamStart = dataStart;
                obj.StreamEnd = dataEnd;
                pos = Math.Min(raw.Length, dataEnd + "endstream".Length);
            }
            else
            {
                int stop = endObj >= 0 ? endObj : raw.Length;
                obj.Dict = raw.Substring(bodyStart, stop - bodyStart);
                pos = Math.Min(raw.Length, stop + "endobj".Length);
            }

            map[num] = obj;
        }

        return map;
    }

    /// <summary>取对象的解码后内容流；无法解码（图片/未知滤镜）返回 null。</summary>
    private static byte[]? GetDecodedStream(string raw, Dictionary<int, PdfObject> objects, PdfObject obj)
    {
        if (obj.StreamStart < 0 || obj.StreamEnd <= obj.StreamStart) return null;

        // /Length 若为直接数字则按长度截取，避免内容流里出现 "endstream" 字样时被截断
        var lenMatch = Regex.Match(obj.Dict, @"/Length\s+(\d+)(?!\s+\d+\s+R)");
        int start = obj.StreamStart;
        int len = obj.StreamEnd - obj.StreamStart;
        if (lenMatch.Success && int.TryParse(lenMatch.Groups[1].Value, out int declared) &&
            declared > 0 && start + declared <= raw.Length)
        {
            len = declared;
        }

        byte[] bytes = Encoding.Latin1.GetBytes(raw.Substring(start, len));

        if (obj.Dict.Contains("/FlateDecode"))
        {
            try
            {
                using var input = new MemoryStream(bytes);
                using var z = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                z.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                // 有些 PDF 的 zlib 头不规范，跳过 2 字节再试一次
                try
                {
                    using var input = new MemoryStream(bytes, 2, Math.Max(0, bytes.Length - 2));
                    using var z = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    z.CopyTo(output);
                    return output.ToArray();
                }
                catch
                {
                    return null;
                }
            }
        }

        // 图片等二进制流不参与文本提取
        if (obj.Dict.Contains("/DCTDecode") || obj.Dict.Contains("/JPXDecode") ||
            obj.Dict.Contains("/CCITTFaxDecode") || obj.Dict.Contains("/Image"))
            return null;

        return bytes;
    }

    // ================= 页面收集 =================

    private static List<int> CollectPages(string raw, Dictionary<int, PdfObject> objects)
    {
        var pageNums = new List<int>();

        // 优先按 /Pages 的 /Kids 顺序
        foreach (var obj in objects.Values)
        {
            if (!Regex.IsMatch(obj.Dict, @"/Type\s*/Pages\b")) continue;
            var kids = Regex.Match(obj.Dict, @"/Kids\s*\[(?<k>[^\]]*)\]");
            if (!kids.Success) continue;

            foreach (Match r in Regex.Matches(kids.Groups["k"].Value, @"(\d+)\s+\d+\s+R"))
            {
                int n = int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                if (objects.TryGetValue(n, out var child) && Regex.IsMatch(child.Dict, @"/Type\s*/Page\b"))
                    pageNums.Add(n);
            }
            if (pageNums.Count > 0) break;
        }

        if (pageNums.Count > 0) return pageNums;

        // 回退：按文件内出现顺序
        foreach (var kv in objects.OrderBy(k => k.Key))
            if (Regex.IsMatch(kv.Value.Dict, @"/Type\s*/Page\b") && !kv.Value.Dict.Contains("/Pages"))
                pageNums.Add(kv.Key);

        return pageNums;
    }

    // ================= 单页文本提取 =================

    private static string ExtractPageText(string raw, Dictionary<int, PdfObject> objects, string pageDict)
    {
        var contents = new List<int>();
        var single = Regex.Match(pageDict, @"/Contents\s+(\d+)\s+\d+\s+R");
        if (single.Success)
        {
            contents.Add(int.Parse(single.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        else
        {
            var arr = Regex.Match(pageDict, @"/Contents\s*\[(?<k>[^\]]*)\]");
            if (arr.Success)
                foreach (Match r in Regex.Matches(arr.Groups["k"].Value, @"(\d+)\s+\d+\s+R"))
                    contents.Add(int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        var fonts = CollectFonts(raw, objects, pageDict);

        var sb = new StringBuilder();
        foreach (var num in contents)
        {
            if (!objects.TryGetValue(num, out var obj)) continue;
            var data = GetDecodedStream(raw, objects, obj);
            if (data == null) continue;
            sb.Append(DecodeContentStream(Encoding.Latin1.GetString(data), fonts));
        }

        return sb.ToString();
    }

    private sealed class FontInfo
    {
        public bool TwoByte;
        public Dictionary<int, string>? ToUnicode;
    }

    /// <summary>解析页面 /Resources /Font 字典，取出每个字体资源名对应的编码信息。</summary>
    private static Dictionary<string, FontInfo> CollectFonts(string raw, Dictionary<int, PdfObject> objects, string pageDict)
    {
        var result = new Dictionary<string, FontInfo>(StringComparer.Ordinal);

        var resMatch = Regex.Match(pageDict, @"/Resources\s+(\d+)\s+\d+\s+R");
        string resDict = pageDict;
        if (resMatch.Success)
        {
            int rn = int.Parse(resMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            if (objects.TryGetValue(rn, out var ro)) resDict = ro.Dict;
        }

        var fontDictMatch = Regex.Match(resDict, @"/Font\s+(\d+)\s+\d+\s+R");
        string fontDict = resDict;
        if (fontDictMatch.Success)
        {
            int fn = int.Parse(fontDictMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            if (objects.TryGetValue(fn, out var fo)) fontDict = fo.Dict;
        }

        var inline = Regex.Match(fontDict, @"/Font\s*<<(?<d>.*?)>>", RegexOptions.Singleline);
        if (inline.Success) fontDict = inline.Groups["d"].Value;

        foreach (Match m in Regex.Matches(fontDict, @"/(?<name>[A-Za-z0-9#_.+-]+)\s+(?<num>\d+)\s+\d+\s+R"))
        {
            string name = m.Groups["name"].Value;
            int num = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
            if (!objects.TryGetValue(num, out var fontObj)) continue;

            string dict = fontObj.Dict;
            var info = new FontInfo
            {
                TwoByte = dict.Contains("/Identity-H") || Regex.IsMatch(dict, @"/Subtype\s*/Type0")
            };

            var toUni = Regex.Match(dict, @"/ToUnicode\s+(\d+)\s+\d+\s+R");
            if (toUni.Success)
            {
                int un = int.Parse(toUni.Groups[1].Value, CultureInfo.InvariantCulture);
                if (objects.TryGetValue(un, out var cmapObj))
                {
                    var cmapBytes = GetDecodedStream(raw, objects, cmapObj);
                    if (cmapBytes != null)
                        info.ToUnicode = ParseToUnicode(Encoding.Latin1.GetString(cmapBytes));
                }
            }

            result[name] = info;
        }

        return result;
    }

    /// <summary>解析 /ToUnicode CMap 的 bfchar 与 bfrange。</summary>
    private static Dictionary<int, string> ParseToUnicode(string cmap)
    {
        var map = new Dictionary<int, string>();

        foreach (Match block in Regex.Matches(cmap, @"beginbfchar(?<b>.*?)endbfchar", RegexOptions.Singleline))
        {
            foreach (Match pair in Regex.Matches(block.Groups["b"].Value, @"<(?<src>[0-9A-Fa-f]+)>\s*<(?<dst>[0-9A-Fa-f]+)>"))
            {
                int code = Convert.ToInt32(pair.Groups["src"].Value, 16);
                map[code] = HexToText(pair.Groups["dst"].Value);
            }
        }

        foreach (Match block in Regex.Matches(cmap, @"beginbfrange(?<b>.*?)endbfrange", RegexOptions.Singleline))
        {
            string body = block.Groups["b"].Value;

            // <lo> <hi> <dstStart>
            foreach (Match r in Regex.Matches(body, @"<(?<lo>[0-9A-Fa-f]+)>\s*<(?<hi>[0-9A-Fa-f]+)>\s*<(?<dst>[0-9A-Fa-f]+)>"))
            {
                int lo = Convert.ToInt32(r.Groups["lo"].Value, 16);
                int hi = Convert.ToInt32(r.Groups["hi"].Value, 16);
                int dst = Convert.ToInt32(r.Groups["dst"].Value, 16);
                for (int c = lo; c <= hi && c - lo < 65535; c++)
                    map[c] = char.ConvertFromUtf32(dst + (c - lo));
            }

            // <lo> <hi> [<dst1> <dst2> ...]
            foreach (Match r in Regex.Matches(body, @"<(?<lo>[0-9A-Fa-f]+)>\s*<(?<hi>[0-9A-Fa-f]+)>\s*\[(?<arr>[^\]]*)\]"))
            {
                int lo = Convert.ToInt32(r.Groups["lo"].Value, 16);
                int idx = 0;
                foreach (Match d in Regex.Matches(r.Groups["arr"].Value, @"<([0-9A-Fa-f]+)>"))
                {
                    map[lo + idx] = HexToText(d.Groups[1].Value);
                    idx++;
                }
            }
        }

        return map;
    }

    private static string HexToText(string hex)
    {
        if (hex.Length % 4 == 0)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < hex.Length; i += 4)
                sb.Append((char)Convert.ToInt32(hex.Substring(i, 4), 16));
            return sb.ToString();
        }

        if (hex.Length == 2) return ((char)Convert.ToInt32(hex, 16)).ToString();

        try
        {
            var bytes = Convert.FromHexString(hex.Length % 2 == 0 ? hex : hex + "0");
            return Encoding.BigEndianUnicode.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    // ================= 内容流解析 =================

    /// <summary>遍历内容流的文本操作符，拼出可读文本。</summary>
    private static string DecodeContentStream(string content, Dictionary<string, FontInfo> fonts)
    {
        var sb = new StringBuilder();
        var stack = new List<object>();
        FontInfo? currentFont = null;

        int i = 0;
        while (i < content.Length)
        {
            char c = content[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '%')
            {
                while (i < content.Length && content[i] != '\n' && content[i] != '\r') i++;
                continue;
            }

            if (c == '(')
            {
                i = ReadLiteralString(content, i, out string s);
                stack.Add(s);
                continue;
            }

            if (c == '<')
            {
                if (i + 1 < content.Length && content[i + 1] == '<')
                {
                    // 内联字典：整体跳过
                    i = SkipInlineDict(content, i);
                    stack.Add("<dict>");
                    continue;
                }

                int end = content.IndexOf('>', i + 1);
                if (end < 0) break;
                stack.Add(Convert.FromHexString(PadHex(content.Substring(i + 1, end - i - 1))));
                i = end + 1;
                continue;
            }

            if (c == '[')
            {
                stack.Add("[");
                i++;
                continue;
            }

            if (c == ']')
            {
                stack.Add("]");
                i++;
                continue;
            }

            if (c == '/')
            {
                int start = i + 1;
                while (i + 1 < content.Length && !char.IsWhiteSpace(content[i + 1]) &&
                       !"()<>[]{}/%".Contains(content[i + 1]))
                    i++;
                stack.Add("/" + content.Substring(start, i - start + 1));
                i++;
                continue;
            }

            if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
            {
                int start = i;
                while (i < content.Length && (char.IsDigit(content[i]) || content[i] == '-' || content[i] == '+' || content[i] == '.'))
                    i++;
                if (double.TryParse(content.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    stack.Add(num);
                continue;
            }

            // 操作符
            int opStart = i;
            while (i < content.Length && !char.IsWhiteSpace(content[i]) && !"()<>[]{}/%".Contains(content[i]))
                i++;
            string op = content.Substring(opStart, i - opStart);
            if (op.Length == 0) { i++; continue; }

            switch (op)
            {
                case "Tf":
                    {
                        var name = stack.LastOrDefault(x => x is string str && str.StartsWith("/")) as string;
                        if (name != null && fonts.TryGetValue(name.Substring(1), out var info)) currentFont = info;
                        break;
                    }
                case "Tj":
                    sb.Append(DecodeStringValue(LastString(stack), currentFont));
                    break;
                case "TJ":
                    {
                        int arrStart = stack.LastIndexOf("[");
                        if (arrStart >= 0)
                        {
                            foreach (var item in stack.Skip(arrStart + 1))
                            {
                                if (item is byte[] bytes) sb.Append(DecodeBytes(bytes, currentFont));
                                else if (item is double kern && kern < -150) sb.Append(' ');
                            }
                        }
                        break;
                    }
                case "'":
                case "\"":
                    sb.Append('\n');
                    sb.Append(DecodeStringValue(LastString(stack), currentFont));
                    break;
                case "T*":
                case "Tm":
                    sb.Append('\n');
                    break;
                case "Td":
                case "TD":
                    {
                        // ty != 0 视为换行，tx != 0 视为空格
                        var nums = stack.Where(x => x is double).Cast<double>().ToList();
                        if (nums.Count >= 2)
                        {
                            if (Math.Abs(nums[^1]) > 0.5) sb.Append('\n');
                            else if (Math.Abs(nums[^2]) > 0.5) sb.Append(' ');
                        }
                        break;
                    }
                case "ET":
                    sb.Append('\n');
                    break;
            }

            stack.Clear();
        }

        // 压缩多余空行
        var lines = sb.ToString().Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd())
            .Where((l, idx) => l.Length > 0 || (idx > 0))
            .ToList();

        var result = new StringBuilder();
        int blank = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank++;
                if (blank > 1) continue;
            }
            else blank = 0;
            result.AppendLine(line);
        }

        return result.ToString();
    }

    private static string PadHex(string hex)
    {
        var clean = new string(hex.Where(Uri.IsHexDigit).ToArray());
        return clean.Length % 2 == 0 ? clean : clean + "0";
    }

    private static int SkipInlineDict(string s, int start)
    {
        int depth = 0;
        int i = start;
        while (i < s.Length - 1)
        {
            if (s[i] == '<' && s[i + 1] == '<') { depth++; i += 2; continue; }
            if (s[i] == '>' && s[i + 1] == '>')
            {
                depth--;
                i += 2;
                if (depth <= 0) return i;
                continue;
            }
            i++;
        }
        return s.Length;
    }

    private static int ReadLiteralString(string s, int start, out string value)
    {
        var sb = new StringBuilder();
        int i = start + 1;
        int depth = 1;

        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                switch (n)
                {
                    case 'n': sb.Append('\n'); i += 2; continue;
                    case 'r': sb.Append('\r'); i += 2; continue;
                    case 't': sb.Append('\t'); i += 2; continue;
                    case 'b': sb.Append('\b'); i += 2; continue;
                    case 'f': sb.Append('\f'); i += 2; continue;
                    case '(': sb.Append('('); i += 2; continue;
                    case ')': sb.Append(')'); i += 2; continue;
                    case '\\': sb.Append('\\'); i += 2; continue;
                    case '\n': i += 2; continue;
                    case '\r': i += (i + 2 < s.Length && s[i + 2] == '\n') ? 3 : 2; continue;
                    default:
                        if (char.IsDigit(n))
                        {
                            int oct = 0, k = 0;
                            while (k < 3 && i + 1 + k < s.Length && char.IsDigit(s[i + 1 + k]) && s[i + 1 + k] <= '7')
                            {
                                oct = oct * 8 + (s[i + 1 + k] - '0');
                                k++;
                            }
                            sb.Append((char)oct);
                            i += 1 + k;
                            continue;
                        }
                        sb.Append(n);
                        i += 2;
                        continue;
                }
            }

            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    value = sb.ToString();
                    return i + 1;
                }
            }

            sb.Append(c);
            i++;
        }

        value = sb.ToString();
        return s.Length;
    }

    private static byte[]? LastString(List<object> stack)
    {
        for (int i = stack.Count - 1; i >= 0; i--)
            if (stack[i] is byte[] b) return b;
        return null;
    }

    private static string DecodeStringValue(byte[]? bytes, FontInfo? font)
        => bytes == null ? "" : DecodeBytes(bytes, font);

    /// <summary>按字体编码把字节串转成 Unicode 文本。</summary>
    private static string DecodeBytes(byte[] bytes, FontInfo? font)
    {
        if (bytes.Length == 0) return "";

        if (font?.ToUnicode != null && font.ToUnicode.Count > 0)
        {
            var sb = new StringBuilder();
            if (font.TwoByte)
            {
                for (int i = 0; i + 1 < bytes.Length; i += 2)
                {
                    int code = (bytes[i] << 8) | bytes[i + 1];
                    sb.Append(font.ToUnicode.TryGetValue(code, out var u) ? u : "");
                }
            }
            else
            {
                foreach (byte b in bytes)
                    sb.Append(font.ToUnicode.TryGetValue(b, out var u) ? u : ((char)b).ToString());
            }
            return sb.ToString();
        }

        if (font?.TwoByte == true)
        {
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                int code = (bytes[i] << 8) | bytes[i + 1];
                if (code >= 32) sb.Append((char)code);
            }
            return sb.ToString();
        }

        return Encoding.Latin1.GetString(bytes);
    }
}
