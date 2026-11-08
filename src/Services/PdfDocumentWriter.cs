using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text;

namespace VPet.Plugin.PetChat.Services;

/// <summary>PDF 段落对齐方式。</summary>
public enum PdfAlign
{
    Left,
    Center,
    Right,
    Justify
}

/// <summary>create_pdf 的排版块类型。</summary>
public enum PdfBlockKind
{
    Text,
    Table,
    Image
}

/// <summary>
/// 一个 PDF 排版块。由 <see cref="ToolExecutor"/> 解析工具参数后构造。
/// </summary>
public sealed class PdfBlock
{
    public PdfBlockKind Kind = PdfBlockKind.Text;

    /// <summary>正文文本（Kind=Text 时使用）。</summary>
    public string Text = "";

    /// <summary>字号（磅，PDF 排版单位约 1/72 英寸）。</summary>
    public float FontSize = 12f;

    public bool Bold;
    public bool Italic;
    public Color Color = Color.Black;
    public PdfAlign Align = PdfAlign.Left;

    /// <summary>为 true 时在首行前加 "• "（列表项）。</summary>
    public bool Bullet;

    /// <summary>表格表头（Kind=Table）。</summary>
    public List<string> Headers = new();

    /// <summary>表格数据行（Kind=Table）。</summary>
    public List<List<string>> Rows = new();

    /// <summary>图片（Kind=Image）。</summary>
    public Bitmap? Picture;

    /// <summary>图片最大显示宽度（排版单位）。</summary>
    public float MaxWidth = 500f;

    /// <summary>图片最大显示高度（排版单位）。</summary>
    public float MaxHeight = 400f;
}

/// <summary>
/// 零依赖 PDF 生成器。
///
/// 背景：原先用 iTextSharp（连带 BouncyCastle）生成本地 PDF，但这两个 DLL 内嵌了
/// 资源文件（.afm/.lng/Base64 包），VPet 的代码插件检测无法解析这些资源，会判为
/// 「无法识别资源内容」并阻止整个 MOD 加载，因此改为自绘：
/// 用 GDI+ 按 A4 版面排好内容 → 渲染成位图 → 逐页写入 PDF（DCTDecode 图片页）。
/// 优点是零第三方依赖、中文渲染完全正确；代价是 PDF 内的文字不可选中/检索。
/// </summary>
public static class PdfDocumentWriter
{
    // 版面用「96dpi 像素」作为单位，一页 A4 ≈ 794 x 1123。
    private const float PageW = 794f;
    private const float PageH = 1123f;
    private const float Margin = 66f;
    private const float ContentW = PageW - Margin * 2;

    // 渲染倍率 1.6 ≈ 154 dpi，保证打印清晰度同时控制文件体积。
    private const float RenderScale = 1.6f;

    // PDF 页面尺寸（磅）。
    private const string MediaBox = "0 0 595.28 841.89";

    /// <summary>
    /// 生成 PDF 文件。
    /// </summary>
    /// <param name="path">输出路径。</param>
    /// <param name="title">文档标题（同时用于页眉）。</param>
    /// <param name="blocks">排版块列表。</param>
    public static void Write(string path, string title, IReadOnlyList<PdfBlock> blocks)
    {
        var canvas = new PageCanvas(title ?? "");
        try
        {
            canvas.Begin();
            foreach (var block in blocks)
            {
                switch (block.Kind)
                {
                    case PdfBlockKind.Table:
                        canvas.DrawTable(block);
                        break;
                    case PdfBlockKind.Image:
                        canvas.DrawImage(block);
                        break;
                    default:
                        canvas.DrawText(block);
                        break;
                }
            }
            canvas.Finish();

            AssemblePdf(path, canvas.Pages);
        }
        finally
        {
            canvas.Dispose();
        }
    }

    /// <summary>把各页 JPEG 打包成 PDF 文件。</summary>
    private static void AssemblePdf(string path, IReadOnlyList<RenderedPage> pages)
    {
        if (pages.Count == 0) return;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var ascii = Encoding.ASCII;
        var offsets = new List<long>();

        void WriteRaw(byte[] data) => fs.Write(data, 0, data.Length);
        void WriteStr(string s) => WriteRaw(ascii.GetBytes(s));
        void BeginObj(int num)
        {
            offsets.Add(fs.Position);
            WriteStr($"{num} 0 obj\n");
        }

        // 头部（第二行是二进制标记，告诉工具这是二进制 PDF）
        WriteStr("%PDF-1.4\n");
        WriteRaw(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });

