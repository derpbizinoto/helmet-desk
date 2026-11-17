# Minimal .docx writer (stdlib only): paragraphs, runs, tables, bullets, images, header/footer.
import zipfile, zlib, struct, math, os
from xml.sax.saxutils import escape

W_NS = ('xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" '
        'xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" '
        'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" '
        'xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"')

FONT = "Arial"
MONO = "Consolas"
INK = "292333"      # body text on the white page
DARK = "17111F"     # dark band fill
DARK2 = "1D1528"
DARK3 = "251A34"
LIGHT = "F5F1FA"


# ---------- colors ----------
def parse(c):
    """'#RRGGBB' or '#AARRGGBB' (WPF order) -> (a, r, g, b) 0..255"""
    c = c.lstrip('#')
    if len(c) == 6:
        return 255, int(c[0:2], 16), int(c[2:4], 16), int(c[4:6], 16)
    return int(c[0:2], 16), int(c[2:4], 16), int(c[4:6], 16), int(c[6:8], 16)


def over(c, base):
    a, r, g, b = parse(c)
    _, R, G, B = parse(base)
    k = a / 255
    return '%02X%02X%02X' % (round(r * k + R * (1 - k)), round(g * k + G * (1 - k)), round(b * k + B * (1 - k)))


def css(c):
    """WPF #AARRGGBB -> CSS #RRGGBBAA (or #RRGGBB when opaque)"""
    a, r, g, b = parse(c)
    s = '#%02X%02X%02X' % (r, g, b)
    return s if a == 255 else s + '%02X' % a


def pct(c):
    return round(parse(c)[0] / 255 * 100)


def lum(h):
    _, r, g, b = parse(h)
    def ch(v):
        v /= 255
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    return 0.2126 * ch(r) + 0.7152 * ch(g) + 0.0722 * ch(b)


def contrast(a, b):
    la, lb = sorted([lum(a), lum(b)], reverse=True)
    return (la + 0.05) / (lb + 0.05)


# ---------- PNG gradients ----------
def _png(w, h, rows):
    raw = b''.join(b'\x00' + bytes(r) for r in rows)
    def chunk(t, d):
        c = struct.pack('>I', len(d)) + t + d
        return c + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


def _sample(stops, t):
    t = max(0.0, min(1.0, t))
    if t <= stops[0][1]:
        return parse(stops[0][0])
    for (c0, o0), (c1, o1) in zip(stops, stops[1:]):
        if t <= o1:
            k = 0 if o1 == o0 else (t - o0) / (o1 - o0)
            p0, p1 = parse(c0), parse(c1)
            return tuple(p0[i] + (p1[i] - p0[i]) * k for i in range(4))
    return parse(stops[-1][0])


def gradient_png(path, layers, base, w=360, h=90, radius=0):
    """layers: list of ('linear', stops, (sx,sy,ex,ey)) or ('radial', stops, (cx,cy,rx,ry)), painted over base."""
    _, R, G, B = parse(base)
    rows = []
    for y in range(h):
        row = []
        for x in range(w):
            u, v = (x + 0.5) / w, (y + 0.5) / h
            r, g, b = R, G, B
            for kind, stops, geo in layers:
                if kind == 'linear':
                    sx, sy, ex, ey = geo
                    dx, dy = ex - sx, ey - sy
                    t = ((u - sx) * dx + (v - sy) * dy) / (dx * dx + dy * dy)
                else:
                    cx, cy, rx, ry = geo
                    t = math.hypot((u - cx) / rx, (v - cy) / ry)
                a, cr, cg, cb = _sample(stops, t)
                k = a / 255
                r, g, b = cr * k + r * (1 - k), cg * k + g * (1 - k), cb * k + b * (1 - k)
            row += [int(round(r)), int(round(g)), int(round(b))]
        rows.append(row)
    with open(path, 'wb') as f:
        f.write(_png(w, h, rows))
    return path


