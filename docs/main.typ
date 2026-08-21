#let team = "Menu Mingles"
#let title = "TransGourmet: Clever planen, bewusster essen - Menueplanung smart gemacht"

#set document(author: team)
#set text(font: "Arial", size: 10pt, lang: "de", region: "CH")
#set heading(numbering: "1.1")
#set list(marker: ([•], [•]))
#show link: underline
#show link: set text(fill: navy)

#set page(
  paper: "a4",
  header: [
    #block(inset: (left: 0.5cm), spacing: 0pt)[
      #image("assets/logo_header.emf", width: 3.1cm, height: 0.89cm)
      #align(center)[
        #v(-9pt)
        #line(length: 100%, stroke: 2pt)\
      ]
    ]
  ],
  header-ascent: 0pt,
  footer-descent: 10pt,
  footer: [
    #block(inset: (left: 0.5cm))[
      #text(size: 8pt, weight: "extrabold", fill: rgb("#e31b1b"), "BÄRNHÄCKT")
      #text(size: 8pt, weight: "extrabold", datetime.today().display("[year]"))
      #v(-8pt)

      #align(center)[
        #line(length: 100%, stroke: 2pt)
        #v(-10pt)
      ]
      #align(right)[
        #text("Seite", size: 8pt)
        #text(context counter(page).display(), size: 8pt)
        #text("von", size: 8pt)
        #text(context counter(page).final().first(), size: 8pt)
      ]
    ]
  ],
)

#show heading.where(level: 1): it => [
  #set text(size: 9pt, weight: "regular")
  #v(10pt)
  #it
  #v(-0.09in)
  #line(end: (98%, 0%), stroke: (thickness: 0.5pt, paint: rgb(178, 178, 178)))
  #v(5pt)
]
#show heading.where(level: 2): it => [
  #set text(size: 9pt, weight: "regular")
  #v(10pt)
  #it
  #v(-0.09in)
  #line(end: (98%, 0%), stroke: (thickness: 0.5pt, paint: rgb(178, 178, 178)))
  #v(5pt)
]

#let titlepage(title, team) = {
  set page(
    margin: (top: .25in, bottom: 0.3in, left: 1.25in, right: 1.25in),
    header: none,
    footer: none,
    numbering: none,
  )

  place(top + center, image("assets/logo_title.emf", width: 7.99cm, height: 3.18cm))
  place(bottom + center, image("assets/title_footer.emf", width: 20.94cm, height: 5.54cm))

  v(4in)

  align(left)[
    #text(size: 36pt, weight: "bold", team)
    #v(0.3in)
    #text(size: 36pt, weight: "bold", title)
    #v(-0.49in)
    #line(end: (99%, 0%), stroke: (thickness: 2pt))
    #text(size: 14pt, weight: "bold", "Technische Informationen für die Jury")
  ]
}

#let toc() = [
  #text(size: 16pt)[Inhaltsverzeichnis]
  #v(0.2in)
  #outline(depth: 2, title: none)
]

#titlepage(
  title,
  team,
)
#pagebreak()
#toc()
#pagebreak()

#include "chapters/content.typ"
