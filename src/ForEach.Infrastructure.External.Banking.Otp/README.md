# ForEach.Infrastructure.External.Banking.Otp

Everything specific to **OTP banka Srbija**: statement import today, payment-order export when it
is built.

## Why this is separate from `ForEach.Infrastructure.External.Banking`

The shared project holds what is *standard* — the ISO 20022 `camt.053` reader. That format is not
OTP's; Intesa, Raiffeisen and the rest are moving to the same thing, and copying the reader into
each bank's project would be five maintained copies of one specification. The fifth would drift.

This project holds what only OTP does. Today that is one thing — recognising their export and
handing it to the shared reader — and the value of the seam is what happens when a file turns out
not to be camt: a bank-specific parser goes here, and no host and no other bank changes.

A second bank is a second project beside this one, referencing the same shared reader.

## Not yet verified against a real export

The reader underneath is written against the published standard rather than against a sample from
OTP. The two agree until a bank does something the standard permits and the example did not show.

`BankStatement.Reconciles` is what catches that on the first real file: if the entries do not
account for the movement between the opening and closing balances, something was not understood.
On a bank statement that is worth refusing an import over — a payment silently missing is a
customer chased for money they have already paid.