# ---------- document builder ----------
class Doc:
    def __init__(self, header_left, footer_left):
        self.body = []
        self.images = []   # (rid, path)
        self.header_left = header_left
        self.footer_left = footer_left
        self._pic = 0

    # runs
    @staticmethod
    def run(text, bold=False, italic=False, color=INK, size=20, font=FONT, caps=False, spacing=None):
        rpr = f'<w:rFonts w:ascii="{font}" w:hAnsi="{font}" w:cs="{font}" w:eastAsia="{font}"/>'
        if bold: rpr += '<w:b/><w:bCs/>'
        if italic: rpr += '<w:i/><w:iCs/>'
        if caps: rpr += '<w:caps/>'
        rpr += f'<w:color w:val="{color}"/>'
        if spacing: rpr += f'<w:spacing w:val="{spacing}"/>'
        rpr += f'<w:sz w:val="{size}"/><w:szCs w:val="{size}"/>'
        return f'<w:r><w:rPr>{rpr}</w:rPr><w:t xml:space="preserve">{escape(text)}</w:t></w:r>'

    @staticmethod
    def para(runs, align=None, before=0, after=120, style=None, keep=False, shade=None, num=False, line=None, ind=None, border_bottom=None):
        if isinstance(runs, str) and not runs.startswith('<w:'):
            runs = Doc.run(runs)
        ppr = ''
        if style: ppr += f'<w:pStyle w:val="{style}"/>'
        if keep: ppr += '<w:keepNext/>'
        if num: ppr += '<w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr>'
        if border_bottom: ppr += f'<w:pBdr><w:bottom w:val="single" w:sz="6" w:space="4" w:color="{border_bottom}"/></w:pBdr>'
        if shade: ppr += f'<w:shd w:val="clear" w:color="auto" w:fill="{shade}"/>'
        ppr += f'<w:spacing w:before="{before}" w:after="{after}"' + (f' w:line="{line}" w:lineRule="auto"' if line else '') + '/>'
        if ind: ppr += f'<w:ind w:left="{ind}"/>'
        if align: ppr += f'<w:jc w:val="{align}"/>'
        return f'<w:p><w:pPr>{ppr}</w:pPr>{runs}</w:p>'

    def add(self, xml):
        self.body.append(xml)

    def h1(self, text):
        self.add(self.para(self.run(text, bold=True, color="8950D9", size=32), before=360, after=140, style="Heading1", keep=True,
                           border_bottom="D9C8F5"))

    def h2(self, text):
        self.add(self.para(self.run(text, bold=True, color=INK, size=24), before=220, after=100, style="Heading2", keep=True))

    def p(self, text, **kw):
        self.add(self.para(self.rich(text), **kw))

    def bullet(self, text):
        self.add(self.para(self.rich(text), num=True, after=60))

    @staticmethod
    def rich(text, color=INK, size=20):
        """**bold** segments"""
        out, bold = '', False
        for i, part in enumerate(text.split('**')):
            if part:
                out += Doc.run(part, bold=bold, color=color, size=size)
            bold = not bold
        return out

    # images
    def image_xml(self, path, w_in, h_in):
        self._pic += 1
        rid = f'rIdImg{self._pic}'
        self.images.append((rid, path))
        cx, cy = int(w_in * 914400), int(h_in * 914400)
        n = self._pic
        return (f'<w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="{cx}" cy="{cy}"/>'
                f'<wp:docPr id="{n}" name="img{n}"/><wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>'
                f'<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>'
                f'<pic:nvPicPr><pic:cNvPr id="{n}" name="img{n}.png"/><pic:cNvPicPr/></pic:nvPicPr>'
                f'<pic:blipFill><a:blip r:embed="{rid}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>'
                f'<pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>'
                f'</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>')

    def image(self, path, w_in, h_in, caption=None):
        self.add(self.para(self.image_xml(path, w_in, h_in), align="center", before=120, after=60))
        if caption:
            self.add(self.para(self.run(caption, italic=True, color="655A70", size=17), align="center", after=200))

    # tables
    @staticmethod
    def cell(content, width, fill=None, valign="center", margins=(100, 140), span=1):
        if isinstance(content, str) and not content.startswith('<w:'):
            content = Doc.para(Doc.run(content), after=0)
        tcpr = f'<w:tcW w:w="{width}" w:type="dxa"/>'
        if span > 1: tcpr += f'<w:gridSpan w:val="{span}"/>'
        if fill: tcpr += f'<w:shd w:val="clear" w:color="auto" w:fill="{fill}"/>'
        tcpr += (f'<w:tcMar><w:top w:w="{margins[0]}" w:type="dxa"/><w:left w:w="{margins[1]}" w:type="dxa"/>'
                 f'<w:bottom w:w="{margins[0]}" w:type="dxa"/><w:right w:w="{margins[1]}" w:type="dxa"/></w:tcMar>')
        tcpr += f'<w:vAlign w:val="{valign}"/>'
        return f'<w:tc><w:tcPr>{tcpr}</w:tcPr>{content}</w:tc>'

    def table(self, widths, rows, border="D9C8F5", header=False):
        total = sum(widths)
        grid = ''.join(f'<w:gridCol w:w="{w}"/>' for w in widths)
        b = f'w:val="single" w:sz="4" w:space="0" w:color="{border}"' if border else 'w:val="nil"'
        borders = ''.join(f'<w:{s} {b}/>' for s in ('top', 'left', 'bottom', 'right', 'insideH', 'insideV'))
        trs = ''
        for i, row in enumerate(rows):
            trpr = '<w:trPr><w:cantSplit/>' + ('<w:tblHeader/>' if header and i == 0 else '') + '</w:trPr>'
            trs += f'<w:tr>{trpr}{"".join(row)}</w:tr>'
        self.add(f'<w:tbl><w:tblPr><w:tblW w:w="{total}" w:type="dxa"/>'
                 f'<w:tblBorders>{borders}</w:tblBorders><w:tblLayout w:type="fixed"/></w:tblPr><w:tblGrid>{grid}</w:tblGrid>{trs}</w:tbl>')
        self.add(self.para('', after=80))

    def data_table(self, widths, head, rows):
        """head: list of str; rows: list of list of (str | xml-cell)"""
        hrow = [self.cell(self.para(self.run(h, bold=True, color=LIGHT, size=18), after=0), w, fill=DARK) for h, w in zip(head, widths)]
        out = [hrow]
        for r in rows:
            cells = []
            for c, w in zip(r, widths):
                if isinstance(c, str) and c.startswith('<w:tc>'):
                    cells.append(c)
                else:
                    cells.append(self.cell(self.para(self.rich(c, size=18), after=0), w))
            out.append(cells)
        self.table(widths, out, header=True)

    def swatch_cell(self, hex_rgb, width, label=''):
        light = lum('#' + hex_rgb) < 0.25
        return self.cell(self.para(self.run(label, color=LIGHT if light else INK, size=16), after=0, align="center"), width, fill=hex_rgb)

    def code(self, text):
        paras = ''.join(self.para(self.run(line if line else ' ', color="E9DDF7", size=16, font=MONO), after=0, line=250)
                        for line in text.split('\n'))
        self.table([9808], [[self.cell(paras, 9808, fill=DARK, margins=(200, 240))]], border=None)

    # ---------- save ----------
    def save(self, path):
        sect = ('<w:sectPr><w:headerReference w:type="default" r:id="rIdH"/><w:footerReference w:type="default" r:id="rIdF"/>'
                '<w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="879" w:right="1049" w:bottom="879" w:left="1049" w:header="397" w:footer="397" w:gutter="0"/></w:sectPr>')
        doc = f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document {W_NS}><w:body>{"".join(self.body)}{sect}</w:body></w:document>'
        hf_tabs = '<w:tabs><w:tab w:val="right" w:pos="9808"/></w:tabs>'
        header = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:hdr {W_NS}><w:p><w:pPr>{hf_tabs}<w:spacing w:after="0"/></w:pPr>'
                  + self.run(self.header_left, bold=True, color="8950D9", size=15, spacing=20)
                  + '<w:r><w:tab/></w:r>' + self.run('Helmet Desk', color="9A8FA8", size=15) + '</w:p></w:hdr>')
        footer = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:ftr {W_NS}><w:p><w:pPr>{hf_tabs}<w:spacing w:after="0"/></w:pPr>'
                  + self.run(self.footer_left, color="9A8FA8", size=15)
                  + '<w:r><w:tab/></w:r>'
                  + '<w:r><w:rPr><w:color w:val="9A8FA8"/><w:sz w:val="15"/></w:rPr><w:fldChar w:fldCharType="begin"/></w:r>'
                  + '<w:r><w:rPr><w:color w:val="9A8FA8"/><w:sz w:val="15"/></w:rPr><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r>'
                  + '<w:r><w:rPr><w:color w:val="9A8FA8"/><w:sz w:val="15"/></w:rPr><w:fldChar w:fldCharType="separate"/></w:r>'
                  + '<w:r><w:rPr><w:color w:val="9A8FA8"/><w:sz w:val="15"/></w:rPr><w:t>1</w:t></w:r>'
                  + '<w:r><w:rPr><w:color w:val="9A8FA8"/><w:sz w:val="15"/></w:rPr><w:fldChar w:fldCharType="end"/></w:r></w:p></w:ftr>')
        styles = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles {W_NS}>'
                  f'<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="{FONT}" w:hAnsi="{FONT}" w:cs="{FONT}" w:eastAsia="{FONT}"/>'
                  f'<w:color w:val="{INK}"/><w:sz w:val="20"/><w:szCs w:val="20"/><w:lang w:val="pt-BR"/></w:rPr></w:rPrDefault>'
                  '<w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>'
                  '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>'
                  f'<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>'
                  f'<w:pPr><w:keepNext/><w:spacing w:before="360" w:after="140"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:rFonts w:ascii="{FONT}" w:hAnsi="{FONT}"/><w:b/><w:color w:val="8950D9"/><w:sz w:val="32"/></w:rPr></w:style>'
                  f'<w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>'
                  f'<w:pPr><w:keepNext/><w:spacing w:before="220" w:after="100"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:rFonts w:ascii="{FONT}" w:hAnsi="{FONT}"/><w:b/><w:color w:val="{INK}"/><w:sz w:val="24"/></w:rPr></w:style>'
                  '<w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:tblPr><w:tblInd w:w="0" w:type="dxa"/>'
                  '<w:tblCellMar><w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>'
                  '</w:styles>')
        numbering = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:numbering {W_NS}>'
                     '<w:abstractNum w:abstractNumId="0"><w:multiLevelType w:val="singleLevel"/><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/>'
                     '<w:lvlText w:val="•"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="360" w:hanging="260"/></w:pPr>'
                     '<w:rPr><w:rFonts w:ascii="Arial" w:hAnsi="Arial"/><w:color w:val="8950D9"/></w:rPr></w:lvl></w:abstractNum>'
                     '<w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>')
        rels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                '<Relationship Id="rIdS" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>'
                '<Relationship Id="rIdN" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/>'
                '<Relationship Id="rIdSet" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings" Target="settings.xml"/>'
                '<Relationship Id="rIdH" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/>'
                '<Relationship Id="rIdF" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer1.xml"/>'
                + ''.join(f'<Relationship Id="{rid}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/{os.path.basename(p)}"/>'
                          for rid, p in self.images)
                + '</Relationships>')
        settings = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:settings {W_NS}><w:defaultTabStop w:val="708"/>'
                    '<w:compat><w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="15"/></w:compat></w:settings>')
        ctypes = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                  '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                  '<Default Extension="xml" ContentType="application/xml"/><Default Extension="png" ContentType="image/png"/>'
                  '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
                  '<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>'
                  '<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>'
                  '<Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>'
                  '<Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>'
                  '<Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/>'
                  '<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>'
                  '</Types>')
        root_rels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                     '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>'
                     '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>'
                     '</Relationships>')
        core = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" '
                'xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">'
                f'<dc:title>{escape(self.footer_left)}</dc:title><dc:creator>Helmet Desk</dc:creator><dc:language>pt-BR</dc:language></cp:coreProperties>')
        with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
            z.writestr('[Content_Types].xml', ctypes)
            z.writestr('_rels/.rels', root_rels)
            z.writestr('docProps/core.xml', core)
            z.writestr('word/document.xml', doc)
            z.writestr('word/styles.xml', styles)
            z.writestr('word/numbering.xml', numbering)
            z.writestr('word/settings.xml', settings)
            z.writestr('word/header1.xml', header)
            z.writestr('word/footer1.xml', footer)
            z.writestr('word/_rels/document.xml.rels', rels)
            for _, p in self.images:
                z.write(p, 'word/media/' + os.path.basename(p))