        int pageCount = pages.Count;
        int firstPageObj = 3;

        // 1: Catalog
        BeginObj(1);
        WriteStr("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2: Pages
        BeginObj(2);
        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++)
            kids.Append($"{firstPageObj + i * 3} 0 R ");
        WriteStr($"<< /Type /Pages /Count {pageCount} /Kids [ {kids.ToString().Trim()} ] >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            var page = pages[i];
            int pageObj = firstPageObj + i * 3;
            int contentObj = pageObj + 1;
            int imageObj = pageObj + 2;

            // Page
            BeginObj(pageObj);
            WriteStr($"<< /Type /Page /Parent 2 0 R /MediaBox [ {MediaBox} ] " +
                     $"/Resources << /XObject << /Im0 {imageObj} 0 R >> /ProcSet [ /PDF /ImageC ] >> " +
                     $"/Contents {contentObj} 0 R >>\nendobj\n");

            // Content（把图片铺满整页）
            string content = $"q 595.28 0 0 841.89 0 0 cm /Im0 Do Q\n";
            byte[] contentBytes = ascii.GetBytes(content);
            BeginObj(contentObj);
            WriteStr($"<< /Length {contentBytes.Length} >>\nstream\n");
            WriteRaw(contentBytes);
            WriteStr("endstream\nendobj\n");

            // Image XObject
            BeginObj(imageObj);
            WriteStr($"<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} " +
                     $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode " +
                     $"/Length {page.Jpeg.Length} >>\nstream\n");
            WriteRaw(page.Jpeg);
            WriteStr("\nendstream\nendobj\n");
        }

        long xrefPos = fs.Position;
        WriteStr($"xref\n0 {offsets.Count + 1}\n");
        WriteStr("0000000000 65535 f \n");
        foreach (var off in offsets)
            WriteStr($"{off:D10} 00000 n \n");

        WriteStr($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
    }

    private sealed class RenderedPage
    {
        public byte[] Jpeg = Array.Empty<byte>();
        public int Width;
        public int Height;
    }

    /// <summary>逐页排版 + 渲染的画布。</summary>
    private sealed class PageCanvas : IDisposable
    {
        private readonly List<RenderedPage> _pages = new();
        private readonly string _title;

        private Bitmap? _bitmap;
        private Graphics? _graphics;
        private Graphics? _measure;
        private float _y;
        private int _pageIndex;

        private readonly string _fontFamily;
        private readonly Font _headerFont;

        private float Bottom => PageH - Margin - 34f;

        public IReadOnlyList<RenderedPage> Pages => _pages;

        public PageCanvas(string title)
        {
            _title = title;
            _fontFamily = PickFontFamily();
            _headerFont = new Font(_fontFamily, 9f, FontStyle.Regular, GraphicsUnit.Point);
            _measure = Graphics.FromImage(new Bitmap(4, 4));
            _measure.TextRenderingHint = TextRenderingHint.AntiAlias;
        }

        /// <summary>选一个系统里存在的中文字体。</summary>
        private static string PickFontFamily()
        {
            string[] candidates =
            {
                "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑",
                "SimHei", "DengXian", "SimSun"
            };
            var installed = FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in candidates)
                if (installed.Contains(name)) return name;
            return SystemFonts.DefaultFont.FontFamily.Name;
        }

        public void Begin() => StartPage();

        public void Finish() => EndPage();

        private void StartPage()
        {
            _pageIndex++;
            _bitmap = new Bitmap((int)(PageW * RenderScale), (int)(PageH * RenderScale));
            _graphics = Graphics.FromImage(_bitmap);
            _graphics.Clear(Color.White);
            _graphics.SmoothingMode = SmoothingMode.AntiAlias;
            _graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            _graphics.ScaleTransform(RenderScale, RenderScale);
            _graphics.PageUnit = GraphicsUnit.Pixel;

            // 页眉：标题 + 分隔线
            using (var titleBrush = new SolidBrush(Color.FromArgb(120, 120, 120)))
                _graphics.DrawString(_title, _headerFont, titleBrush, Margin, Margin - 22f);

            using (var pen = new Pen(Color.FromArgb(200, 200, 200), 0.8f))
                _graphics.DrawLine(pen, Margin, Margin, PageW - Margin, Margin);

            _y = Margin + 18f;
        }

