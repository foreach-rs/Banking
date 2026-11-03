# ForEach.Infrastructure.External.Banking.Halcom

Halcom Hal E-Bank statement exports.

Halcom is not a bank — it is the e-banking client roughly **twenty Serbian banks** run (Raiffeisen,
Intesa, Erste, UniCredit, AIK, ProCredit, Komercijalna, Addiko, OTP, Poštanska štedionica and the
rest). One reader covers all of them, which is what makes the fixed-width work worth doing.

## Statements — `HalcomStatementReader`

| Variant | Detected by | Read |
|---|---|---|
| Fixed-width, 280-character lines | posting code at 19–20 and the dots inside the booking date | yes |
| Extended, `#`-delimited | header row beginning `ST_RACUNA#` | yes |
| Devizni, tag-based pseudo-XML | `<CLIENTPROFILE>` | yes — see below |

### The balances are in a second file

The transaction export carries **no balances and no statement number**. Those live in the companion
recap, `*_cov.txt` ("Rekapitulacija izvoda"), which `Read` takes as its optional second argument.

Without it, `BankStatement.Reconciles` is null and the host's *"the entries do not account for the
movement between the balances"* refusal cannot fire. That is a real reduction in safety, not a
cosmetic gap — **ask for both files**.

A recap line **inside** the transaction file is also picked up, which covers somebody concatenating
the two exports or pasting both into one upload. It is unambiguous — 147 characters beginning `01`,
against transactions of 280 — and before this such a file was refused outright as unrecognised,
which is the worst of the three outcomes: the person had both files and was told the format was not
understood. An explicitly passed recap still wins; the sniffing is a fallback, not a preference.

### Three things real files do that the spec does not say

- **Šifra plaćanja spans 106–111 as a 3-digit NBS code.** The spec was written for the pre-2003
  two-digit codebook and describes a leading "oblik plaćanja" digit. Real files leave that blank.
  The reader takes the whole zone and trims.
- **The bank's reference is not unique.** One order can produce several lines sharing a broj za
  reklamaciju. Repeats get a deterministic `#2`, `#3` suffix — a host deduplicating on the raw
  reference would otherwise drop genuine payments, and the suffix is stable so a re-import still
  recognises every line.
- **Recap dates have no dots** (`27122011`), although the spec writes `DD.MM.GGGG` — which does not
  fit the eight characters the field has. The files are right and the document is not.

### Extended variant

Slovenian column names, Serbian display amounts (`3.000.000,00` — read as three million, not
three), one file able to hold several statements, grouped by account + number + currency. It
carries **no balances**, so they are null rather than zero: zero would claim the account was empty.

Required columns are demanded up front (`ST_RACUNA`, `ZNESEK_V_BREME`, `ZNESEK_V_DOBRO` and one of
`DATUM_KNJ`/`DATUM_OBD`) — without them every row reads as a silent zero on a nameless account and
the import reports success over nothing. A repeated column name is refused.

### Devizni: the direction is derived, not decoded

Derived from a real file; no specification was available. The file carries several codes that
*might* be a debit/credit flag — `IDB`, `TI`, `BTO` — and the one sample holds a single transaction,
which is a credit. There is nothing to learn the debit encoding from, and guessing produces a reader
that books every outgoing payment as money in.

So it is not decoded. Each movement appears twice — a `<PPTPROMET>` with the detail and a `<KONTO>`
with the running balance, joined by `P_GI` / `K_GI` — and the KONTO states the balance before
(`AMM`), the amount (`VTO`) and the balance after (`PST`). Which way the money went is arithmetic:
`PST > AMM` is a credit.

**A movement whose direction cannot be derived refuses the whole file**, not the line. Importing
what resolved and reporting success is how a payment goes missing.

Two things are deliberately absent: the **reference** (`OZK` is free text that happens to hold a
SWIFT `/INV/` value in the one sample — handing that to the invoice matcher on one example is the
unsafe direction to be wrong in, so it stays in the description) and the **statement number**, which
the format does not carry.

## Payment orders — `HalcomPaymentOrderWriter`

The virman import, built from a specification **and** a real accepted file together — the standard
this solution holds writers to, and the reason it waited. Asseco's OfficeBanking manual documents
the format under *"Multi E-bank format — Platni nalozi"*; a genuine file exported for Banka
Poštanska štedionica supplied what the manual does not say.

A file is an **address record**, a **summary record** and one record per order.

### The manual is out by 37 characters per order

Its detail record is 180 wide. A real one is 217, and three differences account for every character:

| Finding | Where |
|---|---|
| A **35-character address** between the beneficiary's name and their place | not in the manual at all |
| **Three-digit šifra plaćanja**, absorbing the position the manual calls *oblik plaćanja* | same deviation as the statement side |
| Two single-character **fillers** — before the purpose and before the value date | not in the manual |

The layout is then arithmetic rather than opinion: the fields sum to 217 exactly, and the summary
record's stated total matched the orders beneath it to the para. The writer reproduces the real file
**byte for byte**, and the committed test pins that with the identities replaced at the same lengths.

### Encoding

**Windows-1250, ending with `0x1A`.** Not UTF-8: a name with a `Ć` becomes two bytes and every
fixed-width column after it moves, so the bank reads the amount out of the middle of somebody's
street. A character the code page cannot write — Cyrillic — is **refused by name** rather than
replaced with a question mark.

Everything else refuses rather than repairs, and one file means one payer: the header states the
account every order is drawn on, so a second account in the batch is refused rather than silently
paid from the first.
