# Data files

The monthly workflow creates these files; only `notes/` and the `receipt` column are
written by hand. All amounts are decimal numbers with a dot as separator and at most two decimals,
in the currency named on the row. Months are `YYYY-MM`. Nothing in these files may
identify a donor.

## `income/YYYY-MM.csv`

One row per source per month. Aggregate before writing; never one row per donor.

| column     | meaning                                                                |
|------------|------------------------------------------------------------------------|
| `source`   | one of `liberapay`, `stripe`, `bank_transfer`, `other`                 |
| `donors`   | number of distinct donors in the month (blank if unknown)              |
| `gross`    | what donors gave                                                       |
| `fees`     | processor fees withheld before the money reached us                    |
| `net`      | what reached our account; must equal `gross − fees`                    |
| `currency` | ISO 4217 code, normally `EUR`                                          |
| `reference`| where the number comes from, e.g. `Liberapay payday 2026-10-07`        |

Liberapay is a Stripe Connect platform: every Liberapay donation is a charge on our own
Stripe account, with the processor fee already taken, so `liberapay` and `stripe` are
both read from Stripe. The Stripe payouts that then reach the bank are internal
transfers, not income. Liberapay never holds our money, so it is not an account.

## `expenses/YYYY-MM.csv`

One row per outgoing payment, from the bank statement or the processor dashboard.

| column        | meaning                                                                  |
|---------------|--------------------------------------------------------------------------|
| `date`        | `YYYY-MM-DD`, the day the payment left the account                       |
| `category`    | one of the categories below                                              |
| `payee`       | who was paid (a company, or `member reimbursement` — never a member's name)  |
| `description` | what for, one line; starting with `unclassified` marks a payment no rule matched, and `check` fails while one exists |
| `amount`      | positive number                                                          |
| `currency`    | ISO 4217 code                                                            |
| `paid_from`   | one of `bank`, `stripe`, `card`                                          |
| `receipt`     | id of the receipt in the private archive, e.g. `R-2026-013`; blank if none |

Categories:

| category          | typical content                                                         |
|-------------------|-------------------------------------------------------------------------|
| `hosting`         | Fly.io for the landing page and the docs site, CI minutes               |
| `domains`         | domain registrations and renewals                                        |
| `store_fees`      | Google Play developer account, other store or signing fees              |
| `devices`         | phones, watches, scales and other hardware bought for testing           |
| `software`        | paid tools and subscriptions                                            |
| `bank_fees`       | account maintenance, transfer fees                                      |
| `processor_fees`  | Stripe or PayPal fees charged separately from a donation                |
| `legal`           | registry filings, accountancy, the annual report                        |
| `insurance`       | liability or other insurance                                            |
| `translation`     | paid translation or proofreading                                        |
| `development`     | paid development, design or testing work                                |
| `travel`          | travel to events, meetings                                              |
| `taxes`           | taxes paid by the association                                           |
| `other`           | anything else; explain it in the description                            |

## `donations/YYYY-MM.csv`

How much donors gave, in size bands, across all sources. A donor is counted once per month
with the sum of what they gave before fees. Bands start at 0–5, 5–20, 20–50, 50–100,
100–500 and 500+, and any band with fewer than 3 donors is merged into its neighbour until
every published band has at least 3, so no donation can be traced to a person. This is
what protects donors who transfer straight to the bank account.

| column     | meaning                                          |
|------------|--------------------------------------------------|
| `from`     | lower edge, inclusive                            |
| `to`       | upper edge, exclusive; blank for the top band    |
| `donors`   | donors whose month total fell in the band        |
| `total`    | what they gave together                          |
| `currency` | ISO 4217 code                                    |

## `balances.csv`

Closing balance of every account at the end of each month. One row per account per
month, including accounts whose balance is zero.

| column     | meaning                                              |
|------------|------------------------------------------------------|
| `month`    | `YYYY-MM`                                            |
| `account`  | one of `bank`, `stripe`                              |
| `balance`  | balance at 23:59 on the last day of the month        |
| `currency` | ISO 4217 code                                        |

The report uses the previous month's rows as the opening balance. The first month
published has no opening balance and is not reconciled.

## `notes/YYYY-MM.md`

Optional. Free-form Markdown appended to the month's report under "Notes". Not fetched; written by hand in the pull request. Say what
the spending was for, what is coming next month, and anything unusual (a refund, a
payout still in transit, a currency conversion).
