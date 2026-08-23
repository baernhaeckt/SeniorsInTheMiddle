#import "@preview/polylux:0.4.0": *

#let u = 841.89pt / 1280

// Colour carries meaning here, not decoration:
//   warm  = data that identifies a person (the trusted side)
//   cool  = data that does not (the open side)
//   alert = an identifier caught mid-flight
#let void = rgb("#0d1216")
#let panel = rgb("#0c2a2b")
#let line-soft = rgb("#183f3c")
#let line-mid = rgb("#225851")
#let ink = rgb("#ffffff")
#let ink-2 = rgb("#a5afb4")
#let ink-3 = rgb("#7d8689")
#let warm = rgb("#3fb39c")
#let warm-2 = rgb("#288879")
#let cool = rgb("#38b5ee")
#let alert = rgb("#d4708d")

// Segoe UI is what the HTML falls back to on the presenting machine; the Linux
// names keep a CI build from silently landing on something else.
#let display-font = ("Archivo", "Segoe UI", "Liberation Sans", "DejaVu Sans")
#let body-font = ("IBM Plex Sans", "Segoe UI", "Liberation Sans", "DejaVu Sans")

#set page(
  paper: "presentation-16-9",
  fill: void,
  margin: (top: 60 * u, bottom: 52 * u, x: 80 * u),
  background: {
    // The room the deck sits in: warm behind the trusted side, cold behind the open one.
    place(top + left, rect(
      width: 100%, height: 100%,
      fill: gradient.radial(rgb("#28887924"), rgb("#28887900"), center: (6%, 34%), radius: 62%),
    ))
    place(top + left, rect(
      width: 100%, height: 100%,
      fill: gradient.radial(rgb("#38b5ee18"), rgb("#38b5ee00"), center: (96%, 66%), radius: 62%),
    ))
  },
)

#set text(font: body-font, fill: ink, size: 20 * u, lang: "de", region: "CH")
#set par(leading: 0.62em, spacing: 0pt)

#let label-text(it) = text(
  font: display-font, size: 13 * u, weight: 600, tracking: 0.22em, fill: ink-3,
)[#upper(it)]

#let title-text(it) = block(above: 24 * u)[
  #set par(leading: 0.06em)
  #text(font: display-font, size: 108 * u, weight: 700, tracking: -0.02em)[#it]
]

#let heading-text(it) = block(above: 14 * u)[
  #set par(leading: 0.32em)
  #text(font: display-font, size: 48 * u, weight: 700, tracking: -0.01em)[#it]
]

#let lede(it) = block(width: 26em, above: 34 * u)[
  #set text(size: 25 * u, fill: ink-2)
  #set par(leading: 0.7em)
  #it
]

// Footer: Teaser and with the position marker on the right.
#let footer(active) = place(bottom + left, block(width: 100%)[
  #set text(font: display-font, size: 12 * u, tracking: 0.18em, fill: ink-3)
  #grid(columns: (1fr, auto), align: (left + bottom, right + bottom),
    upper[Echte Werte verlassen das Land nie],
    stack(dir: ltr, spacing: 7 * u, ..range(3).map(i =>
      rect(width: 22 * u, height: 2 * u, fill: if i == active { warm } else { line-mid })
    )),
  )
])

// ---- slide 1: title and promise ----
#let stream-rows = (
  ((48, 70, 36, 92, 58, 42, 30), 3),
  ((84, 40, 52, 64, 34, 72, 24), 3),
  ((30, 98, 46, 66, 40, 54, 34), 1),
  ((56, 42, 76, 34, 88, 46, 28), 4),
  ((66, 48, 30, 58, 40, 100, 36), 5),
)

#let stream-block(x0, mark-fill) = {
  for (r, row) in stream-rows.enumerate() {
    let (widths, mark) = row
    let x = x0
    for (i, w) in widths.enumerate() {
      // The outermost words fade, so the stream reads as passing through rather than starting here.
      let edge = calc.min(1.0, calc.min(x - x0, x0 + 450 - x - w) / 90.0 + 0.25)
      let fill = if i == mark { mark-fill } else { ink-2 }
      place(top + left, dx: x * u, dy: (10 + r * 32) * u,
        rect(width: w * u, height: 13 * u, radius: 6.5 * u,
          fill: fill.transparentize(if i == mark { 100% - edge * 100% } else { 100% - edge * 20% })))
      x += w + 12
    }
  }
}

