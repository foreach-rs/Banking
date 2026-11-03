# ForEach.Infrastructure.External.Banking

Bank statement readers, with no knowledge of any host application.

## What it does

`Camt053Reader.Read(xml)` turns an ISO 20022 **camt.053** account statement into a
`BankStatement`: the account, the currency, the statement number, the opening and closing
balances, and one `StatementEntry` per line.

## What it deliberately does not do

It does not decide what a payment *means*. No contact matching, no invoice allocation, no
currency conversion, no posting. Those depend on the host's own data, and a library that guessed
them would be a library that has to know about customers.

## Three decisions worth knowing

**Version-agnostic.** Every lookup is by local element name; the namespace — which is the camt
version — is ignored. A reader pinned to `camt.053.001.02` answers "empty statement" for a
perfectly valid `.001.08` file, and an empty statement looks exactly like a quiet day at the bank.

**It never throws.** A renamed PDF, a truncated download, an export from something that is not a
bank: each returns `BankStatement.Empty`. The caller's honest answer is "this is not a statement",
not a stack trace on an import screen.

**Amounts are positive; the direction carries the sign.** That is how camt states it, and a host
that read the amount alone and forgot the indicator would book a bank charge as a customer
payment — silently, and in the direction that flatters the balance. Balances are the exception and
are signed, because an overdrawn account really is negative.

`BankStatement.Reconciles` says whether the entries account for the movement between the two
balances. False means something in the file was not understood, and on a bank statement that is
worth saying out loud: a payment silently missing from an import is a customer chased for money
they have already paid.
