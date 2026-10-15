using System.IO.Compression;
using System.Text;
using NOOSE_Website.Services.Import;

namespace NOOSE_Website.Tests.Services.Import;

/// <summary>Google's HTML export folded into a 1:1 original and an editable copy.</summary>
public class GoogleDocConverterTests
{
    /// <summary>Shaped like a real "Webseite (.html, gezippt)" export: one style block, generated classes,
    /// ::before list markers, a comment, a redirect link and a cropped picture - plus things that must not survive.</summary>
    private const string Export = """
        <html><head><meta content="text/html; charset=UTF-8" http-equiv="content-type"><style type="text/css">@import url(https://themes.googleusercontent.com/fonts/css?kit=abc);ol.lst-kix_abc-0.start{counter-reset:lst-ctn-kix_abc-0 0}.lst-kix_abc-0>li:before{content:"" counter(lst-ctn-kix_abc-0,decimal) ". "}.lst-kix_xyz-0>li:before{content:"\0025cf   "}ul.lst-kix_xyz-0{list-style-type:none}ol.lst-kix_abc-0{list-style-type:none}.lst-kix_abc-0>li{counter-increment:lst-ctn-kix_abc-0}table td,table th{padding:0}.c1{color:#000000;font-weight:700;text-decoration:none;vertical-align:baseline;font-size:11pt;font-family:"Arial";font-style:normal}.c2{padding-top:0pt;padding-bottom:0pt;line-height:1.15;orphans:2;widows:2;text-align:left}.c3{background-color:#ffffff;max-width:451.4pt;padding:72pt 72pt 72pt 72pt}.c4{font-family:"Lato";color:#ff0000}.c5{padding:5pt 5pt 5pt 5pt;border-top-width:1pt;background-color:#cfe2f3}p{margin:0;color:#000000;font-size:11pt;font-family:"Arial"}h1{padding-top:20pt;font-size:20pt;font-family:"Arial"}</style><title>Funkhandbuch</title></head><body class="c3 doc-content"><h1 class="c2" id="h.abc123"><span class="c1">Einleitung</span></h1><p class="c2"><span class="c4">Rot und Lato</span><span class="c1">Fett</span><sup><a href="#cmnt1" id="cmnt_ref1">[a]</a></sup></p><p><a href="https://www.google.com/url?q=https://example.org/page&amp;sa=D&amp;source=editors&amp;ust=1&amp;usg=x">Link</a> <a href="#h.abc123">Sprung</a></p><ol class="c6 lst-kix_abc-0 start" start="1"><li class="c2 li-bullet-0"><span>Eins</span></li><li><span>Zwei</span></li></ol><p>Zwischentext</p><ol class="c6 lst-kix_abc-0"><li><span>Drei</span></li></ol><ul class="lst-kix_xyz-0 start"><li>Punkt</li></ul><table><tr><td class="c5"><p>Zelle</p></td></tr></table><p><span style="overflow: hidden; display: inline-block; margin: 0.00px 0.00px; transform: rotate(0.00rad) translateZ(0px); -webkit-transform: rotate(0.00rad) translateZ(0px); width: 100.00px; height: 50.00px;"><img alt="" src="images/image1.png" style="width: 100.00px; height: 50.00px; margin-left: 0.00px; margin-top: 0.00px;" title=""></span><img src="https://evil.example/x.png"></p><script>alert(1)</script><p onclick="x()" style="position:fixed;top:0;color:#123456">Pinned</p><div><p><a href="#cmnt_ref1" id="cmnt1">[a]</a><span>Kommentartext geheim</span></p></div></body></html>
        """;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static async Task<GoogleDocConversion> ConvertAsync(List<string>? stored = null)
    {
        var (html, images) = GoogleDocConverter.ReadZip(Zip(("Funkhandbuch.html", Encoding.UTF8.GetBytes(Export)),
            ("images/image1.png", Png)));
        return await GoogleDocConverter.ConvertAsync(html, images, image =>
        {
            stored?.Add(image.Path);
            return Task.FromResult<string?>("/dateien/textbilder/11111111-2222-3333-4444-555555555555");
        });
    }

    [Fact]
    public void ReadZip_takes_the_one_document_and_its_pictures_and_ignores_the_rest()
    {
        var (html, images) = GoogleDocConverter.ReadZip(Zip(
            ("doc.html", Encoding.UTF8.GetBytes("<p>x</p>")),
            ("images/image1.png", Png),
            ("../evil.html", Encoding.UTF8.GetBytes("<p>evil</p>")),
            ("images/../../escape.png", Png),
            ("notes.txt", Encoding.UTF8.GetBytes("ignored"))));

        Assert.Equal("<p>x</p>", html);
        Assert.Equal(["images/image1.png"], images.Keys);
        Assert.Equal("image/png", images["images/image1.png"].ContentType);
    }