        private void EndPage()
        {
            if (_graphics == null || _bitmap == null) return;

            // 页脚：页码
            using (var footerBrush = new SolidBrush(Color.FromArgb(130, 130, 130)))
            {
                string footer = $"第 {_pageIndex} 页";
                var size = _graphics.MeasureString(footer, _headerFont);
                _graphics.DrawString(footer, _headerFont, footerBrush,
                    (PageW - size.Width) / 2f, PageH - Margin - 6f);
            }

            byte[] jpeg = EncodeJpeg(_bitmap);
            _pages.Add(new RenderedPage { Jpeg = jpeg, Width = _bitmap.Width, Height = _bitmap.Height });

            _graphics.Dispose();
            _bitmap.Dispose();
            _graphics = null;
            _bitmap = null;
        }

        private static byte[] EncodeJpeg(Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
            bitmap.Save(ms, encoder, parameters);
            return ms.ToArray();
        }

        /// <summary>空间不够就换页。</summary>
        private void EnsureSpace(float height)
        {
            if (_y + height <= Bottom) return;
            EndPage();
            StartPage();
        }

        private Font MakeFont(float pt, bool bold, bool italic)
        {
            var style = FontStyle.Regular;
            if (bold) style |= FontStyle.Bold;
            if (italic) style |= FontStyle.Italic;
            return new Font(_fontFamily, pt, style, GraphicsUnit.Point);
        }

        private float LineHeight(Font font) => font.GetHeight(_measure) + 2f;

        /// <summary>按可用宽度做贪心断行（中英文混排都按字符计算，遇到空格优先断行）。</summary>
        private List<string> WrapText(string text, Font font, float maxWidth)
        {
            var result = new List<string>();
            if (maxWidth <= 1f) maxWidth = ContentW;

            foreach (var paragraph in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (paragraph.Length == 0)
                {
                    result.Add("");
                    continue;
                }

                var current = new StringBuilder();
                int lastSpace = -1;

                foreach (char ch in paragraph)
                {
                    current.Append(ch);
                    if (ch == ' ') lastSpace = current.Length - 1;

                    if (_measure.MeasureString(current.ToString(), font).Width <= maxWidth) continue;

                    // 超宽：回退到最近的空格断行，否则直接按字符断
                    if (lastSpace > 0 && lastSpace < current.Length - 1)
                    {
                        string head = current.ToString(0, lastSpace);
                        string tail = current.ToString(lastSpace + 1, current.Length - lastSpace - 1);
                        result.Add(head);
                        current.Clear();
                        current.Append(tail);
                    }
                    else
                    {
                        current.Length--;
                        result.Add(current.ToString());
                        current.Clear();
                        current.Append(ch);
                    }
                    lastSpace = -1;
                }

                if (current.Length > 0) result.Add(current.ToString());
            }

            return result;
        }

        /// <summary>绘制一段文本（自动换行 / 分页 / 对齐）。</summary>
        public void DrawText(PdfBlock block)
        {
            using var font = MakeFont(block.FontSize, block.Bold, block.Italic);
            using var brush = new SolidBrush(block.Color);

            float indent = block.Bullet ? 18f : 0f;
            float lineHeight = LineHeight(font);
            float availWidth = ContentW - indent;

            string text = block.Text ?? "";
            var wrapped = WrapText(block.Bullet ? "• " + text : text, font, availWidth);

            for (int i = 0; i < wrapped.Count; i++)
            {
                EnsureSpace(lineHeight);

                string line = wrapped[i];
                if (line.Length == 0)
                {
                    _y += lineHeight * 0.45f;
                    continue;
                }

                float lineWidth = _measure.MeasureString(line, font).Width;
                float x = block.Align switch
                {
                    PdfAlign.Center => Margin + (ContentW - lineWidth) / 2f,
                    PdfAlign.Right => Margin + ContentW - lineWidth,
                    PdfAlign.Justify => Margin + indent,
                    _ => Margin + indent
                };

                bool isLastLine = i == wrapped.Count - 1;
                if (block.Align == PdfAlign.Justify && !isLastLine && lineWidth < availWidth - 2f)
                    DrawJustifiedLine(line, font, brush, Margin + indent, _y, availWidth);
                else
                    _graphics!.DrawString(line, font, brush, x, _y);

                _y += lineHeight;
            }

            _y += block.FontSize * 0.35f + 4f;
        }

