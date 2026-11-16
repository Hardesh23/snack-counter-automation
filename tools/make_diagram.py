"""Generate docs/lifecycle.svg (refill lifecycle diagram). Stdlib only.

    python3 tools/make_diagram.py docs/lifecycle.svg
"""
import sys
from xml.sax.saxutils import escape

W, H = 1250, 590
BW, BH = 150, 76
XS = [30, 290, 550, 810, 1070]        # main row x positions
Y1, Y2 = 150, 340                      # main row / lower row y

C = {  # fill, stroke
    "grey": ("#F1F5F9", "#64748B"),
    "blue": ("#DBEAFE", "#2563EB"),
    "amber": ("#FEF3C7", "#D97706"),
    "teal": ("#CCFBF1", "#0F766E"),
    "green": ("#DCFCE7", "#15803D"),
    "red": ("#FEE2E2", "#B91C1C"),
}
INK, MUTED = "#0F172A", "#475569"
out = []


def box(x, y, title, sub, color, dashed=False):
    fill, stroke = C[color]
    d = ' stroke-dasharray="6 4"' if dashed else ""
    out.append(f'<rect x="{x}" y="{y}" width="{BW}" height="{BH}" rx="12" fill="{fill}" '
               f'stroke="{stroke}" stroke-width="2"{d}/>')
    tl = title.split("\n")
    ty = y + (33 if len(tl) == 1 else 27)
    for k, t in enumerate(tl):
        out.append(f'<text x="{x + BW / 2}" y="{ty + 18 * k}" text-anchor="middle" font-size="16" '
                   f'font-weight="700" fill="{INK}">{escape(t)}</text>')
    out.append(f'<text x="{x + BW / 2}" y="{y + (54 if len(tl) == 1 else 68)}" text-anchor="middle" '
               f'font-size="12" fill="{MUTED}">{escape(sub)}</text>')


def text(x, y, s, anchor="middle", size=12, color=INK, weight="400", italic=False):
    st = ' font-style="italic"' if italic else ""
    out.append(f'<text x="{x}" y="{y}" text-anchor="{anchor}" font-size="{size}" '
               f'font-weight="{weight}" fill="{color}"{st}>{escape(s)}</text>')


def arrow(path, color="#334155", dashed=False, mark="a"):
    d = ' stroke-dasharray="6 4"' if dashed else ""
    out.append(f'<path d="{path}" fill="none" stroke="{color}" stroke-width="2.2"{d} '
               f'marker-end="url(#{mark})"/>')


# ---- title
text(30, 36, "Refill lifecycle: from “box is empty” to “reimbursed”", "start", 22, INK, "700")
text(30, 58, "Happy path runs left to right. Side paths show handoff, budget approval and rejection.",
     "start", 13, MUTED)

# ---- main row
box(XS[0], Y1, "BOX EMPTY", "someone taps “empty”", "grey")
box(XS[1], Y1, "OFFERED", "one volunteer asked", "blue")
box(XS[2], Y1, "COMMITTED", "volunteer accepted", "blue")
box(XS[3], Y1, "CLAIMED", "claim filed in HR", "teal")
box(XS[4], Y1, "REIMBURSED", "volunteer paid back", "green")

cy = Y1 + BH // 2
labels = [("report_empty", "(one open refill", "per office)"), ("Accept", "", ""),
          ("receipt sent,", "budget OK", ""), ("HR approves", "", "")]
for i, (a, b, c) in enumerate(labels):
    x0, x1 = XS[i] + BW, XS[i + 1]
    arrow(f"M{x0 + 4} {cy} L{x1 - 4} {cy}")
    mid = (x0 + x1) / 2
    lines = [s for s in (a, b, c) if s]
    for k, s in enumerate(lines):
        text(mid, cy - 10 - 14 * (len(lines) - 1 - k), s, size=12, weight="700" if k == 0 else "400",
             color=INK if k == 0 else MUTED)

# ---- handoff loop (COMMITTED/OFFERED back to OFFERED)
ox, cx_ = XS[1] + BW / 2, XS[2] + BW / 2
arrow(f"M{cx_} {Y1 - 2} C{cx_} 100 {ox} 100 {ox} {Y1 - 4}", "#2563EB")
text((ox + cx_) / 2, 80, "decline  ·  no reply in 4 h  ·  no purchase in 24 h", size=12, weight="700", color="#1D4ED8")
text((ox + cx_) / 2, 104, "→ next volunteer (earlier ones excluded this round)", size=12, color=MUTED)

