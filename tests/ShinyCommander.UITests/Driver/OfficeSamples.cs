using System.Diagnostics;
using System.IO.Compression;
using System.Security;

namespace ShinyCommander.UITests.Driver;

/// <summary>Small real Word, Excel and PowerPoint files for the Office previewers - no Office needed.</summary>
public static class OfficeSamples
{
    /// <summary>A .docx from HTML, by macOS's textutil - headings, a list and a table come through as Word styles.</summary>
    public static byte[] Docx(string html)
    {
        var dir = Directory.CreateTempSubdirectory("docx").FullName;
        try
        {
            var source = Path.Combine(dir, "in.html");
            var target = Path.Combine(dir, "out.docx");
            File.WriteAllText(source, html);
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/textutil", ["-convert", "docx", "-output", target, source]) { RedirectStandardError = true })!;
            p.WaitForExit();
            return File.ReadAllBytes(target);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>One sheet; numbers stay numbers, everything else is an inline string.</summary>
    public static byte[] Xlsx(string sheetName, object[][] rows)
    {
        var cells = rows.Select((row, r) =>
            $"<row r=\"{r + 1}\">" + String.Concat(row.Select((value, c) => Cell($"{(char)('A' + c)}{r + 1}", value))) + "</row>");

        return Zip(
            ("[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """),
            ("_rels/.rels", Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml"))),
            ("xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="{SecurityElement.Escape(sheetName)}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml"))),
            ("xl/worksheets/sheet1.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>{String.Concat(cells)}</sheetData>
                </worksheet>
                """)
        );
    }

    static string Cell(string reference, object value) => value is int or long or double or decimal
        ? $"<c r=\"{reference}\"><v>{Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)}</v></c>"
        : $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value.ToString())}</t></is></c>";

    /// <summary>A 16:9 deck, a title and a line of body text per slide - plain text boxes, so no layout placeholders to resolve.</summary>
    public static byte[] Pptx(params (string Title, string Body)[] slides)
    {
        var parts = new List<(string, string)>
        {
            ("[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
                  <Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/>
                  <Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>
                  <Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>
                """ + String.Concat(slides.Select((_, i) => $"""<Override PartName="/ppt/slides/slide{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>""")) + "</Types>"),
            ("_rels/.rels", Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "ppt/presentation.xml"))),
            ("ppt/presentation.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
                  <p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rIdMaster"/></p:sldMasterIdLst>
                  <p:sldIdLst>{String.Concat(slides.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rIdSlide{i + 1}\"/>"))}</p:sldIdLst>
                  <p:sldSz cx="12192000" cy="6858000"/>
                  <p:notesSz cx="6858000" cy="9144000"/>
                </p:presentation>
                """),
            ("ppt/_rels/presentation.xml.rels", Rels([
                ("rIdMaster", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster", "slideMasters/slideMaster1.xml"),
                ("rIdTheme", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme", "theme/theme1.xml"),
                .. slides.Select((_, i) => ($"rIdSlide{i + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide", $"slides/slide{i + 1}.xml"))
            ])),
            ("ppt/slideMasters/slideMaster1.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sldMaster xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
                  <p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld>
                  <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
                  <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst>
                </p:sldMaster>
                """),
            ("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels(
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout", "../slideLayouts/slideLayout1.xml"),
                ("rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme", "../theme/theme1.xml"))),
            ("ppt/slideLayouts/slideLayout1.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sldLayout xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" type="blank">
                  <p:cSld name="Blank"><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld>
                </p:sldLayout>
                """),
            ("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster", "../slideMasters/slideMaster1.xml"))),
            ("ppt/theme/theme1.xml", Theme)
        };

        foreach (var (slide, i) in slides.Select((x, i) => (x, i)))
        {
            parts.Add(($"ppt/slides/slide{i + 1}.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
                  <p:cSld><p:spTree>
                    <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>
                    {TextBox(2, "Title", slide.Title, 914400, 1600200, 4400, true)}
                    {TextBox(3, "Body", slide.Body, 914400, 3200400, 2400, false)}
                  </p:spTree></p:cSld>
                </p:sld>
                """));
            parts.Add(($"ppt/slides/_rels/slide{i + 1}.xml.rels", Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout", "../slideLayouts/slideLayout1.xml"))));
        }
        return Zip([.. parts]);
    }

    static string TextBox(int id, string name, string text, long x, long y, int size, bool bold) => $"""
        <p:sp>
          <p:nvSpPr><p:cNvPr id="{id}" name="{name}"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
          <p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="10363200" cy="1371600"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
          <p:txBody><a:bodyPr wrap="square"/><a:lstStyle/><a:p><a:r><a:rPr lang="en-US" sz="{size}" b="{(bold ? 1 : 0)}"/><a:t>{SecurityElement.Escape(text)}</a:t></a:r></a:p></p:txBody>
        </p:sp>
        """;

    const string Theme = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Office">
          <a:themeElements>
            <a:clrScheme name="Office">
              <a:dk1><a:srgbClr val="000000"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1>
              <a:dk2><a:srgbClr val="1F2937"/></a:dk2><a:lt2><a:srgbClr val="E5E7EB"/></a:lt2>
              <a:accent1><a:srgbClr val="2563EB"/></a:accent1><a:accent2><a:srgbClr val="F59E0B"/></a:accent2>
              <a:accent3><a:srgbClr val="10B981"/></a:accent3><a:accent4><a:srgbClr val="EF4444"/></a:accent4>
              <a:accent5><a:srgbClr val="8B5CF6"/></a:accent5><a:accent6><a:srgbClr val="06B6D4"/></a:accent6>
              <a:hlink><a:srgbClr val="2563EB"/></a:hlink><a:folHlink><a:srgbClr val="7C3AED"/></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="Office">
              <a:majorFont><a:latin typeface="Helvetica Neue"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
              <a:minorFont><a:latin typeface="Helvetica Neue"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
            </a:fontScheme>
            <a:fmtScheme name="Office">
              <a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst>
              <a:lnStyleLst><a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst>
              <a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
              <a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst>
            </a:fmtScheme>
          </a:themeElements>
        </a:theme>
        """;

    static string Rels(params (string Id, string Type, string Target)[] rels) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + String.Concat(rels.Select(x => $"<Relationship Id=\"{x.Id}\" Type=\"{x.Type}\" Target=\"{x.Target}\"/>"))
        + "</Relationships>";

    static byte[] Zip(params (string Name, string Content)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content.TrimStart());
            }
        }
        return ms.ToArray();
    }
}
