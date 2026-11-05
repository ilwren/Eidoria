using System.IO.Compression;
using System.Text;

namespace MdExplorer.Screenshots;

/// <summary>截图用样例文件树：与布局文档固定样例数据同名，含手写 PNG「照片」。</summary>
internal static class SampleData
{
    private static readonly object Gate = new();
    private static string? _root;

    public static string Create()
    {
        lock (Gate)
        {
            if (_root is not null) return _root;
            var root = Path.Combine(Path.GetTempPath(), "mdexplorer-sample");
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);

            var pictures = Path.Combine(root, "Pictures");
            var camera = Path.Combine(pictures, "Camera");
            var documents = Path.Combine(root, "Documents");
            Directory.CreateDirectory(camera);
            Directory.CreateDirectory(documents);
            Directory.CreateDirectory(Path.Combine(root, "Downloads"));

            File.WriteAllBytes(Path.Combine(pictures, "IMG_20261001.jpg.png"), Array.Empty<byte>()); // 占位将被下方真实 PNG 以 jpg 命名替代
            File.Delete(Path.Combine(pictures, "IMG_20261001.jpg.png"));
            WritePhotoPng(Path.Combine(pictures, "IMG_20261001.jpg"), 0);
            WritePhotoPng(Path.Combine(pictures, "海边日落.png"), 1);
            WritePhotoPng(Path.Combine(pictures, "screenshot_ide.png"), 2);
            WritePhotoPng(Path.Combine(camera, "IMG_0001.png"), 3);

            File.WriteAllText(Path.Combine(pictures, "Program.cs"),
                "// MdExplorer 样例源文件\nusing Avalonia;\n\ninternal static class Program\n{\n    [STAThread]\n    public static void Main(string[] args)\n        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);\n\n    public static AppBuilder BuildAvaloniaApp()\n        => AppBuilder.Configure<App>()\n            .UsePlatformDetect()\n            .WithInterFont()\n            .LogToTrace();\n}\n");
            File.WriteAllText(Path.Combine(pictures, "README.md"),
                "# MdExplorer\n\n跨平台本地文件资源管理器（Avalonia 12 + .NET 10 + Material Design 3）。\n\n- 五档断点响应式布局\n- 预览与递归搜索\n- Native AOT 发布\n");
            File.WriteAllText(Path.Combine(pictures, "会议纪要.txt"),
                "会议纪要（样例）\n\n1. 决策：许可证采用 Apache-2.0\n2. Android 全文件变体稍后做\n3. 媒体播放后续版本跟进\n");
            WriteSamplePdf(Path.Combine(pictures, "设计规范 v3.pdf"));
            File.WriteAllBytes(Path.Combine(pictures, "演示视频.mp4"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(pictures, "录音 001.m4a"), new byte[4096]);
            File.WriteAllText(Path.Combine(pictures, "数据.csv"), "名称,大小\n报告,128\n图片,4096\n");
            WriteSampleDocx(Path.Combine(documents, "报告-终版.docx"));
            WriteSamplePptx(Path.Combine(documents, "产品发布会.pptx"));
            WriteSampleXlsx(Path.Combine(documents, "预算表.xlsx"));
            WriteSampleOdt(Path.Combine(documents, "采购方案.odt"));
            WriteSampleOds(Path.Combine(documents, "季度销量.ods"));
            File.WriteAllText(Path.Combine(documents, "todo.txt"), "- [ ] 评审布局稿\n- [x] 定版 API\n");

            using (var stream = File.Create(Path.Combine(pictures, "backup-2026.zip")))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void AddText(string entryName, string content)
                {
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var writer = entry.Open();
                    writer.Write(Encoding.UTF8.GetBytes(content));
                }

                AddText("docs/readme.txt", string.Join("\n", Enumerable.Repeat("ZIP 工作台样例 · 免整包解压预览", 24)));
                AddText("docs/config.json", "{\n  \"theme\": \"teal\",\n  \"view\": \"grid\",\n  \"archive\": true\n}\n");
                AddText("docs/CHANGELOG.md", "# 备份说明\n\n- 保留目录结构\n- 同名文件两者保留\n- 可只解压所选条目\n");
                AddText("docs/LICENSE.txt", "Sample archive license notes for layout screenshots.\n");
                AddText("docs/guide/start.txt", "从压缩包根目录进入 docs，再进入 guide。\n");
                AddText("photos/photo-list.txt", "IMG_20261001.jpg\n海边日落.png\nscreenshot_ide.png\n");
                AddText("notes/readme.txt", "backup notes\n- 保留目录结构\n- 同名文件两者保留\n");
                zip.CreateEntry("empty/");

                var photoEntry = zip.CreateEntry("photos/IMG_20261001.jpg", CompressionLevel.Optimal);
                using var photoWriter = photoEntry.Open();
                photoWriter.Write(File.ReadAllBytes(Path.Combine(pictures, "IMG_20261001.jpg")));
            }

            var baseTime = new DateTime(2026, 10, 1, 14, 22, 0, DateTimeKind.Local);
            var i = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetLastWriteTime(file, baseTime.AddDays(-3.0 * i++));

            Directory.CreateDirectory(Path.Combine(root, "Music"));
            return _root = root;
        }
    }

    /// <summary>生成一张风景示意 PNG（渐变海面 + 太阳 + 山丘），供缩略图/预览截图出真实图。</summary>
    private static void WritePhotoPng(string path, int variant)
    {
        const int w = 640, h = 420;
        var pixels = new byte[h * (1 + w * 3)];
        (byte r, byte g, byte b) Sky(int y)
        {
            double t = y / (double)h;
            return variant switch
            {
                1 => ((byte)(250 - 120 * t), (byte)(160 - 60 * t), (byte)(90 + 40 * t)),
                2 => ((byte)(28 + 30 * t), (byte)(32 + 26 * t), (byte)(52 + 30 * t)),
                _ => ((byte)(110 - 40 * t), (byte)(200 - 60 * t), (byte)(205 - 35 * t)),
            };
        }
        for (int y = 0; y < h; y++)
        {
            var row = y * (1 + w * 3);
            pixels[row] = 0; // filter: None
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = Sky(y);
                var horizon = h * 0.62 + Math.Sin(x / 55.0 + variant) * 14;
                if (y > horizon) { r = (byte)(r * 0.35); g = (byte)(g * 0.55); b = (byte)(b * 0.5); }
                var sunX = w * (0.68 + variant * 0.05);
                var sunY = h * 0.30;
                var d = Math.Sqrt((x - sunX) * (x - sunX) + (y - sunY) * (y - sunY));
                if (d < 42) { r = 255; g = 226; b = 150; }
                var offset = row + 1 + x * 3;
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
            }
        }

        using var stream = File.Create(path);
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // PNG 签名须用原始字节：u8 字面量会把 \x89 当作字符再 UTF-8 编码为 C2 89
        WriteChunk(stream, "IHDR", BuildIhdr(w, h));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(pixels);
        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteOpenXmlPackage(string path, params (string Entry, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = entry.Open();
            writer.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    private static string OpenXmlContentTypes(params (string Part, string Type)[] overrides)
    {
        var body = string.Join("\n", overrides.Select(item =>
            $"  <Override PartName=\"{item.Part}\" ContentType=\"{item.Type}\" />"));
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
              <Default Extension="xml" ContentType="application/xml" />
            {body}
            </Types>
            """;
    }

    private static string OpenXmlRelationships(params (string Id, string Type, string Target)[] values)
    {
        var body = string.Join("\n", values.Select(item =>
            $"  <Relationship Id=\"{item.Id}\" Type=\"{item.Type}\" Target=\"{item.Target}\" />"));
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            {body}
            </Relationships>
            """;
    }

    /// <summary>真实 DOCX 样例：样式、列表式段落与表格均走 Open XML 包，不再使用伪造文本字节。</summary>
    private static void WriteSampleDocx(string path)
    {
        const string styles = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:docDefaults>
                <w:rPrDefault><w:rPr>
                  <w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Microsoft YaHei" />
                  <w:sz w:val="22" /><w:color w:val="1F2937" />
                </w:rPr></w:rPrDefault>
              </w:docDefaults>
              <w:style w:type="paragraph" w:default="1" w:styleId="Normal">
                <w:name w:val="Normal" /><w:qFormat />
                <w:pPr><w:spacing w:after="120" w:line="276" w:lineRule="auto" /></w:pPr>
                <w:rPr><w:rFonts w:ascii="Calibri" w:eastAsia="Microsoft YaHei" /><w:sz w:val="22" /></w:rPr>
              </w:style>
              <w:style w:type="paragraph" w:styleId="Title">
                <w:name w:val="Title" /><w:basedOn w:val="Normal" /><w:qFormat />
                <w:pPr><w:jc w:val="center" /><w:spacing w:before="360" w:after="180" /></w:pPr>
                <w:rPr><w:b /><w:sz w:val="56" /><w:color w:val="0F4C5C" /></w:rPr>
              </w:style>
              <w:style w:type="paragraph" w:styleId="Heading1">
                <w:name w:val="heading 1" /><w:basedOn w:val="Normal" /><w:qFormat />
                <w:pPr><w:spacing w:before="360" w:after="120" /></w:pPr>
                <w:rPr><w:b /><w:sz w:val="32" /><w:color w:val="0F766E" /></w:rPr>
              </w:style>
            </w:styles>
            """;
        const string document = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:pPr><w:jc w:val="center" /><w:spacing w:before="240" w:after="60" /></w:pPr>
                  <w:r><w:rPr><w:b /><w:sz w:val="20" /><w:color w:val="0F766E" /></w:rPr><w:t>EIDORIA · 产品报告</w:t></w:r></w:p>
                <w:p><w:pPr><w:pStyle w:val="Title" /></w:pPr><w:r><w:t>万象屿季度报告</w:t></w:r></w:p>
                <w:p><w:pPr><w:jc w:val="center" /><w:spacing w:after="420" /></w:pPr>
                  <w:r><w:rPr><w:sz w:val="22" /><w:color w:val="64748B" /></w:rPr><w:t>跨平台文件体验 · 2026 年第四季度</w:t></w:r></w:p>
                <w:p><w:pPr><w:pStyle w:val="Heading1" /></w:pPr><w:r><w:t>执行摘要</w:t></w:r></w:p>
                <w:p><w:r><w:t>本季度重点把文件浏览、Office 预览与 PDF 阅读收敛为一套连续体验。用户可以在不离开 Eidoria 的情况下先判断文档内容，再决定使用系统应用进行完整编辑。</w:t></w:r></w:p>
                <w:p><w:pPr><w:ind w:left="360" w:hanging="220" /></w:pPr><w:r><w:t>• DOCX、PPTX 与 XLSX 使用原生只读控件连续预览。</w:t></w:r></w:p>
                <w:p><w:pPr><w:ind w:left="360" w:hanging="220" /></w:pPr><w:r><w:t>• PDF 继续由 PDFium 提供密码、旋转、缩略图与连续阅读。</w:t></w:r></w:p>
                <w:p><w:pPr><w:ind w:left="360" w:hanging="220" /></w:pPr><w:r><w:t>• 所有文档都先经过字节、展开量与结构复杂度预检。</w:t></w:r></w:p>
                <w:p><w:pPr><w:pStyle w:val="Heading1" /></w:pPr><w:r><w:t>里程碑</w:t></w:r></w:p>
                <w:tbl>
                  <w:tr>
                    <w:tc><w:tcPr><w:shd w:fill="0F766E" /></w:tcPr><w:p><w:r><w:rPr><w:b /><w:color w:val="FFFFFF" /></w:rPr><w:t>阶段</w:t></w:r></w:p></w:tc>
                    <w:tc><w:tcPr><w:shd w:fill="0F766E" /></w:tcPr><w:p><w:r><w:rPr><w:b /><w:color w:val="FFFFFF" /></w:rPr><w:t>目标</w:t></w:r></w:p></w:tc>
                    <w:tc><w:tcPr><w:shd w:fill="0F766E" /></w:tcPr><w:p><w:r><w:rPr><w:b /><w:color w:val="FFFFFF" /></w:rPr><w:t>状态</w:t></w:r></w:p></w:tc>
                  </w:tr>
                  <w:tr>
                    <w:tc><w:p><w:r><w:t>核心浏览</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>双窗格与任务中心</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>完成</w:t></w:r></w:p></w:tc>
                  </w:tr>
                  <w:tr>
                    <w:tc><w:p><w:r><w:t>文档预览</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>Office + PDF</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>本版交付</w:t></w:r></w:p></w:tc>
                  </w:tr>
                </w:tbl>
                <w:p><w:r><w:br w:type="page" /></w:r></w:p>
                <w:p><w:pPr><w:pStyle w:val="Heading1" /></w:pPr><w:r><w:t>下一阶段</w:t></w:r></w:p>
                <w:p><w:r><w:t>继续评估 PDF 目录与文本层搜索；Office 保持只读定位，不在文件管理器里冒充完整办公套件。</w:t></w:r></w:p>
                <w:sectPr>
                  <w:pgSz w:w="11906" w:h="16838" />
                  <w:pgMar w:top="1134" w:right="1247" w:bottom="1134" w:left="1247" w:header="720" w:footer="720" />
                </w:sectPr>
              </w:body>
            </w:document>
            """;

        WriteOpenXmlPackage(path,
            ("[Content_Types].xml", OpenXmlContentTypes(
                ("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
                ("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"))),
            ("_rels/.rels", OpenXmlRelationships(
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "word/document.xml"))),
            ("word/document.xml", document),
            ("word/styles.xml", styles),
            ("word/_rels/document.xml.rels", OpenXmlRelationships(
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml"))));
    }

    private static string PptShape(
        int id,
        string name,
        long x,
        long y,
        long width,
        long height,
        string geometry,
        string? fill,
        string paragraphs,
        string anchor = "t")
    {
        var fillXml = fill is null ? string.Empty : $"<a:solidFill><a:srgbClr val=\"{fill}\" /></a:solidFill>";
        return $"""
            <p:sp>
              <p:nvSpPr><p:cNvPr id="{id}" name="{name}" /><p:cNvSpPr /><p:nvPr /></p:nvSpPr>
              <p:spPr>
                <a:xfrm><a:off x="{x}" y="{y}" /><a:ext cx="{width}" cy="{height}" /></a:xfrm>
                <a:prstGeom prst="{geometry}"><a:avLst /></a:prstGeom>
                {fillXml}
              </p:spPr>
              <p:txBody>
                <a:bodyPr wrap="square" anchor="{anchor}" lIns="91440" tIns="45720" rIns="91440" bIns="45720" />
                <a:lstStyle />
                {paragraphs}
              </p:txBody>
            </p:sp>
            """;
    }

    private static string PptParagraph(string text, int size, string color, bool bold = false, string alignment = "l")
    {
        var boldAttribute = bold ? " b=\"1\"" : string.Empty;
        return $"""
            <a:p><a:pPr algn="{alignment}" />
              <a:r><a:rPr lang="zh-CN" sz="{size}"{boldAttribute}><a:solidFill><a:srgbClr val="{color}" /></a:solidFill></a:rPr><a:t>{text}</a:t></a:r>
            </a:p>
            """;
    }

    /// <summary>真实 PPTX 样例：关系驱动的两张宽屏幻灯片、形状、文本与主题色。</summary>
    private static void WriteSamplePptx(string path)
    {
        var slide1 = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                   xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:cSld>
                <p:bg><p:bgPr><a:solidFill><a:srgbClr val="F3F7F7" /></a:solidFill></p:bgPr></p:bg>
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name="" /><p:cNvGrpSpPr /><p:nvPr /></p:nvGrpSpPr><p:grpSpPr />
                  {PptShape(2, "Top band", 0, 0, 12192000, 914400, "rect", "0F4C5C", PptParagraph("Eidoria · 万象屿", 1800, "FFFFFF", true), "ctr")}
                  {PptShape(3, "Title", 685800, 1371600, 7315200, 914400, "rect", null, PptParagraph("让本地文件各归其屿", 4000, "0F4C5C", true), "ctr")}
                  {PptShape(4, "Subtitle", 685800, 2377440, 6858000, 548640, "rect", null, PptParagraph("Windows · Linux · Android 的 Material Design 3 文件管理器", 1900, "0F766E", true), "ctr")}
                  {PptShape(5, "Bullets", 685800, 3291840, 6400800, 1828800, "rect", null,
                      PptParagraph("• 五档真实响应式布局", 2000, "1F2937") +
                      PptParagraph("• Office 与 PDF 原生预览", 2000, "1F2937") +
                      PptParagraph("• Native AOT 与本地优先", 2000, "1F2937"))}
                  {PptShape(6, "Stats", 8229600, 1508760, 3200400, 3657600, "roundRect", "FFFFFF",
                      PptParagraph("v0.23", 3800, "0F766E", true, "ctr") +
                      PptParagraph("DOCX · PPTX · XLSX", 1800, "334155", true, "ctr") +
                      PptParagraph("PDFium + OfficeViewer", 1500, "64748B", false, "ctr"), "ctr")}
                  {PptShape(7, "Accent", 10515600, 5486400, 914400, 914400, "ellipse", "F59E0B", string.Empty)}
                </p:spTree>
              </p:cSld>
            </p:sld>
            """;
        var slide2 = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                   xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:cSld>
                <p:bg><p:bgPr><a:solidFill><a:srgbClr val="FFFFFF" /></a:solidFill></p:bgPr></p:bg>
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name="" /><p:cNvGrpSpPr /><p:nvPr /></p:nvGrpSpPr><p:grpSpPr />
                  {PptShape(2, "Title", 685800, 548640, 10058400, 822960, "rect", null, PptParagraph("文档工作台", 3400, "0F4C5C", true), "ctr")}
                  {PptShape(3, "Card A", 685800, 1828800, 3200400, 3200400, "roundRect", "E6F4F1",
                      PptParagraph("文本", 2600, "0F766E", true, "ctr") + PptParagraph("轻编辑与安全保存", 1600, "334155", false, "ctr"), "ctr")}
                  {PptShape(4, "Card B", 4480560, 1828800, 3200400, 3200400, "roundRect", "FFF7E6",
                      PptParagraph("Office", 2600, "B45309", true, "ctr") + PptParagraph("原生只读连续预览", 1600, "334155", false, "ctr"), "ctr")}
                  {PptShape(5, "Card C", 8275320, 1828800, 3200400, 3200400, "roundRect", "EEF2FF",
                      PptParagraph("PDF", 2600, "3730A3", true, "ctr") + PptParagraph("单页与连续阅读", 1600, "334155", false, "ctr"), "ctr")}
                  {PptShape(6, "Footer", 685800, 5669280, 10058400, 548640, "rect", null, PptParagraph("复杂文档仍可一键交给系统应用完整编辑", 1700, "64748B", false, "ctr"), "ctr")}
                </p:spTree>
              </p:cSld>
            </p:sld>
            """;
        const string theme = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Eidoria">
              <a:themeElements>
                <a:clrScheme name="Eidoria">
                  <a:dk1><a:sysClr val="windowText" lastClr="000000" /></a:dk1>
                  <a:lt1><a:sysClr val="window" lastClr="FFFFFF" /></a:lt1>
                  <a:dk2><a:srgbClr val="0F4C5C" /></a:dk2>
                  <a:lt2><a:srgbClr val="F3F7F7" /></a:lt2>
                  <a:accent1><a:srgbClr val="0F766E" /></a:accent1>
                  <a:accent2><a:srgbClr val="F59E0B" /></a:accent2>
                  <a:accent3><a:srgbClr val="3730A3" /></a:accent3>
                  <a:accent4><a:srgbClr val="64748B" /></a:accent4>
                  <a:accent5><a:srgbClr val="334155" /></a:accent5>
                  <a:accent6><a:srgbClr val="B45309" /></a:accent6>
                  <a:hlink><a:srgbClr val="0F766E" /></a:hlink>
                  <a:folHlink><a:srgbClr val="0F4C5C" /></a:folHlink>
                </a:clrScheme>
                <a:fontScheme name="Eidoria"><a:majorFont><a:latin typeface="Calibri" /><a:ea typeface="Microsoft YaHei" /></a:majorFont><a:minorFont><a:latin typeface="Calibri" /><a:ea typeface="Microsoft YaHei" /></a:minorFont></a:fontScheme>
                <a:fmtScheme name="Eidoria"><a:fillStyleLst /><a:lnStyleLst /><a:effectStyleLst /><a:bgFillStyleLst /></a:fmtScheme>
              </a:themeElements>
            </a:theme>
            """;
        const string presentation = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldIdLst><p:sldId id="256" r:id="rId1" /><p:sldId id="257" r:id="rId2" /></p:sldIdLst>
              <p:sldSz cx="12192000" cy="6858000" type="wide" />
              <p:notesSz cx="6858000" cy="9144000" />
            </p:presentation>
            """;

        WriteOpenXmlPackage(path,
            ("[Content_Types].xml", OpenXmlContentTypes(
                ("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
                ("/ppt/slides/slide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"),
                ("/ppt/slides/slide2.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"),
                ("/ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml"))),
            ("_rels/.rels", OpenXmlRelationships(
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "ppt/presentation.xml"))),
            ("ppt/presentation.xml", presentation),
            ("ppt/_rels/presentation.xml.rels", OpenXmlRelationships(
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide", "slides/slide1.xml"),
                ("rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide", "slides/slide2.xml"),
                ("rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme", "theme/theme1.xml"))),
            ("ppt/slides/slide1.xml", slide1),
            ("ppt/slides/slide2.xml", slide2),
            ("ppt/theme/theme1.xml", theme));
    }

    /// <summary>真实 XLSX 样例：两张表、共享关系、样式、数字格式与合并标题。</summary>
    private const string OdfNamespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
        "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
        "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
        "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
        "xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    /// <summary>写一个最小可用的 ODF 包（mimetype + META-INF/manifest.xml + content.xml）。</summary>
    private static void WriteOpenDocument(string path, string mimetype, string officeBodyName, string body)
    {
        var content =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<office:document-content {OdfNamespaces} office:version=\"1.2\">" +
            $"<office:body><office:{officeBodyName}>{body}</office:{officeBodyName}></office:body>" +
            "</office:document-content>";
        var manifest =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\">" +
            $"<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"{mimetype}\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
            "</manifest:manifest>";

        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), Encoding.ASCII))
            writer.Write(mimetype);
        using (var writer = new StreamWriter(zip.CreateEntry("META-INF/manifest.xml").Open(), new UTF8Encoding(false)))
            writer.Write(manifest);
        using (var writer = new StreamWriter(zip.CreateEntry("content.xml").Open(), new UTF8Encoding(false)))
            writer.Write(content);
    }

    /// <summary>ODT 样例：标题层级 + 正文 + 列表 + 表格，正好覆盖内置只读器还原的结构。</summary>
    private static void WriteSampleOdt(string path) => WriteOpenDocument(
        path,
        "application/vnd.oasis.opendocument.text",
        "text",
        """
        <text:h text:outline-level="1">办公耗材采购方案</text:h>
        <text:p>本方案覆盖 2026 年第四季度的打印耗材、线材与备件，目标是 <text:span text:style-name="Emphasis">在预算内一次备齐</text:span>。</text:p>
        <text:h text:outline-level="2">采购清单</text:h>
        <text:list>
          <text:list-item><text:p>激光打印机硒鼓 × 6</text:p></text:list-item>
          <text:list-item><text:p>USB-C 线材 × 20</text:p></text:list-item>
          <text:list-item><text:p>机械键盘 × 4</text:p></text:list-item>
        </text:list>
        <text:h text:outline-level="2">预算分配</text:h>
        <table:table table:name="预算">
          <table:table-row>
            <table:table-cell><text:p>项目</text:p></table:table-cell>
            <table:table-cell><text:p>数量</text:p></table:table-cell>
            <table:table-cell><text:p>金额（元）</text:p></table:table-cell>
          </table:table-row>
          <table:table-row>
            <table:table-cell><text:p>硒鼓</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="6"><text:p>6</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="4200"><text:p>4,200</text:p></table:table-cell>
          </table:table-row>
          <table:table-row>
            <table:table-cell><text:p>线材</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="20"><text:p>20</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="980"><text:p>980</text:p></table:table-cell>
          </table:table-row>
          <table:table-row>
            <table:table-cell><text:p>键盘</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="4"><text:p>4</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="1560"><text:p>1,560</text:p></table:table-cell>
          </table:table-row>
        </table:table>
        <text:p>合计 6,740 元，留出 10% 作为应急额度。</text:p>
        """);

    /// <summary>ODS 样例：两张工作表的数字与文本混排，用来验证重复列与多表结构。</summary>
    private static void WriteSampleOds(string path) => WriteOpenDocument(
        path,
        "application/vnd.oasis.opendocument.spreadsheet",
        "spreadsheet",
        """
        <table:table table:name="一季度">
          <table:table-row>
            <table:table-cell><text:p>城市</text:p></table:table-cell>
            <table:table-cell><text:p>一月</text:p></table:table-cell>
            <table:table-cell><text:p>二月</text:p></table:table-cell>
            <table:table-cell><text:p>三月</text:p></table:table-cell>
          </table:table-row>
          <table:table-row>
            <table:table-cell><text:p>上海</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="1820"><text:p>1,820</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="1640"><text:p>1,640</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="2050"><text:p>2,050</text:p></table:table-cell>
          </table:table-row>
          <table:table-row>
            <table:table-cell><text:p>杭州</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="960"><text:p>960</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="1120"><text:p>1,120</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="1310"><text:p>1,310</text:p></table:table-cell>
          </table:table-row>
          <table:table-row>
            <table:table-cell><text:p>成都</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="740"><text:p>740</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="820"><text:p>820</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="905"><text:p>905</text:p></table:table-cell>
          </table:table-row>
        </table:table>
        <table:table table:name="季度合计">
          <table:table-row>
            <table:table-cell><text:p>季度</text:p></table:table-cell>
            <table:table-cell><text:p>销量</text:p></table:table-cell>
          </table:table-row>
          <table:table-row>
            <table:table-cell><text:p>Q1</text:p></table:table-cell>
            <table:table-cell office:value-type="float" office:value="12365"><text:p>12,365</text:p></table:table-cell>
          </table:table-row>
        </table:table>
        """);

    private static void WriteSampleXlsx(string path)
    {
        const string styles = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <numFmts count="1"><numFmt numFmtId="164" formatCode="¥#,##0" /></numFmts>
              <fonts count="4">
                <font><sz val="11" /><color rgb="FF1F2937" /><name val="Microsoft YaHei" /></font>
                <font><b /><sz val="18" /><color rgb="FFFFFFFF" /><name val="Microsoft YaHei" /></font>
                <font><b /><sz val="11" /><color rgb="FFFFFFFF" /><name val="Microsoft YaHei" /></font>
                <font><b /><sz val="11" /><color rgb="FF0F766E" /><name val="Microsoft YaHei" /></font>
              </fonts>
              <fills count="5">
                <fill><patternFill patternType="none" /></fill>
                <fill><patternFill patternType="gray125" /></fill>
                <fill><patternFill patternType="solid"><fgColor rgb="FF0F766E" /><bgColor rgb="FF0F766E" /></patternFill></fill>
                <fill><patternFill patternType="solid"><fgColor rgb="FFE6F4F1" /><bgColor rgb="FFE6F4F1" /></patternFill></fill>
                <fill><patternFill patternType="solid"><fgColor rgb="FFFFF7E6" /><bgColor rgb="FFFFF7E6" /></patternFill></fill>
              </fills>
              <borders count="2">
                <border><left /><right /><top /><bottom /><diagonal /></border>
                <border>
                  <left style="thin"><color rgb="FFD1D5DB" /></left><right style="thin"><color rgb="FFD1D5DB" /></right>
                  <top style="thin"><color rgb="FFD1D5DB" /></top><bottom style="thin"><color rgb="FFD1D5DB" /></bottom><diagonal />
                </border>
              </borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" /></cellStyleXfs>
              <cellXfs count="7">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" />
                <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="center" vertical="center" /></xf>
                <xf numFmtId="0" fontId="2" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" /></xf>
                <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1" applyAlignment="1"><alignment vertical="center" /></xf>
                <xf numFmtId="164" fontId="3" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center" /></xf>
                <xf numFmtId="10" fontId="3" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center" /></xf>
                <xf numFmtId="0" fontId="0" fillId="3" borderId="1" xfId="0" applyFill="1" applyBorder="1" />
              </cellXfs>
              <cellStyles count="1"><cellStyle name="常规" xfId="0" builtinId="0" /></cellStyles>
            </styleSheet>
            """;
        const string sheet1 = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetFormatPr defaultColWidth="14" defaultRowHeight="18" />
              <cols><col min="1" max="1" width="26" customWidth="1" /><col min="2" max="2" width="18" customWidth="1" /><col min="3" max="3" width="18" customWidth="1" /><col min="4" max="4" width="16" customWidth="1" /></cols>
              <sheetData>
                <row r="1" ht="36" customHeight="1"><c r="A1" s="1" t="inlineStr"><is><t>2026 年第四季度预算</t></is></c></row>
                <row r="2" ht="24" customHeight="1">
                  <c r="A2" s="2" t="inlineStr"><is><t>项目</t></is></c><c r="B2" s="2" t="inlineStr"><is><t>预算</t></is></c>
                  <c r="C2" s="2" t="inlineStr"><is><t>已用</t></is></c><c r="D2" s="2" t="inlineStr"><is><t>进度</t></is></c>
                </row>
                <row r="3"><c r="A3" s="3" t="inlineStr"><is><t>产品设计</t></is></c><c r="B3" s="4"><v>120000</v></c><c r="C3" s="4"><v>86000</v></c><c r="D3" s="5"><v>0.72</v></c></row>
                <row r="4"><c r="A4" s="3" t="inlineStr"><is><t>开发工具</t></is></c><c r="B4" s="4"><v>88000</v></c><c r="C4" s="4"><v>51000</v></c><c r="D4" s="5"><v>0.58</v></c></row>
                <row r="5"><c r="A5" s="3" t="inlineStr"><is><t>设备测试</t></is></c><c r="B5" s="4"><v>64000</v></c><c r="C5" s="4"><v>47500</v></c><c r="D5" s="5"><v>0.74</v></c></row>
                <row r="6"><c r="A6" s="6" t="inlineStr"><is><t>合计</t></is></c><c r="B6" s="4"><v>272000</v></c><c r="C6" s="4"><v>184500</v></c><c r="D6" s="5"><v>0.68</v></c></row>
              </sheetData>
              <mergeCells count="1"><mergeCell ref="A1:D1" /></mergeCells>
            </worksheet>
            """;
        const string sheet2 = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetFormatPr defaultColWidth="16" defaultRowHeight="18" />
              <cols><col min="1" max="1" width="22" customWidth="1" /><col min="2" max="2" width="34" customWidth="1" /><col min="3" max="3" width="16" customWidth="1" /></cols>
              <sheetData>
                <row r="1" ht="32" customHeight="1"><c r="A1" s="1" t="inlineStr"><is><t>预算明细</t></is></c></row>
                <row r="2"><c r="A2" s="2" t="inlineStr"><is><t>类别</t></is></c><c r="B2" s="2" t="inlineStr"><is><t>说明</t></is></c><c r="C2" s="2" t="inlineStr"><is><t>负责人</t></is></c></row>
                <row r="3"><c r="A3" s="3" t="inlineStr"><is><t>设计系统</t></is></c><c r="B3" s="3" t="inlineStr"><is><t>MD3 组件与概念稿</t></is></c><c r="C3" s="3" t="inlineStr"><is><t>设计组</t></is></c></row>
                <row r="4"><c r="A4" s="3" t="inlineStr"><is><t>真机测试</t></is></c><c r="B4" s="3" t="inlineStr"><is><t>Windows 与 Android 验收</t></is></c><c r="C4" s="3" t="inlineStr"><is><t>质量组</t></is></c></row>
                <row r="5"><c r="A5" s="3" t="inlineStr"><is><t>发布</t></is></c><c r="B5" s="3" t="inlineStr"><is><t>签名、截图与发行说明</t></is></c><c r="C5" s="3" t="inlineStr"><is><t>平台组</t></is></c></row>
              </sheetData>
              <mergeCells count="1"><mergeCell ref="A1:C1" /></mergeCells>
            </worksheet>
            """;
        const string workbook = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="预算总览" sheetId="1" r:id="rId1" /><sheet name="明细" sheetId="2" r:id="rId2" /></sheets>
            </workbook>
            """;

        WriteOpenXmlPackage(path,
            ("[Content_Types].xml", OpenXmlContentTypes(
                ("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
                ("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
                ("/xl/worksheets/sheet2.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
                ("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))),
            ("_rels/.rels", OpenXmlRelationships(
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml"))),
            ("xl/workbook.xml", workbook),
            ("xl/_rels/workbook.xml.rels", OpenXmlRelationships(
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml"),
                ("rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet2.xml"),
                ("rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml"))),
            ("xl/worksheets/sheet1.xml", sheet1),
            ("xl/worksheets/sheet2.xml", sheet2),
            ("xl/styles.xml", styles));
    }

    /// <summary>手写一份最小两页 PDF（正确 xref 偏移），让 PDFium 截图测试渲染真实分页而非占位文本。</summary>
    private static void WriteSamplePdf(string path)
    {
        const string page1 = "BT /F1 30 Tf 64 720 Td (Eidoria PDF Preview) Tj /F1 16 Tf 0 -42 Td (Page 1 - layered pages, paths and light.) Tj ET\n";
        const string page2 = "BT /F1 30 Tf 64 720 Td (Eidoria PDF Preview) Tj /F1 16 Tf 0 -42 Td (Page 2 - paging renders on demand.) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 6 0 R >> >> /Contents 7 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(page1)} >>\nstream\n{page1}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(page2)} >>\nstream\n{page2}endstream",
        };

        using var stream = new MemoryStream();
        void WriteAscii(string text) => stream.Write(Encoding.ASCII.GetBytes(text));

        WriteAscii("%PDF-1.4\n");
        var offsets = new long[objects.Length + 1];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = stream.Length;
            WriteAscii($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = stream.Length;
        WriteAscii($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i < offsets.Length; i++)
            WriteAscii($"{offsets[i]:D10} 00000 n \n");
        WriteAscii($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static byte[] BuildIhdr(int w, int h)
    {
        var data = new byte[13];
        WriteBigEndian(data.AsSpan(0, 4), (uint)w);
        WriteBigEndian(data.AsSpan(4, 4), (uint)h);
        data[8] = 8;  // bit depth
        data[9] = 2;  // truecolor
        return data;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> header = stackalloc byte[4];
        WriteBigEndian(header, (uint)data.Length);
        stream.Write(header);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crcSource = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcSource, 0);
        data.CopyTo(crcSource, typeBytes.Length);
        WriteBigEndian(header, Crc32(crcSource));
        stream.Write(header);
    }

    private static void WriteBigEndian(Span<byte> dest, uint value)
    {
        dest[0] = (byte)(value >> 24);
        dest[1] = (byte)(value >> 16);
        dest[2] = (byte)(value >> 8);
        dest[3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFU;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFU;
    }
}