# ---- lower row
box(XS[1], Y2, "ADMIN ALERTED", "retried on next tick", "grey", dashed=True)
box(XS[2], Y2, "AWAITING\nAPPROVAL", "bought, over budget", "amber")
box(XS[3], Y2, "REJECTED", "volunteer + admin told", "red")

# OFFERED -> admin alert
ax = XS[1] + BW / 2
arrow(f"M{ax} {Y1 + BH + 2} L{ax} {Y2 - 4}", "#64748B", dashed=True)
text(ax + 8, 285, "no volunteer left", "start", 12, MUTED, italic=True)

# COMMITTED -> awaiting approval
mx = XS[2] + BW / 2
arrow(f"M{mx} {Y1 + BH + 2} L{mx} {Y2 - 4}", "#D97706")
text(mx - 8, 280, "over budget:", "end", 12, INK, "700")
text(mx - 8, 296, "month spend + amount", "end", 12, MUTED)
text(mx - 8, 311, "> monthly budget", "end", 12, MUTED)

# awaiting -> claimed (admin approves): elbow
arrow(f"M{XS[2] + BW - 20} {Y2 - 2} L{XS[2] + BW - 20} 285 L{XS[3] + 30} 285 L{XS[3] + 30} {Y1 + BH + 4}", "#15803D")
text(XS[2] + BW + 6, 277, "admin approves", "start", 12, "#15803D", "700")

# awaiting -> rejected (admin rejects)
ry = Y2 + BH // 2
arrow(f"M{XS[2] + BW + 4} {ry} L{XS[3] - 4} {ry}", "#B91C1C")
text((XS[2] + BW + XS[3]) / 2, ry - 8, "admin rejects", size=12, weight="700", color="#B91C1C")

# claimed -> rejected (HR rejects)
hx = XS[3] + BW - 30
arrow(f"M{hx} {Y1 + BH + 2} L{hx} {Y2 - 4}", "#B91C1C")
text(hx + 10, 280, "HR rejects", "start", 12, "#B91C1C", "700")
text(hx + 10, 296, "(admin alerted)", "start", 12, MUTED)

# claim overdue note
text(XS[4] + BW / 2, Y2 + 28, "Claim pending > 10 days", size=12, weight="700")
text(XS[4] + BW / 2, Y2 + 44, "→ admin chases HR once", size=12, color=MUTED)
out.append(f'<rect x="{XS[4] - 8}" y="{Y2 + 6}" width="{BW + 16}" height="52" rx="10" fill="none" '
           f'stroke="#64748B" stroke-dasharray="6 4"/>')

# ---- rules panel
py = 440
out.append(f'<rect x="30" y="{py}" width="1190" height="98" rx="12" fill="#F8FAFC" stroke="#CBD5E1"/>')
rules = [
    ("Why it can't double-buy", "one open refill per office; only the offered volunteer can accept; atomic transitions"),
    ("Who is asked", "not waiting for money → fewest refills in 90 days → longest since last buy; away/visiting skipped"),
    ("Budget check", "per office and currency; soft limit: over-budget goes to admin, never silently refused"),
]
for i, (h, t) in enumerate(rules):
    x = 50 + i * 395
    text(x, py + 28, h, "start", 13, INK, "700")
    # wrap manually at ~44 chars
    words, line, ln = t.split(), "", []
    for w in words:
        if len(line) + len(w) + 1 > 46:
            ln.append(line); line = w
        else:
            line = (line + " " + w).strip()
    ln.append(line)
    for k, s in enumerate(ln):
        text(x, py + 46 + 15 * k, s, "start", 12, MUTED)

# ---- colour legend
lx = 30
for name, label in [("blue", "waiting on volunteer"), ("amber", "needs admin"), ("teal", "waiting on HR"),
                    ("green", "done"), ("red", "needs follow-up"), ("grey", "trigger / escalation")]:
    fill, stroke = C[name]
    out.append(f'<rect x="{lx}" y="560" width="14" height="14" rx="3" fill="{fill}" stroke="{stroke}" stroke-width="1.5"/>')
    text(lx + 20, 572, label, "start", 12, MUTED)
    lx += 30 + len(label) * 7 + 22

defs = ('<defs><marker id="a" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" '
        'orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="context-stroke"/></marker></defs>')
svg = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H + 10}" viewBox="0 0 {W} {H + 10}" '
       f'font-family="Segoe UI, Calibri, Arial, sans-serif"><rect width="100%" height="100%" fill="#FFFFFF"/>'
       f'{defs}{"".join(out)}</svg>')
with open(sys.argv[1], "w", encoding="utf-8") as f:
    f.write(svg)
