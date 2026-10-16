# OpenVitals finances

Public accounts of the association behind [OpenVitals](https://github.com/OpenVitals-MTU).
Every month we publish what came in, what went out, and what is left, so that
anyone who donates can see where the money goes.

- **Reports:** [reports/](reports/) — one Markdown file per month, plus a yearly index.
- **Data:** [data/](data/) — the CSV files the reports are built from. The reports are
  generated; if a number looks wrong, the fix goes in the CSV.
- **How to donate:** [Liberapay](https://liberapay.com/openvitals.mtu/donate), or a
  direct transfer to the association's bank account (details on request; we do not
  publish the IBAN here).

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

## What we deliberately leave out

- Donor names, e-mails or anything that identifies a person. Stripe and the bank
  statement contain them; only aggregates reach this repository. Payees are companies,
  or `member reimbursement`.
- Bank account numbers, card numbers, statement PDFs.
- Receipts and invoices. Each expense row carries a receipt id (`R-2026-013`) that
  points to the treasurer's private archive, where members can ask to see it.

## How the month is produced

Nothing runs on anyone's laptop. On the 10th of each month the `monthly report` workflow:

1. pulls the previous month from **Stripe** (donations, fees, refunds, donor count,
   payouts, month-end balance) through a restricted read-only API key. Liberapay is a
   Stripe Connect platform, so its donations are among these charges and are told apart
   from direct ones;
2. pulls every booked transaction and the month-end balance from the **bank** through
   Enable Banking, a PSD2 account-information service that is free for accounts you link
   yourself and lists the Estonian banks under country `EE`;
3. reads the patron count from the **Liberapay** public profile for the Liberapay row;
4. classifies the bank rows with [config/rules.json](config/rules.json), writes the CSV
   rows under `data/`, renders the PDF and the index under `reports/`;
5. opens a pull request. The treasurer adds receipt ids and a note, and merges. The
   `verify` workflow re-checks the data and the reports on every pull request.

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

### Connecting the bank, and every 90 days after

The PSD2 consent must be approved in a browser and lasts at most 90 days (180 at some
banks), so it is done on a laptop with the .NET 9 SDK, never in the pipeline. The
application is registered once; the consent is repeated when it expires.

#### Once: the Enable Banking application

1. Sign in at <https://enablebanking.com/cp/> with the association's e-mail. There is no
   separate sign-up: the account is created on the first sign-in through a one-time link.
2. Under **API applications**, add a new application:
   - environment **production** (the sandbox only has fake banks);
   - a name, which the bank shows you when you approve the consent;
   - `https://github.com/OpenVitals-MTU/finances` as the allowed redirect URL.
3. Choose how the signing key is made. Either works with the tool:
   - **Generate in the browser** (simplest): the page creates the key pair and downloads the
     private key as `<application id>.pem`. The Control Panel never shows it again.
   - **Generate outside the browser**: make the key and a self-signed certificate yourself
     and upload the `.crt` on that page; the `.pem` stays with you.

     ```sh
     openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
       -keyout openvitals-finances.pem -out openvitals-finances.crt \
       -subj "/CN=openvitals-finances"
     ```
4. Copy `appsettings.local.example.json` to `appsettings.local.json` and fill in
   `ENABLEBANKING_APP_ID` (the application id from the Control Panel) and
   `ENABLEBANKING_PRIVATE_KEY_FILE` (the path to the `.pem`).
5. Create the repository secrets `ENABLEBANKING_APP_ID` and `ENABLEBANKING_PRIVATE_KEY`,
   the latter being the full **contents** of the `.pem` file, header and footer included.

The `.pem` is the one secret that cannot be re-downloaded. Keep it out of the repository;
if it is lost, register a new application.

#### Every 90 days: the consent and the session id

1. List the bank names Enable Banking knows in Estonia. The name must match exactly below.

   ```sh
   dotnet run --project src/Finances -- bank-institutions
   ```

2. Start the consent, with the bank's name and a number of days up to the maximum shown
   in step 1. This prints a link.

   ```sh
   dotnet run --project src/Finances -- bank-connect "LHV Pank" 90
   ```

3. Open the link, log in to the bank and allow read access to the account. The bank sends
   the browser back to the redirect URL with `?code=...` appended in the address bar.
   Copy that code; it is single-use and expires in minutes.
4. Turn the code into a session. This prints the session id and the accounts it covers.

   ```sh
   dotnet run --project src/Finances -- bank-session <the code>
   ```

5. Store the session id in `appsettings.local.json` as `ENABLEBANKING_SESSION_ID` and as
   the repository secret of the same name. Then confirm:

   ```sh
   dotnet run --project src/Finances -- bank-status
   ```

Free use is limited to accounts you link yourself and to a few API calls a day; one
monthly run makes three, so do not run the fetch twice on the same day. When the session
expires the monthly workflow fails on the bank step; that is the reminder. Repeat steps 2
to 5, update the secret, and re-run the workflow for that month from the Actions tab.

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