    [Fact]
    public void ReadZip_refuses_no_document_two_documents_and_non_zip_bytes()
    {
        Assert.Throws<GoogleExportException>(() => GoogleDocConverter.ReadZip(Zip(("images/a.png", Png))));
        Assert.Throws<GoogleExportException>(() => GoogleDocConverter.ReadZip(Zip(
            ("a.html", Encoding.UTF8.GetBytes("<p>a</p>")), ("b.html", Encoding.UTF8.GetBytes("<p>b</p>")))));
        Assert.Throws<GoogleExportException>(() => GoogleDocConverter.ReadZip(Encoding.UTF8.GetBytes("not a zip")));
    }

    [Fact]
    public async Task The_style_block_lands_on_the_elements_and_no_class_survives()
    {
        var result = await ConvertAsync();

        Assert.Equal("Funkhandbuch", result.Title);
        Assert.DoesNotContain("class=", result.OriginalHtml);
        Assert.DoesNotContain("<style", result.OriginalHtml);
        Assert.Matches("Fett", result.OriginalHtml);
        Assert.Matches(@"font-weight:\s*(700|bold)", result.OriginalHtml);
        Assert.Matches(@"color:\s*(#ff0000|rgba?\(255,\s*0,\s*0)", result.OriginalHtml);
        Assert.Matches(@"background-color:\s*(#cfe2f3|rgba?\(207,\s*226,\s*243)", result.OriginalHtml);
        // the body's page box moves onto a wrapper
        Assert.Contains("451.4pt", result.OriginalHtml);
        Assert.Equal(["Lato"], result.FontFamilies);
    }

    [Fact]
    public async Task Scripts_handlers_pinned_positions_remote_pictures_and_comments_are_gone()
    {
        var stored = new List<string>();
        var result = await ConvertAsync(stored);

        Assert.DoesNotContain("<script", result.OriginalHtml);
        Assert.DoesNotContain("onclick", result.OriginalHtml);
        Assert.DoesNotContain("fixed", result.OriginalHtml);
        Assert.Contains("Pinned", result.OriginalHtml);
        Assert.DoesNotContain("evil.example", result.OriginalHtml);
        Assert.DoesNotContain("Kommentartext geheim", result.OriginalHtml);
        Assert.DoesNotContain("[a]", result.OriginalHtml);
        Assert.DoesNotContain("Kommentartext geheim", result.EditableHtml);
        // the picture went through the callback once and both copies point at the stored file
        Assert.Equal(["images/image1.png"], stored);
        Assert.Contains("/dateien/textbilder/11111111-2222-3333-4444-555555555555", result.OriginalHtml);
        Assert.Contains("/dateien/textbilder/11111111-2222-3333-4444-555555555555", result.EditableHtml);
    }

    [Fact]
    public async Task Links_are_unwrapped_and_in_document_jumps_follow_the_prefixed_ids()
    {
        var result = await ConvertAsync();

        Assert.Contains("href=\"https://example.org/page\"", result.OriginalHtml);
        Assert.DoesNotContain("google.com/url", result.OriginalHtml);
        Assert.Contains("id=\"gdoc-h.abc123\"", result.OriginalHtml);
        Assert.Contains("href=\"#gdoc-h.abc123\"", result.OriginalHtml);
    }

    [Fact]
    public async Task List_markers_become_native_and_numbering_continues_after_an_interruption()
    {
        var result = await ConvertAsync();

        Assert.Matches(@"<ol[^>]*list-style-type:\s*decimal", result.OriginalHtml);
        Assert.Matches(@"<ol[^>]*start=""3""", result.OriginalHtml);
        Assert.Matches(@"<ul[^>]*list-style-type:\s*disc", result.OriginalHtml);
        Assert.DoesNotContain("list-style-type: none", result.OriginalHtml);
        Assert.DoesNotContain("list-style-type:none", result.OriginalHtml);
    }

    [Fact]
    public async Task The_editable_copy_speaks_the_editor_formats()
    {
        var result = await ConvertAsync();

        Assert.Contains("<strong>Fett</strong>", result.EditableHtml);
        Assert.DoesNotContain("font-family", result.EditableHtml);
        // black on the dark app would be unreadable; a colour the author chose stays
        Assert.DoesNotMatch(@"color:\s*(#000000|rgba?\(0,\s*0,\s*0)", result.EditableHtml);
        Assert.Matches(@"color:\s*(#ff0000|rgba?\(255,\s*0,\s*0)", result.EditableHtml);
        Assert.DoesNotContain("#gdoc-", result.EditableHtml);
        Assert.Contains("Sprung", result.EditableHtml);
        Assert.DoesNotContain("font-weight", result.EditableHtml);
        Assert.Contains("Zwischentext", result.EditableHtml);
        Assert.Contains("<table", result.EditableHtml);
    }

    [Fact]
    public void Font_families_skip_system_fonts_and_odd_names()
    {
        var html = """<span style="font-family:'Roboto'">a</span><span style="font-family:Arial">b</span><span style="font-family:&quot;x&lt;y&quot;">c</span>""";

        Assert.Equal(["Roboto"], GoogleDocConverter.FontFamilies(html));
    }
}
