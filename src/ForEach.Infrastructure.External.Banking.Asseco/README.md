# ForEach.Infrastructure.External.Banking.Asseco

Asseco's iBank / OfficeBanking XML — the e-banking behind Banca Intesa and others in Serbia.

Built against Asseco's published specifications: *Opisi standardnih formata poruka u okviru iBank
sistema*, *OfficeBanking — specifikacija formata* and *FX Client — specifikacija export/import
formata*.

## Statements — `AssecoStatementReader`

| Root | What it is |
|---|---|
| `<stmtrs>` | a statement, answering a request |
| `<pmtnotification>` | the same document pushed as an instant notification |
| `<stmtrslist>` | a wrapper around several of either |

Three things worth knowing:

- **`ledgerbal` is the OPENING balance**, `availbal` the closing one. The specification's own words:
  ledgerbal is the balance *"sa kojim je zaključen prethodni izvod"*. Read the obvious way round,
  every correct file fails reconciliation.
- **The reference that names the invoice swaps with the direction.** `refnumber` is the statement
  owner's poziv na broj and `payeerefnumber` the counterparty's, so on money in it is `refnumber`
  (we are the beneficiary) and on money out it is `payeerefnumber`. This is the **opposite**
  convention to ROL, where "korisnika" means the beneficiary in both directions.
- **`stmtnumber` is empty on the intraday message** — the spec says *"uvek prazno"* — and populated
  on a real izvod.

Balances are read as declared and **not** enforced against the transaction sum: the vendor's own
demo file is internally inconsistent by ninety lipa, so a hard check would refuse the example the
bank ships. `BankStatement.Reconciles` reports it and the host decides.

### Devizni (FX Client) statements are read, and share almost nothing but the root

`rstype` starting `ibank.fps.` marks them, and it is the only thing that tells the two documents
apart *before* reading — which matters, because reading a devizni file with the domestic element
names **succeeds** and quietly yields a statement with no balances, no number and nameless lines.

| | domestic | devizni |
|---|---|---|
| opening balance | `ledgerbal/balamt` | `stmbal/balamt` |
| closing balance | `availbal/balamt` | `closing/balamt` |
| statement number | `stmtnumber` | `stmtno` |
| statement date | `availbal/dtasof` | `dateasto` |
| counterparty | `payeeinfo` on the line | `documentlist`, joined by `documentid` |

Two details worth knowing: the account element is spelled **`accountifo`** in the specification and
the files follow the typo (both spellings are accepted), and transactions are taken from under
`trnlist` only — `documentlist` holds transaction-shaped elements too, and counting both gives every
payment twice.

## Payment orders — `AssecoPaymentOrderWriter`

Writes `<pmtorderrq count="n">` with one `<pmtorder>` per order, UTF-8 without a BOM.

**Accounts are written with their dashes** — `b(3)-p(13)-k(2)` — which is the opposite of what ROL
wants for the same eighteen digits. There is deliberately no shared account formatter: the
disagreement *is* the format.

`bankid` is derived from the account's leading three digits rather than carried, because a bank code
disagreeing with the account beside it is a contradiction the file cannot resolve. `bankname` is
left empty rather than guessed — it is a lookup this library cannot do, and a wrong one gets quoted
back on a confirmation.

## Statements — `AssecoTextStatementReader` (fixed-width TXT)

220 characters per record, the record kind in the **last** character (`1` header, `2` movement).
Direction is stated outright — `D` duguje, `P` potražuje — so unlike the ROL and Halcom fixed-width
formats there is nothing to infer.

**This is the only reader here built from a specification with no sample to check it against.** Its
tables do not survive the PDF's text layer at all — the columns interleave — and were recovered with
PyMuPDF's table extraction, which puts them back in order. Two things make that acceptable:

- Both record layouts account for **exactly 220 characters**, so an offset that drifts leaves a gap.
- **The reader checks itself.** The header states the day's debit *and* credit turnover, and the
  entries must reproduce both — not merely net to the same figure, which two swapped columns would
  also do. If these offsets are wrong against a real file, the totals disagree and the import is
  refused rather than handed a statement whose amounts came from elsewhere.

## Not built

- **Devizni nalog za plaćanje** — documented in the FX Client spec, not written here (see
  `docs/banking-formats.md` for why that document is not sound enough to write from). The devizni
  *izvod* is read.
- **Excel and Multi E-bank variants.** MT940 is read by the shared reader in `…Banking` — the
  OfficeBanking "SWIFT SAP" export is MT940.
