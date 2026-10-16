# OpenVitals finances

Public accounts of the association behind [OpenVitals](https://github.com/OpenVitals-MTU).

- **Reports:** [reports/](reports/) — one Markdown file per month, plus a yearly index.
- **Data:** [data/](data/) — the CSV files the reports are built from. The reports are
  generated; if a number looks wrong, the fix goes in the CSV.
- **How to donate:** [Liberapay](https://liberapay.com/openvitals.mtu/donate), or a
  direct transfer to the association's bank account 

> **Legal entity:** _OpenVitals MTÜ_, a non-profit association (mittetulundusühing)
> registered in Estonia, registry code 80677610.

## What a monthly report contains

The PDF for each month, in `reports/<year>/`, has six parts:

1. **Summary** — net income, expenses, result for the month, closing balance, and the
   runway (how many months of the trailing year's average spending the balance covers).
2. **Income by source** — Liberapay, Stripe, direct bank transfers, other. For each
   source: number of donors, gross amount, processor fees, net received. Donor counts,
   never donor names.
3. **Donations by size** — how many donors gave under 5, 5 to 20, 20 to 50 and so on,
   and how much each group gave together. A band with fewer than three donors is merged
   with the next, so the spread is public but no donation can be traced to a person.
4. **Expenses** — every payment, with date, category, payee, description and amount.
   Categories are fixed (hosting, domains, store fees, devices, …) so months compare.
5. **Reconciliation** — opening balance + net income − expenses must equal the closing
   balance across the bank account and Stripe. A difference is printed, not hidden; it is
   usually a payout in transit.
6. **Notes** — a short hand-written paragraph from `data/notes/`: what the money was
   for, what is coming, anything unusual.

## How the month is produced

On the 10th of each month the `monthly report` workflow:

1. pulls the previous month from **Stripe** (donations, fees, refunds, donor count,
   payouts, month-end balance)
2. pulls every booked transaction and the month-end balance from the **bank** through
   Enable Banking
3. reads the patron count from the **Liberapay** public profile for the Liberapay row;
4. classifies the bank rows with [config/rules.json](config/rules.json), writes the CSV
   rows under `data/`, renders the PDF and the index under `reports/`;
5. opens a pull request.

A bank payment that matches no rule is written in category `other`, with the bank's
counterparty as payee and a description starting with `unclassified`. The pull request is
opened as a draft and `check` fails until a rule covers it. Add the rule, re-run the
workflow for that month from the Actions tab, done. Because the counterparty is taken
from the statement as-is, keep a rule with `"payee": "member reimbursement"` for every
person who is ever paid, so a name never reaches a branch.

The only thing that ever runs outside the pipeline is the bank consent below.

### Secrets and variables

Repository secrets (Settings → Secrets and variables → Actions):

| secret | from |
|---|---|
| `STRIPE_API_KEY` | a **restricted** key with read access to Balance, Balance transactions, Charges |
| `ENABLEBANKING_APP_ID` | the application registered in the Enable Banking Control Panel |
| `ENABLEBANKING_PRIVATE_KEY` | the full text of the `.pem` file the Control Panel downloads when you register the application |
| `ENABLEBANKING_SESSION_ID` | printed by `bank-session`, run locally, after the consent |

Everything that is not a secret lives in [appsettings.json](appsettings.json): the
organisation name on the PDF, currency, timezone, bank country and the Liberapay username.

On a laptop, copy `appsettings.local.example.json` to `appsettings.local.json` (gitignored)
and put the secrets there under the same names. An environment variable, when set, wins
over both files.

## Repository layout

```
data/                the source of truth: income/, expenses/, donations/, balances.csv, notes/
reports/             the monthly PDFs by year, and README.md as the index
config/rules.json    bank transaction → income source / expense category
src/Finances/        the .NET 9 console tool: fetch, report, check, and the local bank-* commands
appsettings.json     non-secret defaults; appsettings.local.json (gitignored) holds the secrets on a laptop
tests/Finances.Tests/  xunit tests with fake Stripe, bank and Liberapay answers
.github/workflows/   verify (every PR), monthly report (the 10th)
```

To run it anywhere with the .NET 9 SDK: `dotnet test`, then
`dotnet run --project src/Finances -- check` or `-- report`. `fetch` and the `bank-*`
commands need the secrets from `appsettings.local.json`.

Field definitions, allowed sources and categories: [data/README.md](data/README.md).

## Licence

Data and reports (`data/`, `reports/`): [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
Code (`src/`, `tests/`): MIT. See [LICENSE.md](LICENSE.md).
