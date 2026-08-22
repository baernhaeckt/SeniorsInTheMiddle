/**
 * Swiss-shaped personal data for the traffic the harness generates.
 *
 * Every kind here matches one member of the EntityKind union in
 * frontend/src/protocol/types.ts, so once the proxy detects and the dashboard renders,
 * the categories on screen come from real-looking values rather than lorem ipsum.
 *
 * The values are synthetic. AHV numbers and IBANs carry correct check digits because a
 * detector worth testing will verify them, and a fixture that fails its own checksum
 * would make the harness look like it found nothing.
 */

const FIRST_NAMES = [
  'Ruth', 'Hans', 'Verena', 'Ernst', 'Margrit', 'Walter', 'Heidi', 'Peter',
  'Elsbeth', 'Kurt', 'Rosmarie', 'Fritz', 'Anna', 'Beat', 'Käthi', 'Urs',
]

const LAST_NAMES = [
  'Studer', 'Wyss', 'Zbinden', 'Aebischer', 'Bürki', 'Schmid', 'Hofer', 'Rüegg',
  'Marti', 'Gerber', 'Baumgartner', 'Lüthi', 'Kunz', 'Iseli', 'Moser', 'Frei',
]

const STREETS = [
  'Marktgasse', 'Bahnhofstrasse', 'Kramgasse', 'Länggassstrasse', 'Seftigenstrasse',
  'Optingenstrasse', 'Zähringerstrasse', 'Effingerstrasse',
]

const CITIES = [
  ['3011', 'Bern'], ['3007', 'Bern'], ['3014', 'Bern'], ['3084', 'Wabern'],
  ['3400', 'Burgdorf'], ['2502', 'Biel'], ['3600', 'Thun'], ['8001', 'Zürich'],
]

const CONDITIONS = [
  'Bluthochdruck', 'Diabetes Typ 2', 'Arthrose im rechten Knie', 'Vorhofflimmern',
  'Osteoporose', 'chronische Bronchitis',
]

const INSURERS = ['CSS', 'Helsana', 'Visana', 'Swica', 'Sanitas', 'Concordia', 'KPT']

const BANK_CLEARING = ['00700', '00243', '08387', '06300', '09000']

/** EAN-13 check digit, which is what an AHV number ends with. */
function ean13CheckDigit(digits) {
  let sum = 0
  for (let i = 0; i < digits.length; i++) {
    sum += Number(digits[i]) * (i % 2 === 0 ? 1 : 3)
  }
  return (10 - (sum % 10)) % 10
}

/** IBAN check digits, mod-97-10 over the rearranged string. */
function ibanCheckDigits(countryCode, bban) {
  const rearranged = `${bban}${countryCode}00`
  let remainder = 0
  for (const character of rearranged) {
    const value = /\d/.test(character) ? character : String(character.charCodeAt(0) - 55)
    remainder = Number(`${remainder}${value}`) % 97
  }
  return String(98 - remainder).padStart(2, '0')
}

/** Umlauts the German way, so the generated address is a plausible one. */
const UMLAUTS = { 'ä': 'ae', 'ö': 'oe', 'ü': 'ue', 'Ä': 'Ae', 'Ö': 'Oe', 'Ü': 'Ue', 'ß': 'ss' }
const transliterate = (value) =>
  value.replace(/[äöüÄÖÜß]/g, (character) => UMLAUTS[character]).toLowerCase()

const pick = (rng, list) => list[Math.floor(rng() * list.length)]
const digits = (rng, count) =>
  Array.from({ length: count }, () => Math.floor(rng() * 10)).join('')

/** One synthetic person, with every identifier the dashboard knows how to categorise. */
export function person(rng) {
  const firstName = pick(rng, FIRST_NAMES)
  const lastName = pick(rng, LAST_NAMES)
  const [postalCode, city] = pick(rng, CITIES)

  const ahvBody = `756${digits(rng, 9)}`
  const ahv = `${ahvBody}${ean13CheckDigit(ahvBody)}`.replace(
    /^(\d{3})(\d{4})(\d{4})(\d{2})$/,
    '$1.$2.$3.$4',
  )

  const bban = `${pick(rng, BANK_CLEARING)}${digits(rng, 12)}`
  const iban = `CH${ibanCheckDigits('CH', bban)}${bban}`.replace(
    /^(.{4})(.{4})(.{4})(.{4})(.{4})(.{1})$/,
    '$1 $2 $3 $4 $5 $6',
  )

  const birthYear = 1932 + Math.floor(rng() * 30)
  const birthDate = [
    String(1 + Math.floor(rng() * 28)).padStart(2, '0'),
    String(1 + Math.floor(rng() * 12)).padStart(2, '0'),
    String(birthYear),
  ].join('.')

  return {
    name: `${firstName} ${lastName}`,
    firstName,
    lastName,
    ahv,
    iban,
    address: `${pick(rng, STREETS)} ${1 + Math.floor(rng() * 90)}, ${postalCode} ${city}`,
    phone: `+41 ${pick(rng, ['31', '32', '33', '79', '76'])} ${digits(rng, 3)} ${digits(rng, 2)} ${digits(rng, 2)}`,
    email: transliterate(`${firstName}.${lastName}@example.ch`),
    birthDate,
    condition: pick(rng, CONDITIONS),
    insurer: pick(rng, INSURERS),
    policyNumber: `POL-${digits(rng, 8)}`,
  }
}

/**
 * The values that must come back to the client untouched. If the proxy redacts on the
 * way out and rehydrates on the way back, the client still sees all of these; the
 * receiver, on the other side, should see none of them.
 */
export function secretsOf(subject) {
  return [
    subject.name,
    subject.ahv,
    subject.iban,
    subject.address,
    subject.phone,
    subject.email,
    subject.birthDate,
  ]
}

export { pick }
