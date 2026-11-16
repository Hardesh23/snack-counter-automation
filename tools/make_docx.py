"""Convert DESIGN.md to DESIGN.docx using only the standard library.

    python3 tools/make_docx.py DESIGN.md DESIGN.docx

Supports: #/## headings, paragraphs, bullets, numbered items, tables,
fenced code blocks, **bold** and `inline code`.
"""
import os
import re
import struct
import sys
import zipfile
from xml.sax.saxutils import escape

NS = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'


def runs(text, mono=False, base_bold=False):
    out = []
    for part in re.split(r"(\*\*.+?\*\*|`.+?`)", text):
        if not part:
            continue
        bold, code = base_bold, mono
        if part.startswith("**") and part.endswith("**"):
            part, bold = part[2:-2], True
        elif part.startswith("`") and part.endswith("`"):
            part, code = part[1:-1], True
        rpr = ""
        if bold:
            rpr += "<w:b/>"
        if code:
            rpr += '<w:rFonts w:ascii="Consolas" w:hAnsi="Consolas"/><w:sz w:val="19"/>'
        out.append(f'<w:r><w:rPr>{rpr}</w:rPr><w:t xml:space="preserve">{escape(part)}</w:t></w:r>')
    return "".join(out)


def para(text, style=None, indent=None, mono=False, bold=False):
    ppr = ""
    if style:
        ppr += f'<w:pStyle w:val="{style}"/>'
    if indent:
        ppr += f'<w:ind w:left="{indent[0]}" w:hanging="{indent[1]}"/>'
    return f"<w:p><w:pPr>{ppr}</w:pPr>{runs(text, mono, bold)}</w:p>"


def table(rows):
    border = "".join(f'<w:{s} w:val="single" w:sz="4" w:color="999999"/>'
                     for s in ("top", "left", "bottom", "right", "insideH", "insideV"))
    xml = f'<w:tbl><w:tblPr><w:tblW w:w="5000" w:type="pct"/><w:tblBorders>{border}</w:tblBorders></w:tblPr>'
    for i, row in enumerate(rows):
        xml += "<w:tr>"
        for cell in row:
            shade = '<w:shd w:val="clear" w:fill="E7E6E6"/>' if i == 0 else ""
            xml += (f"<w:tc><w:tcPr>{shade}</w:tcPr>"
                    f'{para(cell, "Cell", bold=(i == 0))}</w:tc>')
        xml += "</w:tr>"
    return xml + "</w:tbl>" + para("")


IMAGES = []  # (zip name, bytes)


def image(path, alt, max_in=6.8):
    data = open(path, "rb").read()
    w, h = struct.unpack(">II", data[16:24])          # PNG header: width, height
    cx = int(max_in * 914400)
    cy = int(cx * h / w)
    IMAGES.append((f"word/media/image{len(IMAGES) + 1}.png", data))
    n = len(IMAGES)
    return (f'<w:p><w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" '
            f'distT="0" distB="0" distL="0" distR="0"><wp:extent cx="{cx}" cy="{cy}"/>'
            f'<wp:docPr id="{n}" name="Picture {n}" descr="{escape(alt)}"/>'
            f'<a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">'
            f'<a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">'
            f'<pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">'
            f'<pic:nvPicPr><pic:cNvPr id="{n}" name="image{n}.png"/><pic:cNvPicPr/></pic:nvPicPr>'
            f'<pic:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" '
            f'r:embed="rIdImg{n}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>'
            f'<pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm>'
            f'<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>'
            f'</a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>')


def convert(md, base="."):
    body, lines, i = [], md.splitlines(), 0
    while i < len(lines):
        ln = lines[i]
        mi = re.fullmatch(r"!\[(.*?)\]\((.+?)\)", ln.strip())
        if mi:
            body.append(image(os.path.join(base, mi.group(2)), mi.group(1)))
        elif ln.startswith("```"):
            i += 1
            while i < len(lines) and not lines[i].startswith("```"):
                body.append(para(lines[i] or " ", "Code", mono=True))
                i += 1
        elif ln.startswith("|"):
            rows = []
            while i < len(lines) and lines[i].startswith("|"):
                cells = [c.strip() for c in lines[i].strip().strip("|").split("|")]
                if not all(re.fullmatch(r"-+", c) for c in cells):
                    rows.append(cells)
                i += 1
            body.append(table(rows))
            continue
        elif ln.startswith("# "):
            body.append(para(ln[2:], "Title"))
        elif ln.startswith("### "):
            body.append(para(ln[4:], "Heading2"))
        elif ln.startswith("## "):
            body.append(para(ln[3:], "Heading1"))
        elif ln.startswith("- "):
            body.append(para("•\t" + ln[2:], "Body", indent=(360, 360)))
        elif re.match(r"\d+\. ", ln):
            n, rest = ln.split(". ", 1)
            body.append(para(f"{n}.\t{rest}", "Body", indent=(360, 360)))
        elif ln.strip():
            body.append(para(ln, "Body"))
        i += 1
    return "".join(body)


STYLES = f"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles {NS}>
<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/><w:sz w:val="21"/></w:rPr></w:rPrDefault></w:docDefaults>
<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
<w:style w:type="paragraph" w:styleId="Body"><w:name w:val="Body"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="80"/></w:pPr></w:style>
<w:style w:type="paragraph" w:styleId="Cell"><w:name w:val="Cell"/><w:basedOn w:val="Normal"/><w:rPr><w:sz w:val="19"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="160"/></w:pPr><w:rPr><w:b/><w:sz w:val="36"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="80"/></w:pPr><w:rPr><w:b/><w:color w:val="1F3864"/><w:sz w:val="26"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="140" w:after="60"/></w:pPr><w:rPr><w:b/><w:color w:val="2F5496"/><w:sz w:val="23"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Code"><w:name w:val="Code"/><w:basedOn w:val="Normal"/><w:pPr><w:shd w:val="clear" w:fill="F2F2F2"/><w:spacing w:after="0"/></w:pPr></w:style>
</w:styles>"""

CONTENT_TYPES = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Default Extension="png" ContentType="image/png"/>
<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
</Types>"""

RELS = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>"""

DOC_RELS = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>"""


def main(src, dst):
    with open(src, encoding="utf-8") as f:
        body = convert(f.read(), os.path.dirname(os.path.abspath(src)))
    sect = ('<w:sectPr><w:pgSz w:w="11906" w:h="16838"/>'
            '<w:pgMar w:top="1000" w:right="1000" w:bottom="1000" w:left="1000" '
            'w:header="500" w:footer="500" w:gutter="0"/></w:sectPr>')
    doc = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
           f"<w:document {NS}><w:body>{body}{sect}</w:body></w:document>")
    with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", CONTENT_TYPES)
        z.writestr("_rels/.rels", RELS)
        z.writestr("word/document.xml", doc)
        z.writestr("word/styles.xml", STYLES)
        rels = DOC_RELS.replace("</Relationships>", "".join(
            f'<Relationship Id="rIdImg{k}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" '
            f'Target="media/image{k}.png"/>' for k in range(1, len(IMAGES) + 1)) + "</Relationships>")
        z.writestr("word/_rels/document.xml.rels", rels)
        for name, data in IMAGES:
            z.writestr(name, data)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
