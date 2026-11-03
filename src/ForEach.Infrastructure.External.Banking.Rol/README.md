# ForEach.Infrastructure.External.Banking.Rol

ROL — Raiffeisen OnLine — XML. Raiffeisen banka and OTP banka Srbija both export it.

Built against the bank's own published package (`Formati 2026`): the field descriptions
(`DinarskiIzvodOpis`, `DinarskiNalogOpis`) and the example files shipped beside them.

## Statements — `RolStatementReader`

Reads both roots:

| Root | Account |
|---|---|
| `<TransakcioniRacunPrivredaIzvod>` | dinar current account |
| `<RacunPrivredaIzvod>` | foreign currency (`OznakaValute`) |

Everything is an XML **attribute**: `<Zaglavlje>` is the header, each `<Stavke>` is one line.

Three things it gets right that are silent when wrong:

- **The amount is netted, not picked.** `Duguje` and `Potrazuje` are two columns; real files carry
  a *negative* `Potrazuje` for a storno pair, so `Potrazuje − Duguje` is the fact and
  "whichever is non-zero" books a correction as money arriving.
- **The reference is the beneficiary's** (`PozivNaBrojKorisnika`), not the payer's. On money in it
  is our invoice number; on money out it is the supplier's. Both are what the host matches on.
- **Dates are `dd.MM.yyyy`** whatever the description says. `DatumIzvoda` is declared `xs:date`
  and written `26.06.2006`.

A dinar `<Stavke>` carries only `DatumValute`; its booking date is the header's `DatumIzvoda`.

## Payment orders — `RolPaymentOrderWriter`

`Write` produces one `<Nalog>`; `WriteBatch` produces `<Client><PaketZaSlanje>` with one `<Nalog>`
per order and the bank's own `ImeFajla` naming.

**Use `Bytes()`, not `Content`.** The file must be **UTF-16 LE with a BOM**, CRLF, no XML
declaration — that is what the bank's importer reads, and a UTF-8 file looks correct in an editor
and is refused.

Every order is validated before anything is written, and a batch is refused whole: a file holding
the first twenty of thirty orders is worse than no file, because the bank takes it and ten
suppliers are unpaid with nothing saying which. Refusals are Serbian sentences naming the field —
account not eighteen digits, purpose over 105 characters, a poziv na broj carrying a character the
bank forbids, a non-RSD amount (that is the devizni nalog, a different document).

## Devizni payment orders — `RolForeignPaymentOrderWriter`

Same envelope, different document: `SifraProizvodaCORE` 210 (not 501), a beneficiary named by IBAN
and country code, a correspondent bank, a SWIFT charge option (`OUR`/`BEN`/`SHA`), and a
`<Statistika>` block declaring to the National Bank what the money was for.

**Three rules interlock around the cover indicator** (`IndikatorPokrica`), and each is a file the
bank refuses — or takes and funds from somewhere nobody chose:

| | `$` foreign account | `D` bank buys currency | `S` dinars |
|---|---|---|---|
| Currency | not RSD | not RSD | must be RSD (941) |
| `Partija` | required | required | **must be absent** |

The third rule is that the statistics must total the payment. That one is **inferred rather than
quoted** — the specification does not say it in words — and the writer says so, so that if a real
file ever disagrees the reason is findable. It is enforced because the statistics are the
declaration to the NBS, and a set that does not total the payment declares something other than
what was paid, while the payment itself stays correct and nothing downstream notices.

Reason code **112** (goods already imported) additionally demands the invoice it settles.

`CurrencyCode` is the **numeric** ISO 4217 code (`978`, `941`) because that is what the file carries
and what the cover rule is expressed in — taking the alphabetic code would put a currency table
inside a format writer, and an incomplete one refuses payments the bank supports.

### Empty tags are paired

`<Partija></Partija>`, not `<Partija/>`. Identical to an XML parser, and the bank's own examples
distinguish them: fields the caller may leave blank are paired, and only the ones the bank fills
self-close. Matching the example costs nothing and is the difference that matters if their importer
does string matching rather than parsing.

## Statements — `RolTextStatementReader` (fixed-width TXT)

The bank's other statement export, 489 characters per record, **UTF-16 LE with a BOM**. Level `1`
is the header and `2` a movement; the nineteen detail field lengths sum to exactly 489, which is
what makes the layout trustworthy rather than merely plausible. Amounts are `Z20N.2N` — a sign,
twenty digits, a point and two more — and netted across the two columns, the same rule as the XML.

**Detection cannot rely on the level digit.** A Halcom fixed-width line begins with the
counterparty's account number, which routinely starts with a 1 or a 2, so recognising on that alone
made this reader claim Halcom files and answer with a statement whose account was a date. The shape
is checked where the format fixes punctuation instead: the slashes in the dates and the decimal
points in both amount columns. Nothing else here puts those characters at those offsets, and a
dispatcher test pins both directions.

MT940 is read too, by the shared reader in `…Banking` — it is not a ROL format.

## Not built

- Nothing on the statement side. XML, fixed-width TXT and MT940 are all read.