#let stream = block(width: 1120 * u, height: 176 * u, above: 8 * u)[
  #stream-block(30, warm)
  #stream-block(640, cool)
  #place(top + left, dx: 560 * u, rect(width: 1.5 * u, height: 176 * u,
    fill: gradient.linear(warm.transparentize(100%), warm, cool.transparentize(100%), angle: 90deg)))
  #place(top + left, dx: 555.5 * u, dy: 83.5 * u,
    circle(radius: 5 * u, fill: void, stroke: 1.5 * u + warm))
]

#slide[
  #label-text[BärnHäckt 2026 · Swiss Data Airlock]
  #title-text[SITM Proxy#text(fill: warm)[.]]
  #lede[
    Der #text(fill: warm, weight: 500)[Datentresor] auf Ihrer Seite der Grenze.
    Er ersetzt jede Personenangabe, bevor sie hinausgeht.
  ]
  #place(bottom + left, dy: -46 * u, stream)
  #footer(0)
]

// ---- slide 2: four capabilities ----
#let capability(accent, title, body) = block(
  inset: (left: 20 * u), stroke: (left: 2 * u + accent),
)[
  #text(font: display-font, size: 27 * u, weight: 700, tracking: -0.005em)[#title]
  #block(above: 12 * u)[
    #set text(size: 21 * u, fill: ink-2)
    #set par(leading: 0.62em)
    #body
  ]
]

#slide[
  #label-text[Was es tut]
  #heading-text[Hinaus geht nur,\ was niemanden identifiziert.]
  #block(above: 56 * u)[
    #grid(
      columns: (1fr, 1fr), column-gutter: 34 * u, row-gutter: 44 * u,
      capability(warm, [Pseudonymisierung per NLP],
        [Findet Namen, Adressen und IBANs im Fliesstext und ersetzt sie formattreu.]),
      capability(alert, [Risikobewertung],
        [Jeder Fund wird eingestuft, wie stark er eine Person verrät.]),
      capability(warm-2, [Lokaler Tresor],
        [Die echten Werte bleiben im Haus – für den Benutzer ändert sich nichts.]),
      capability(cool, [Live Insights],
        [Jede Anfrage, jeder Fund, jede Ersetzung live sichtbar.]),
    )
  ]
  #footer(1)
]

// ---- slide 3: one package, three places ----
#let place-card(variant, title, body) = block(
  width: 100%, fill: panel.transparentize(55%), radius: 4 * u,
  stroke: 1 * u + line-soft, inset: (x: 24 * u, top: 26 * u, bottom: 28 * u),
)[
  #label-text[#variant]
  #block(above: 16 * u)[
    #text(font: display-font, size: 25 * u, weight: 700)[#title]
  ]
  #block(above: 10 * u)[
    #set text(size: 18 * u, fill: ink-2)
    #set par(leading: 0.62em)
    #body
  ]
]

#slide[
  #label-text[Wo es läuft]
  #heading-text[Wo es läuft,\ bestimmen Sie!]
  #block(above: 34 * u)[
    #grid(
      columns: (1fr, 1fr, 1fr), column-gutter: 22 * u,
      place-card[Variante 1][Beim Schweizer Hoster][Der Tresor bleibt auf einem Server in der Schweiz.],
      place-card[Variante 2][Im eigenen Haus][Auf eigener Hardware, hinter der eigenen Firewall.],
      place-card[Variante 3][Auf dem Notebook][Dasselbe Paket, alles darin, nichts daneben.],
    )
  ]
  #block(above: 44 * u)[
    #text(font: display-font, size: 28 * u, weight: 700, tracking: -0.005em)[
      Keine externen Komponenten. Keine fremde Abhängigkeit.
    ]
    #block(above: 10 * u)[
      #text(size: 20 * u, fill: ink-2)[
        Die Grenze, die Sie Ihren Kunden zugesagt haben, ist jetzt Software, die Ihnen gehört.
      ]
    ]
  ]
  #footer(2)
]