        /// <summary>两端对齐：按逐字宽度等比拉伸，保证行尾正好落在右边界。</summary>
        private void DrawJustifiedLine(string line, Font font, Brush brush, float x, float y, float width)
        {
            if (line.Length <= 1)
            {
                _graphics!.DrawString(line, font, brush, x, y);
                return;
            }

            // 逐字宽度累加和与整体测量值不完全一致（字距、边距），
            // 这里用缩放系数把整行压/拉到目标宽度，避免溢出右边界。
            var widths = new float[line.Length];
            float total = 0f;
            for (int i = 0; i < line.Length; i++)
            {
                widths[i] = _measure.MeasureString(line[i].ToString(), font).Width;
                total += widths[i];
            }

            if (total <= 0f)
            {
                _graphics!.DrawString(line, font, brush, x, y);
                return;
            }

            float scale = width / total;
            float cursor = x;
            for (int i = 0; i < line.Length; i++)
            {
                _graphics!.DrawString(line[i].ToString(), font, brush, cursor, y);
                cursor += widths[i] * scale;
            }
        }

        /// <summary>绘制表格（表头灰底、隔行浅灰、细边框，跨页自动重复表头）。</summary>
        public void DrawTable(PdfBlock block)
        {
            int colCount = block.Headers.Count;
            foreach (var row in block.Rows) colCount = Math.Max(colCount, row.Count);
            if (colCount == 0) return;

            using var headerFont = MakeFont(11f, true, false);
            using var cellFont = MakeFont(11f, false, false);
            using var headerBrush = new SolidBrush(Color.Black);
            using var cellBrush = new SolidBrush(Color.Black);
            using var headerBg = new SolidBrush(Color.FromArgb(230, 230, 230));
            using var altBg = new SolidBrush(Color.FromArgb(248, 248, 248));
            using var gridPen = new Pen(Color.FromArgb(180, 180, 180), 0.8f);

            const float pad = 6f;
            float colW = ContentW / colCount;

            void DrawRow(IReadOnlyList<string> cells, bool isHeader, bool alternate)
            {
                int lineCount = 1;
                var wrappedCells = new List<List<string>>();
                for (int c = 0; c < colCount; c++)
                {
                    string text = c < cells.Count ? cells[c] ?? "" : "";
                    var lines = WrapText(text, isHeader ? headerFont : cellFont, colW - pad * 2);
                    if (lines.Count == 0) lines.Add("");
                    wrappedCells.Add(lines);
                    lineCount = Math.Max(lineCount, lines.Count);
                }

                float fontLine = LineHeight(isHeader ? headerFont : cellFont);
                float rowHeight = lineCount * fontLine + pad * 2;

                EnsureSpace(rowHeight);

                var rect = new RectangleF(Margin, _y, ContentW, rowHeight);
                if (isHeader) _graphics!.FillRectangle(headerBg, rect);
                else if (alternate) _graphics!.FillRectangle(altBg, rect);

                for (int c = 0; c < colCount; c++)
                {
                    float cx = Margin + c * colW + pad;
                    float cy = _y + pad;
                    foreach (var line in wrappedCells[c])
                    {
                        _graphics!.DrawString(line, isHeader ? headerFont : cellFont,
                            isHeader ? headerBrush : cellBrush, cx, cy);
                        cy += fontLine;
                    }
                    _graphics!.DrawLine(gridPen, Margin + c * colW, _y, Margin + c * colW, _y + rowHeight);
                }

                _graphics!.DrawRectangle(gridPen, Margin, _y, ContentW, rowHeight);
                _y += rowHeight;
            }

            _y += 4f;
            if (block.Headers.Count > 0) DrawRow(block.Headers, true, false);

            bool alternate = false;
            foreach (var row in block.Rows)
            {
                DrawRow(row, false, alternate);
                alternate = !alternate;
            }
            _y += 10f;
        }

        /// <summary>绘制图片（等比缩放到最大宽高以内）。</summary>
        public void DrawImage(PdfBlock block)
        {
            if (block.Picture == null) return;

            float maxW = Math.Min(block.MaxWidth <= 0 ? ContentW : block.MaxWidth, ContentW);
            float maxH = block.MaxHeight <= 0 ? 400f : block.MaxHeight;

            float w = block.Picture.Width;
            float h = block.Picture.Height;
            float ratio = Math.Min(1f, Math.Min(maxW / w, maxH / h));
            w *= ratio;
            h *= ratio;

            EnsureSpace(h + 10f);
            _graphics!.DrawImage(block.Picture, Margin, _y, w, h);
            _y += h + 12f;
        }

        public void Dispose()
        {
            _graphics?.Dispose();
            _bitmap?.Dispose();
            _measure?.Dispose();
            _headerFont.Dispose();
        }
    }
}
